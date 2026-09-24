using System.IO.Compression;
using System.Text.Json;
using SimplePrint.Common;

namespace SimplePrint.Server.Gui;

public sealed class MainForm : Form
{
    private sealed class PrinterChoice(LocalPrinterInfo info)
    {
        public LocalPrinterInfo Info { get; } = info;
        public override string ToString() => $"{Info.Name}   [{Info.DriverName}]   Status: {Info.PrinterStatus}";
    }

    private readonly Label _service = new() { AutoSize = true };
    private readonly Label _network = new() { AutoSize = true };
    private readonly Label _version = new() { AutoSize = true };
    private readonly CheckedListBox _printers = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
    private readonly DataGridView _firewall = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly DataGridView _clients = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly DataGridView _jobs = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private readonly Label _startupStatus = new() { AutoSize = true, Text = "Status: wird ermittelt ..." };
    private readonly CheckBox _startup = new() { Text = "Beim Autostart direkt im Infobereich starten", AutoSize = true, Checked = true };
    private readonly NotifyIcon _tray;
    private readonly ToolStripStatusLabel _operationStatus = new() { Text = "Bereit" };
    private readonly ToolStripProgressBar _operationProgress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 100 };
    private bool _publicNetworkWarningShown;
    private bool _updateCheckRunning;
    private string? _lastOfferedUpdate;
    private ServerConfig _config = new();
    private List<LocalPrinterInfo> _localPrinters = [];
    private bool _allowExit;

    public MainForm()
    {
        Text = "SimplePrint Server";
        Width = 620;
        Height = 500;
        MinimumSize = new Size(560, 440);
        StartPosition = FormStartPosition.CenterScreen;
        Branding.ApplyApplicationIcon(this);
        _printers.ItemCheck += Printers_ItemCheck;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 132, Padding = new Padding(10), ColumnCount = 2, RowCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(Branding.CreateGuiLogoBox(), 0, 0);

        var headerText = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 2, 0, 0) };
        headerText.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        headerText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headerText.Controls.Add(new Label { Text = "SimplePrint Server", AutoSize = true, Font = new Font(Font.FontFamily, 17, FontStyle.Bold), Margin = new Padding(0, 4, 0, 0) }, 0, 0);
        var status = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        status.Controls.AddRange([
            new Label { Text = "Dienst:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _service,
            new Label { Text = "   Netzwerk:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _network,
            new Label { Text = "   Version:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _version
        ]);
        headerText.Controls.Add(status, 0, 1);
        top.Controls.Add(headerText, 1, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreatePrinterTab());
        tabs.TabPages.Add(CreateClientsTab());
        tabs.TabPages.Add(CreateJobsTab());
        tabs.TabPages.Add(CreateFirewallTab());
        tabs.TabPages.Add(CreateSettingsTab());

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        bottom.Controls.Add(MakeButton("Diagnosepaket", async (_, _) => await CreateDiagnosticsAsync()));
        bottom.Controls.Add(MakeButton("Aktualisieren", async (_, _) => await RefreshAllAsync()));
        bottom.Controls.Add(MakeButton("Über", (_, _) => ShowAbout()));

        var statusStrip = new StatusStrip { SizingGrip = false };
        statusStrip.Items.Add(_operationStatus);
        statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
        statusStrip.Items.Add(_operationProgress);

        Controls.Add(tabs);
        Controls.Add(top);
        Controls.Add(bottom);
        Controls.Add(statusStrip);

        var trayIcon = Branding.LoadFixedIcon() ?? (Icon)SystemIcons.Application.Clone();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("SimplePrint Server öffnen", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Aktualisieren", null, async (_, _) => await RefreshAllAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Beenden", null, (_, _) => ExitApplication());
        _tray = new NotifyIcon
        {
            Icon = (Icon)trayIcon.Clone(),
            Text = "SimplePrint Server",
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) HideToTray();
        };
        FormClosing += (_, e) =>
        {
            if (_allowExit || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) return;
            e.Cancel = true;
            HideToTray();
        };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshClients(false);
            RefreshJobsGrid();
        };
        _refreshTimer.Start();
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        _updateTimer.Start();

        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _updateTimer.Stop();
            _updateTimer.Dispose();
            _tray.Dispose();
        };

        Shown += async (_, _) =>
        {
            await RefreshAllAsync();
            if (Program.StartInTray) HideToTray();
            _ = CheckForUpdatesAsync(false);
        };
    }

    private void Printers_ItemCheck(object? sender, ItemCheckEventArgs e)
    {
        // A row click only selects the printer. The check state changes exclusively
        // when the user actually clicks the checkbox glyph itself.
        if (Control.MouseButtons != MouseButtons.Left)
            return;

        var click = _printers.PointToClient(Cursor.Position);
        if (_printers.IndexFromPoint(click) != e.Index)
            return;

        var itemBounds = _printers.GetItemRectangle(e.Index);
        using var graphics = _printers.CreateGraphics();
        var glyphSize = CheckBoxRenderer.GetGlyphSize(
            graphics,
            System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);

        var glyphBounds = new Rectangle(
            itemBounds.Left + 1,
            itemBounds.Top + Math.Max(0, (itemBounds.Height - glyphSize.Height) / 2),
            glyphSize.Width,
            glyphSize.Height);

        if (!glyphBounds.Contains(click))
            e.NewValue = e.CurrentValue;
    }

    private TabPage CreatePrinterTab()
    {
        var tab = new TabPage("Drucker");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(10, 10, 10, 4),
            Text = "Alle lokal installierten Windows-Drucker werden automatisch erkannt. Haken setzen oder entfernen und anschließend speichern."
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("Auswahl speichern", async (_, _) => await SavePrinterSelectionAsync()));
        buttons.Controls.Add(MakeButton("Drucker neu einlesen", async (_, _) => await RefreshPrintersAsync()));
        buttons.Controls.Add(MakeButton("Druckbereitschaft", async (_, _) => await ShowSelectedPrinterHealthAsync()));
        buttons.Controls.Add(MakeButton("Warteschlange öffnen", (_, _) => OpenSelectedPrinterQueue()));
        buttons.Controls.Add(MakeButton("Testseite", (_, _) => TestSelectedPrinter()));
        tab.Controls.Add(_printers);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private TabPage CreateClientsTab()
    {
        _clients.Columns.Add("status", "Status");
        _clients.Columns.Add("client", "Client");
        _clients.Columns.Add("ip", "IP-Adresse");
        _clients.Columns.Add("version", "Version");
        _clients.Columns.Add("protocol", "Protokoll");
        _clients.Columns.Add("compat", "Kompatibilität");
        _clients.Columns.Add("printers", "Drucker");
        _clients.Columns.Add("seen", "Letzte Meldung");

        var tab = new TabPage("Clients");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(10),
            Text = "Client-Agenten melden sich automatisch. Nach 35 Sekunden ohne Lebenszeichen wird ein Client als offline angezeigt."
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("Clients aktualisieren", (_, _) => RefreshClients()));
        buttons.Controls.Add(MakeButton("Schnelldiagnose", (_, _) => ClientQuickDiagnosis()));
        buttons.Controls.Add(MakeButton("Offline-Client löschen", (_, _) => DeleteSelectedOfflineClient()));
        buttons.Controls.Add(MakeButton("Alle Offline löschen", (_, _) => DeleteAllOfflineClients()));
        tab.Controls.Add(_clients);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private TabPage CreateJobsTab()
    {
        _jobs.Columns.Add("id", "Job-ID");
        _jobs.Columns.Add("time", "Zeit");
        _jobs.Columns.Add("client", "Client");
        _jobs.Columns.Add("printer", "Drucker");
        _jobs.Columns.Add("status", "Status");
        _jobs.Columns.Add("bytes", "Bytes");
        _jobs.Columns.Add("spooler", "Spooler-ID");
        _jobs.Columns.Add("message", "Meldung");

        var tab = new TabPage("Druckaufträge");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(10),
            Text = "Serverstatus eines Druckauftrags: empfangen → Spooler → druckt → gedruckt/abgeschlossen oder Fehler."
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("Aktualisieren", (_, _) => RefreshJobsGrid()));
        buttons.Controls.Add(MakeButton("Abgeschlossene löschen", (_, _) => ClearCompletedJobs()));
        buttons.Controls.Add(MakeButton("Alle löschen", (_, _) => ClearAllJobs()));

        tab.Controls.Add(_jobs);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private TabPage CreateFirewallTab()
    {
        _firewall.Columns.Add("rule", "Regel");
        _firewall.Columns.Add("direction", "Richtung");
        _firewall.Columns.Add("protocol", "Protokoll / Port");
        _firewall.Columns.Add("profile", "Profile");
        _firewall.Columns.Add("state", "Status");
        _firewall.Columns.Add("details", "Details");

        var tab = new TabPage("Firewall");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(10),
            Text = "Der Server benötigt genau zwei eingehende Regeln. Änderungen werden nur nach einer UAC-Freigabe ausgeführt."
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("Firewall-Regeln anwenden", async (_, _) => await ApplyFirewallAsync()));
        buttons.Controls.Add(MakeButton("Firewall-Regeln zurücksetzen", async (_, _) => await RemoveFirewallAsync()));
        buttons.Controls.Add(MakeButton("Status aktualisieren", async (_, _) => await RefreshFirewallAsync()));
        tab.Controls.Add(_firewall);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private TabPage CreateSettingsTab()
    {
        var tab = new TabPage("Allgemein");
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(18)
        };

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Text = "Der PrintServer-Dienst startet automatisch mit Windows und läuft auch ohne Benutzeranmeldung. Der GUI-Autostart mit Tray-Symbol kann hier separat aktiviert oder deaktiviert werden."
        });
        panel.Controls.Add(new Label { Height = 10, AutoSize = false });
        panel.Controls.Add(_startupStatus);
        panel.Controls.Add(new Label { Height = 6, AutoSize = false });
        panel.Controls.Add(_startup);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(MakeButton("Autostart aktivieren", async (_, _) => await SetStartupAsync(true)));
        buttons.Controls.Add(MakeButton("Autostart deaktivieren", async (_, _) => await SetStartupAsync(false)));
        buttons.Controls.Add(MakeButton("Serverdienst neu starten", async (_, _) => await RestartServerServiceAsync()));
        buttons.Controls.Add(MakeButton("Nach Updates suchen", async (_, _) => await CheckForUpdatesAsync(true)));
        panel.Controls.Add(buttons);

        tab.Controls.Add(panel);
        return tab;
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckRunning)
        {
            if (manual)
                SetStatus("Updateprüfung läuft bereits …");
            return;
        }

        _updateCheckRunning = true;
        try
        {
            if (manual)
                SetBusy("GitHub Releases werden geprüft …");

            var current = typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            var update = await GitHubUpdateService.CheckAsync(current, SimplePrintComponent.Server);

            if (update is null)
            {
                if (manual)
                    SetStatus("✓ SimplePrint ist aktuell.");
                return;
            }

            if (!manual &&
                string.Equals(_lastOfferedUpdate, update.TagName, StringComparison.OrdinalIgnoreCase))
                return;

            _lastOfferedUpdate = update.TagName;
            SetStatus($"Update verfügbar: {update.TagName}");

            using var dialog = new UpdateForm(update);
            dialog.ShowDialog(Visible ? this : null);

            if (dialog.InstallerStarted)
                ExitApplication();
        }
        catch (Exception ex)
        {
            if (manual)
                SetStatus($"Updateprüfung fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            _updateCheckRunning = false;
            if (manual && _operationProgress.Visible)
                SetIdle();
        }
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32 };
        button.Click += click;
        return button;
    }

    private async Task RefreshAllAsync()
    {
        _config = JsonStore.LoadOrCreate(AppPaths.ServerConfig, () => new ServerConfig());
        await RefreshPrintersAsync();

        var svc = await PowerShellRunner.RunAsync("(Get-Service -Name SimplePrintServer -ErrorAction SilentlyContinue).Status");
        _service.Text = string.IsNullOrWhiteSpace(svc.StdOut) ? "nicht installiert" : svc.StdOut.Trim();

        var assemblyVersion = typeof(MainForm).Assembly.GetName().Version;
        _version.Text = assemblyVersion is null
            ? $"unbekannt · P{Protocol.Version}"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build} · P{Protocol.Version}";

        var net = await PowerShellRunner.RunAsync("if(Get-NetTCPConnection -LocalPort 45881 -State Listen -ErrorAction SilentlyContinue){'bereit'}else{'nicht bereit'}");
        _network.Text = string.IsNullOrWhiteSpace(net.StdOut) ? "unbekannt" : net.StdOut.Trim();

        _tray.Text = $"SimplePrint Server - {_service.Text}";
        RefreshStartupState();
        RefreshClients();
        RefreshJobsGrid();

        var profile = await NetworkProfileHelper.GetStateAsync();
        if (profile.HasPublicProfile)
        {
            _network.Text = "öffentlich (blockiert)";
            SetStatus("⚠ Öffentliches Netzwerk: SimplePrint ist nicht erreichbar.");

            if (!_publicNetworkWarningShown)
            {
                _publicNetworkWarningShown = true;
                MessageBox.Show(
                    "Dieses Gerät befindet sich in einem öffentlichen Netzwerk.\r\n\r\nSimplePrint funktioniert nur in privaten oder Domänennetzwerken. Bitte stelle das Windows-Netzwerkprofil auf \"Privat\" oder verbinde den Rechner mit dem Domänennetzwerk.",
                    "SimplePrint Netzwerk",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        else
        {
            _publicNetworkWarningShown = false;
        }

        await RefreshFirewallAsync();
        UpdateTrayStatus();
    }

    private async Task RefreshPrintersAsync()
    {
        try
        {
            SetBusy("Drucker werden eingelesen …");

            var allPrinters = await WinPrinterHelper.GetPrintersAsync();
            var blocked = allPrinters
                .Where(WinPrinterHelper.IsUnsafeSimplePrintLoop)
                .ToList();

            _localPrinters = allPrinters
                .Where(x => !WinPrinterHelper.IsUnsafeSimplePrintLoop(x))
                .ToList();

            var configChanged = false;

            foreach (var configured in _config.Printers)
            {
                var local = allPrinters.FirstOrDefault(
                    x => x.Name.Equals(configured.QueueName, StringComparison.OrdinalIgnoreCase));

                if (local is null)
                    continue;

                if (WinPrinterHelper.IsUnsafeSimplePrintLoop(local))
                {
                    if (configured.Enabled)
                    {
                        configured.Enabled = false;
                        configChanged = true;
                    }
                    continue;
                }

                if (!string.Equals(configured.DriverName, local.DriverName, StringComparison.Ordinal) ||
                    !string.Equals(configured.PortName, local.PortName, StringComparison.Ordinal) ||
                    !string.Equals(configured.TransportMode, local.TransportMode, StringComparison.Ordinal) ||
                    !string.Equals(configured.DirectAddress, local.DirectAddress, StringComparison.Ordinal) ||
                    !string.Equals(configured.DeviceUuid, local.DeviceUuid, StringComparison.Ordinal))
                {
                    configured.DriverName = local.DriverName;
                    configured.PortName = local.PortName;
                    configured.TransportMode = local.TransportMode;
                    configured.DirectAddress = local.DirectAddress;
                    configured.DeviceUuid = local.DeviceUuid;
                    configChanged = true;
                }
            }

            if (configChanged)
                JsonStore.Save(AppPaths.ServerConfig, _config);

            _printers.BeginUpdate();
            _printers.Items.Clear();

            foreach (var p in _localPrinters.OrderBy(x => x.Name))
            {
                var choice = new PrinterChoice(p);
                var index = _printers.Items.Add(choice);
                var enabled = _config.Printers.Any(
                    x => x.QueueName.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && x.Enabled);
                _printers.SetItemChecked(index, enabled);
            }

            SetStatus(
                blocked.Count == 0
                    ? $"✓ {_localPrinters.Count} Drucker eingelesen."
                    : $"✓ {_localPrinters.Count} Drucker eingelesen · {blocked.Count} SimplePrint-Schleife(n) blockiert.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Drucker konnten nicht gelesen werden", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _printers.EndUpdate();
        }
    }

    private async Task SavePrinterSelectionAsync()
    {
        try
        {
            var selected = _printers.CheckedItems.Cast<PrinterChoice>().Select(x => x.Info).ToList();
            var old = _config.Printers.ToDictionary(x => x.QueueName, StringComparer.OrdinalIgnoreCase);
            var next = new List<SharedPrinterConfig>();

            foreach (var p in selected)
            {
                if (old.TryGetValue(p.Name, out var existing))
                {
                    existing.DisplayName = p.Name;
                    existing.DriverName = p.DriverName;
                    existing.PortName = p.PortName;
                    existing.TransportMode = p.TransportMode;
                    existing.DirectAddress = p.DirectAddress;
                    existing.DeviceUuid = p.DeviceUuid;
                    existing.Enabled = true;
                    next.Add(existing);
                }
                else
                {
                    next.Add(new SharedPrinterConfig
                    {
                        QueueName = p.Name,
                        DisplayName = p.Name,
                        DriverName = p.DriverName,
                        PortName = p.PortName,
                        TransportMode = p.TransportMode,
                        DirectAddress = p.DirectAddress,
                        DeviceUuid = p.DeviceUuid,
                        Enabled = true
                    });
                }
            }

            _config.Printers = next;
            JsonStore.Save(AppPaths.ServerConfig, _config);
            SetStatus($"✓ {next.Count} Druckerfreigabe(n) gespeichert.");
            MessageBox.Show($"{next.Count} Druckerfreigabe(n) gespeichert. Die Clients sehen die Änderung automatisch.", "SimplePrint");
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Speichern fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TestSelectedPrinter()
    {
        if (_printers.SelectedItem is not PrinterChoice choice)
        {
            MessageBox.Show("Bitte zuerst einen Drucker markieren.");
            return;
        }
        WinPrinterHelper.PrintTestPage(choice.Info.Name);
        SetStatus($"✓ Testseite für '{choice.Info.Name}' wurde gestartet.");
    }

    private async Task ShowSelectedPrinterHealthAsync()
    {
        if (_printers.SelectedItem is not PrinterChoice choice)
        {
            MessageBox.Show("Bitte zuerst einen Drucker markieren.");
            return;
        }

        SetBusy($"Druckbereitschaft von '{choice.Info.Name}' wird geprüft …");

        try
        {
            var configured = _config.Printers.FirstOrDefault(
                x => x.QueueName.Equals(choice.Info.Name, StringComparison.OrdinalIgnoreCase));

            var health = await WinPrinterHelper.ProbePrinterAsync(
                choice.Info.Name,
                configured?.Id ?? Guid.Empty);

            using var dialog = new PrinterHealthForm(health);
            SetStatus(
                health.Level == "Green"
                    ? "✓ Drucker ist bereit."
                    : health.Level == "Red"
                        ? "✗ Drucker ist nicht druckbereit."
                        : "⚠ Druckerstatus enthält Hinweise.");
            dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Druckbereitschaft konnte nicht geprüft werden.");
            MessageBox.Show(
                ex.Message,
                "Druckbereitschaft",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private void OpenSelectedPrinterQueue()
    {
        if (_printers.SelectedItem is not PrinterChoice choice)
        {
            MessageBox.Show("Bitte zuerst einen Drucker markieren.");
            return;
        }

        try
        {
            WinPrinterHelper.OpenQueue(choice.Info.Name);
            SetStatus($"✓ Warteschlange '{choice.Info.Name}' geöffnet.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Warteschlange",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void RefreshClients(bool updateStatus = true)
    {
        List<ClientPresence> clients;
        try
        {
            clients = JsonStore.LoadOrCreate(AppPaths.ServerClients, () => new List<ClientPresence>());
        }
        catch
        {
            clients = [];
        }

        _clients.Rows.Clear();
        var now = DateTimeOffset.Now;
        foreach (var client in clients.OrderBy(x => x.ClientName, StringComparer.CurrentCultureIgnoreCase))
        {
            var online = now - client.LastSeen <= TimeSpan.FromSeconds(35);
            var compatible = client.ProtocolVersion == Protocol.Version;
            var row = _clients.Rows.Add(
                online ? "Online" : "Offline",
                client.ClientName,
                client.Address,
                client.AgentVersion,
                client.ProtocolVersion == 0 ? "unbekannt" : client.ProtocolVersion.ToString(),
                compatible ? "OK" : $"benötigt P{Protocol.Version}",
                client.InstalledPrinterCount,
                client.LastSeen.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"));

            _clients.Rows[row].Tag = client;

            if (!online)
                _clients.Rows[row].DefaultCellStyle.ForeColor = SystemColors.GrayText;
        }

        if (updateStatus)
            SetStatus($"✓ {clients.Count(x => now - x.LastSeen <= TimeSpan.FromSeconds(35))} Client(s) online.");
    }

    private void RefreshJobsGrid()
    {
        List<PrintJobRecord> jobs;
        try
        {
            jobs = JsonStore.LoadOrCreate(AppPaths.ServerJobs, () => new List<PrintJobRecord>());
        }
        catch
        {
            jobs = [];
        }

        _jobs.Rows.Clear();

        foreach (var job in jobs.OrderByDescending(x => x.CreatedAt).Take(150))
        {
            var row = _jobs.Rows.Add(
                job.JobId.ToString("N")[..8],
                job.CreatedAt.ToLocalTime().ToString("dd.MM. HH:mm:ss"),
                job.ClientName,
                job.PrinterName,
                job.Status,
                job.Bytes == 0 ? "" : job.Bytes.ToString("N0"),
                job.SpoolerJobId?.ToString() ?? "",
                job.Message);

            _jobs.Rows[row].Tag = job.JobId;

            if (job.Status.Equals("Fehler", StringComparison.OrdinalIgnoreCase))
                _jobs.Rows[row].DefaultCellStyle.ForeColor = Color.DarkRed;
        }
    }

    private void ClearCompletedJobs()
    {
        var jobs = JsonStore.LoadOrCreate(
            AppPaths.ServerJobs,
            () => new List<PrintJobRecord>());

        var completed = new HashSet<string>(
            ["Gedruckt", "Abgeschlossen", "Ignoriert", "Fehler"],
            StringComparer.OrdinalIgnoreCase);

        var count = jobs.RemoveAll(x => completed.Contains(x.Status));
        JsonStore.Save(AppPaths.ServerJobs, jobs);
        RefreshJobsGrid();
        SetStatus($"✓ {count} abgeschlossene Druckaufträge gelöscht.");
    }

    private void ClearAllJobs()
    {
        if (MessageBox.Show(
                "Die gesamte gespeicherte Druckauftragshistorie löschen?",
                "Druckaufträge löschen",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        JsonStore.Save(AppPaths.ServerJobs, new List<PrintJobRecord>());
        RefreshJobsGrid();
        SetStatus("✓ Druckauftragshistorie gelöscht.");
    }

    private void DeleteSelectedOfflineClient()
    {
        if (_clients.SelectedRows.Count == 0 ||
            _clients.SelectedRows[0].Tag is not ClientPresence client)
        {
            MessageBox.Show("Bitte zuerst einen Client markieren.");
            return;
        }

        if (DateTimeOffset.Now - client.LastSeen <= TimeSpan.FromSeconds(35))
        {
            MessageBox.Show("Online-Clients können nicht gelöscht werden. Sobald der Client offline ist, kann sein gespeicherter Eintrag entfernt werden.");
            return;
        }

        if (MessageBox.Show(
                $"Offline-Client '{client.ClientName}' aus der Liste löschen?",
                "Client löschen",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var clients = JsonStore.LoadOrCreate(AppPaths.ServerClients, () => new List<ClientPresence>());
        clients.RemoveAll(x => x.ClientId == client.ClientId);
        JsonStore.Save(AppPaths.ServerClients, clients);
        RefreshClients();
        SetStatus($"✓ Offline-Client '{client.ClientName}' gelöscht.");
    }

    private void DeleteAllOfflineClients()
    {
        var clients = JsonStore.LoadOrCreate(AppPaths.ServerClients, () => new List<ClientPresence>());
        var now = DateTimeOffset.Now;
        var count = clients.RemoveAll(x => now - x.LastSeen > TimeSpan.FromSeconds(35));

        if (count == 0)
        {
            MessageBox.Show("Es sind keine Offline-Clients gespeichert.");
            return;
        }

        JsonStore.Save(AppPaths.ServerClients, clients);
        RefreshClients();
        SetStatus($"✓ {count} Offline-Client(s) gelöscht.");
    }

    private void ClientQuickDiagnosis()
    {
        if (_clients.SelectedRows.Count == 0 ||
            _clients.SelectedRows[0].Tag is not ClientPresence client)
        {
            MessageBox.Show("Bitte zuerst einen Client markieren.");
            return;
        }

        var now = DateTimeOffset.Now;
        var online = now - client.LastSeen <= TimeSpan.FromSeconds(35);

        List<PrintJobRecord> jobs;
        try
        {
            jobs = JsonStore.LoadOrCreate(AppPaths.ServerJobs, () => new List<PrintJobRecord>());
        }
        catch
        {
            jobs = [];
        }

        var recent = jobs
            .Where(x => x.ClientId == client.ClientId ||
                        (!string.IsNullOrWhiteSpace(client.ClientName) &&
                         x.ClientName.Equals(client.ClientName, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.CreatedAt)
            .Take(5)
            .ToList();

        var lines = new List<string>
        {
            $"Client: {client.ClientName}",
            $"IP-Adresse: {client.Address}",
            $"Status: {(online ? "Online" : "Offline")}",
            $"Letztes Lebenszeichen: {client.LastSeen.ToLocalTime():dd.MM.yyyy HH:mm:ss}",
            $"Agent-Version: {client.AgentVersion}",
            $"Protokoll: {client.ProtocolVersion} ({(client.ProtocolVersion == Protocol.Version ? "kompatibel" : $"Server benötigt {Protocol.Version}")})",
            $"Installierte SimplePrint-Drucker: {client.InstalledPrinterCount}",
            "",
            "Letzte Druckaufträge:"
        };

        if (recent.Count == 0)
        {
            lines.Add("Keine Druckaufträge gespeichert.");
        }
        else
        {
            foreach (var job in recent)
                lines.Add($"{job.JobId.ToString("N")[..8]} · {job.CreatedAt.ToLocalTime():HH:mm:ss} · {job.PrinterName} · {job.Status} · {job.Message}");
        }

        SetStatus("✓ Client-Schnelldiagnose erstellt.");

        MessageBox.Show(
            string.Join(Environment.NewLine, lines),
            "SimplePrint Client-Schnelldiagnose",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task RefreshFirewallAsync()
    {
        var state = await WinPrinterHelper.GetFirewallStateAsync();

        _firewall.Rows.Clear();
        _firewall.Rows.Add(
            "SimplePrint Discovery",
            "Eingehend",
            "UDP 45880",
            "Privat/Domäne · LocalSubnet",
            state.Discovery.Correct ? "korrekt" : state.Discovery.Exists ? "fehlerhaft" : "fehlt",
            state.Discovery.Detail);

        _firewall.Rows.Add(
            "SimplePrint Print Gateway",
            "Eingehend",
            "TCP 45881",
            "Privat/Domäne · LocalSubnet",
            state.Gateway.Correct ? "korrekt" : state.Gateway.Exists ? "fehlerhaft" : "fehlt",
            state.Gateway.Detail);

        SetStatus(
            state.Discovery.Correct && state.Gateway.Correct
                ? "✓ Firewall vollständig verifiziert."
                : "⚠ Firewall ist nicht vollständig korrekt.");
    }

    private async Task ApplyFirewallAsync()
    {
        try
        {
            SetBusy("Firewall-Regeln werden angewendet und verifiziert …");
            await WinPrinterHelper.ApplyFirewallAsync();

            var state = await WinPrinterHelper.GetFirewallStateAsync();
            if (!state.Discovery.Correct || !state.Gateway.Correct)
            {
                throw new InvalidOperationException(
                    "Windows hat die Firewallregeln nicht wie erforderlich übernommen." +
                    Environment.NewLine + Environment.NewLine +
                    "Discovery: " + state.Discovery.Detail +
                    Environment.NewLine +
                    "Gateway: " + state.Gateway.Detail);
            }

            await RefreshFirewallAsync();
            SetStatus("✓ Firewall-Regeln angewendet und verifiziert.");
            MessageBox.Show(
                "Beide SimplePrint-Firewallregeln wurden angewendet und erfolgreich verifiziert.",
                "Firewall",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Firewall-Regeln konnten nicht korrekt angewendet werden.");
            MessageBox.Show(ex.Message, "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task RemoveFirewallAsync()
    {
        if (MessageBox.Show("Beide SimplePrint-Firewallregeln entfernen?", "Firewall zurücksetzen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            SetBusy("Firewall-Regeln werden entfernt …");
            await WinPrinterHelper.RemoveFirewallAsync();
            await RefreshFirewallAsync();
            SetStatus("✓ Firewall-Regeln wurden entfernt.");
            MessageBox.Show("Die SimplePrint-Firewallregeln wurden entfernt.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshStartupState()
    {
        var enabled = StartupManager.IsSystemWideEnabled("SimplePrintServerGui");
        _startupStatus.Text = enabled ? "Status: Autostart aktiviert" : "Status: Autostart deaktiviert";
        _startup.Checked = enabled
            ? StartupManager.IsTrayModeEnabled("SimplePrintServerGui", true)
            : true;
    }

    private async Task RestartServerServiceAsync()
    {
        try
        {
            SetBusy("SimplePrint-Serverdienst wird neu gestartet …");
            await WinPrinterHelper.RestartServerServiceAsync();
            await Task.Delay(500);
            await RefreshAllAsync();
            SetStatus("✓ SimplePrint-Serverdienst läuft wieder.");
        }
        catch (Exception ex)
        {
            SetStatus("✗ Serverdienst konnte nicht neu gestartet werden.");
            MessageBox.Show(
                ex.Message,
                "Serverdienst",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task SetStartupAsync(bool enabled)
    {
        try
        {
            SetBusy(enabled ? "Autostart wird aktiviert …" : "Autostart wird deaktiviert …");
            await StartupManager.SetSystemWideAsync(
                "SimplePrintServerGui",
                Application.ExecutablePath,
                enabled,
                _startup.Checked);

            RefreshStartupState();
            SetStatus(enabled ? "✓ Autostart aktiviert." : "✓ Autostart deaktiviert.");
            MessageBox.Show(
                enabled ? "Systemweiter GUI-Autostart wurde aktiviert." : "Systemweiter GUI-Autostart wurde deaktiviert.",
                "Autostart");
        }
        catch (OperationCanceledException ex)
        {
            MessageBox.Show(ex.Message, "Autostart", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Autostart", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CreateDiagnosticsAsync()
    {
        using var save = new SaveFileDialog { Filter = "ZIP-Datei|*.zip", FileName = $"SimplePrint-Server-Diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SetBusy("Diagnosepaket wird erstellt …");
            using var zip = ZipFile.Open(save.FileName, ZipArchiveMode.Create);
            AddFile(zip, AppPaths.ServerConfig, "config.json");
            AddFile(zip, AppPaths.ServerLog, "server.log");
            AddFile(zip, AppPaths.ServerClients, "clients.json");
            AddFile(zip, AppPaths.ServerJobs, "jobs.json");
            var e = zip.CreateEntry("diagnostics.txt");
            using (var w = new StreamWriter(e.Open()))
                await w.WriteAsync(await WinPrinterHelper.GetDiagnosticsAsync());

            var healthResults = new List<PrinterHealthStatus>();
            foreach (var printer in _config.Printers.Where(x => x.Enabled))
            {
                try
                {
                    var health = await WinPrinterHelper.ProbePrinterAsync(
                        printer.QueueName,
                        printer.Id);
                    health.PrinterName = string.IsNullOrWhiteSpace(printer.DisplayName)
                        ? printer.QueueName
                        : printer.DisplayName;
                    healthResults.Add(health);
                }
                catch (Exception ex)
                {
                    healthResults.Add(new PrinterHealthStatus
                    {
                        PrinterId = printer.Id,
                        PrinterName = printer.DisplayName,
                        QueueName = printer.QueueName,
                        Level = "Red",
                        Summary = "Diagnose fehlgeschlagen",
                        Warnings = [ex.Message]
                    });
                }
            }

            var healthEntry = zip.CreateEntry("printer-health.json");
            using (var hw = new StreamWriter(healthEntry.Open()))
                await hw.WriteAsync(JsonSerializer.Serialize(
                    healthResults,
                    JsonStore.Options));

            SetStatus("✓ Diagnosepaket wurde erstellt.");
            MessageBox.Show("Diagnosepaket wurde erstellt.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Diagnose", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowAbout()
    {
        using var dlg = new AboutForm();
        dlg.ShowDialog(this);
    }

    private void SetBusy(string text)
    {
        _operationStatus.Text = text;
        _operationProgress.Visible = true;
        Cursor = Cursors.Default;
    }

    private void SetIdle()
    {
        _operationProgress.Visible = false;
        Cursor = Cursors.Default;
    }

    private void SetStatus(string text)
    {
        SetIdle();
        _operationStatus.Text = text;
    }

    private void UpdateTrayStatus()
    {
        var serviceOk = _service.Text.Equals("Running", StringComparison.OrdinalIgnoreCase);
        var networkOk = _network.Text.Equals("bereit", StringComparison.OrdinalIgnoreCase);
        var hasPrinter = _config.Printers.Any(x => x.Enabled);

        var level = !serviceOk || !networkOk
            ? "Red"
            : hasPrinter
                ? "Green"
                : "Yellow";

        var next = Branding.CreateStatusIcon(level);
        if (next is null) return;

        var previous = _tray.Icon;
        _tray.Icon = next;
        previous?.Dispose();

        _tray.Text = level switch
        {
            "Green" => "SimplePrint Server - bereit",
            "Yellow" => "SimplePrint Server - keine Druckerfreigabe",
            _ => "SimplePrint Server - Problem"
        };
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _tray.Visible = false;
        Close();
    }

    private static void AddFile(ZipArchive zip, string path, string name)
    {
        if (File.Exists(path)) zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
    }
}