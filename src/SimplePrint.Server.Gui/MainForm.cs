using System.IO.Compression;
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
    private readonly CheckedListBox _printers = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
    private readonly DataGridView _firewall = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly DataGridView _clients = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly Label _startupStatus = new() { AutoSize = true, Text = "Status: wird ermittelt ..." };
    private readonly CheckBox _startup = new() { Text = "Beim Autostart direkt im Infobereich starten", AutoSize = true, Checked = true };
    private readonly NotifyIcon _tray;
    private readonly ToolStripStatusLabel _operationStatus = new() { Text = "Bereit" };
    private readonly ToolStripProgressBar _operationProgress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 100 };
    private bool _publicNetworkWarningShown;
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
            new Label { Text = "   Netzwerk:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _network
        ]);
        headerText.Controls.Add(status, 0, 1);
        top.Controls.Add(headerText, 1, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreatePrinterTab());
        tabs.TabPages.Add(CreateClientsTab());
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
        FormClosed += (_, _) => _tray.Dispose();

        Shown += async (_, _) =>
        {
            await RefreshAllAsync();
            if (Program.StartInTray) HideToTray();
        };
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
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("Clients aktualisieren", (_, _) => RefreshClients()));
        tab.Controls.Add(_clients);
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
        panel.Controls.Add(buttons);

        tab.Controls.Add(panel);
        return tab;
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

        var net = await PowerShellRunner.RunAsync("if(Get-NetTCPConnection -LocalPort 45881 -State Listen -ErrorAction SilentlyContinue){'bereit'}else{'nicht bereit'}");
        _network.Text = string.IsNullOrWhiteSpace(net.StdOut) ? "unbekannt" : net.StdOut.Trim();

        _tray.Text = $"SimplePrint Server - {_service.Text}";
        RefreshStartupState();
        RefreshClients();

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
    }

    private async Task RefreshPrintersAsync()
    {
        try
        {
            SetBusy("Drucker werden eingelesen …");
            _localPrinters = await WinPrinterHelper.GetPrintersAsync();
            _printers.BeginUpdate();
            _printers.Items.Clear();
            foreach (var p in _localPrinters.OrderBy(x => x.Name))
            {
                var choice = new PrinterChoice(p);
                var index = _printers.Items.Add(choice);
                var enabled = _config.Printers.Any(x => x.QueueName.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && x.Enabled);
                _printers.SetItemChecked(index, enabled);
            }
            SetStatus($"✓ {_localPrinters.Count} Drucker eingelesen.");
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

    private void RefreshClients()
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
            var row = _clients.Rows.Add(
                online ? "Online" : "Offline",
                client.ClientName,
                client.Address,
                client.AgentVersion,
                client.InstalledPrinterCount,
                client.LastSeen.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"));

            if (!online)
                _clients.Rows[row].DefaultCellStyle.ForeColor = SystemColors.GrayText;
        }

        SetStatus($"✓ {clients.Count(x => now - x.LastSeen <= TimeSpan.FromSeconds(35))} Client(s) online.");
    }

    private async Task RefreshFirewallAsync()
    {
        var state = await WinPrinterHelper.GetFirewallStateAsync();
        _firewall.Rows.Clear();
        _firewall.Rows.Add("SimplePrint Discovery", "Eingehend", "UDP 45880", "Privat/Domäne · LocalSubnet", state.Discovery ? "aktiv" : "fehlt / inaktiv");
        _firewall.Rows.Add("SimplePrint Print Gateway", "Eingehend", "TCP 45881", "Privat/Domäne · LocalSubnet", state.Gateway ? "aktiv" : "fehlt / inaktiv");
        SetStatus("✓ Firewall-Status aktualisiert.");
    }

    private async Task ApplyFirewallAsync()
    {
        try
        {
            SetBusy("Firewall-Regeln werden angewendet …");
            await WinPrinterHelper.ApplyFirewallAsync();
            await RefreshFirewallAsync();
            SetStatus("✓ Firewall-Regeln wurden angewendet.");
            MessageBox.Show("Die benötigten SimplePrint-Firewallregeln wurden angewendet.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            var e = zip.CreateEntry("diagnostics.txt");
            using var w = new StreamWriter(e.Open());
            await w.WriteAsync(await WinPrinterHelper.GetDiagnosticsAsync());
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
        UseWaitCursor = true;
        Application.DoEvents();
    }

    private void SetIdle()
    {
        _operationProgress.Visible = false;
        UseWaitCursor = false;
    }

    private void SetStatus(string text)
    {
        SetIdle();
        _operationStatus.Text = text;
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
