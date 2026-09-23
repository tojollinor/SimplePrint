using System.IO.Compression;
using System.Net.Sockets;
using SimplePrint.Common;

namespace SimplePrint.Client.Gui;

public sealed class MainForm : Form
{
    private readonly Label _agent = new() { AutoSize = true };
    private readonly Label _scan = new() { AutoSize = true };
    private readonly Label _selectedServer = new() { AutoSize = true, Text = "Kein Server fest ausgewählt" };
    private readonly DataGridView _serversGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    private readonly DataGridView _available = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    private readonly DataGridView _installed = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    private readonly Label _startupStatus = new() { AutoSize = true, Text = "Status: wird ermittelt ..." };
    private readonly CheckBox _startup = new() { Text = "Beim Autostart direkt im Infobereich starten", AutoSize = true, Checked = true };
    private readonly NotifyIcon _tray;
    private readonly ToolStripStatusLabel _operationStatus = new() { Text = "Bereit" };
    private readonly ToolStripProgressBar _operationProgress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 100 };
    private bool _publicNetworkWarningShown;

    private ClientConfig _config = new();
    private List<DiscoveredServer> _servers = [];
    private bool _allowExit;

    private sealed record AvailableTag(DiscoveredServer Server, DiscoveredPrinter Printer);

    public MainForm()
    {
        Text = "SimplePrint Client";
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
        headerText.Controls.Add(new Label { Text = "SimplePrint Client", AutoSize = true, Font = new Font(Font.FontFamily, 17, FontStyle.Bold), Margin = new Padding(0, 4, 0, 0) }, 0, 0);
        var status = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        status.Controls.AddRange([
            new Label { Text = "Client-Agent:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _agent,
            new Label { Text = "   Suche:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _scan
        ]);
        headerText.Controls.Add(status, 0, 1);
        top.Controls.Add(headerText, 1, 0);

        ConfigureGrids();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateServerTab());
        tabs.TabPages.Add(CreateAvailableTab());
        tabs.TabPages.Add(CreateInstalledTab());
        tabs.TabPages.Add(CreateSettingsTab());
        tabs.Selected += async (_, e) =>
        {
            if (e.TabPage?.Text == "Verfügbare Drucker")
                await RefreshAvailablePrintersAsync();
        };

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        bottom.Controls.Add(MakeButton("Server suchen", async (_, _) => await RefreshAllAsync()));
        bottom.Controls.Add(MakeButton("Verbindung testen", async (_, _) => await TestConnectionsAsync()));
        bottom.Controls.Add(MakeButton("Diagnosepaket", async (_, _) => await CreateDiagnosticsAsync()));
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
        trayMenu.Items.Add("SimplePrint Client öffnen", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Server suchen", null, async (_, _) => await RefreshAllAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Beenden", null, (_, _) => ExitApplication());
        _tray = new NotifyIcon
        {
            Icon = (Icon)trayIcon.Clone(),
            Text = "SimplePrint Client",
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

    private void ConfigureGrids()
    {
        _serversGrid.Columns.Add("selected", "Verwendet");
        _serversGrid.Columns.Add("server", "Server");
        _serversGrid.Columns.Add("ip", "IP-Adresse");
        _serversGrid.Columns.Add("gateway", "Gateway-Port");
        _serversGrid.Columns.Add("printers", "Drucker");

        _available.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "Verwenden", Width = 75, FillWeight = 20 });
        _available.Columns.Add("printer", "Verfügbarer Drucker");
        _available.Columns.Add("driver", "Treiberhinweis");
        _available.Columns.Add("status", "Status");
        _available.Columns["printer"]!.ReadOnly = true;
        _available.Columns["driver"]!.ReadOnly = true;
        _available.Columns["status"]!.ReadOnly = true;

        _installed.Columns.Add("printer", "Installierter Drucker");
        _installed.Columns.Add("server", "Server");
        _installed.Columns.Add("driver", "Treiber");
        _installed.Columns.Add("port", "Lokaler Proxy-Port");
    }

    private TabPage CreateServerTab()
    {
        var tab = new TabPage("Server");
        var infoPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(10), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        infoPanel.Controls.Add(new Label { AutoSize = true, Text = "Gefundene SimplePrint-Server:" });
        infoPanel.Controls.Add(_selectedServer);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("Diesen Server verwenden", (_, _) => UseSelectedServer()));
        buttons.Controls.Add(MakeButton("Serverauswahl aufheben", (_, _) => ClearServerSelection()));
        buttons.Controls.Add(MakeButton("Neu suchen", async (_, _) => await RefreshAllAsync()));

        tab.Controls.Add(_serversGrid);
        tab.Controls.Add(infoPanel);
        tab.Controls.Add(buttons);
        return tab;
    }

    private TabPage CreateAvailableTab()
    {
        var tab = new TabPage("Verfügbare Drucker");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(10),
            Text = "Hier werden ausschließlich die Drucker des fest ausgewählten Servers angezeigt."
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("Aktualisieren", async (_, _) => await RefreshAvailablePrintersAsync()));
        buttons.Controls.Add(MakeButton("Druckerauswahl speichern", async (_, _) => await SavePrinterSelectionAsync()));
        tab.Controls.Add(_available);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private TabPage CreateInstalledTab()
    {
        var tab = new TabPage("Installierte Drucker");
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("Drucker entfernen", async (_, _) => await RemoveSelectedAsync()));
        buttons.Controls.Add(MakeButton("Testseite", (_, _) => TestPage()));
        tab.Controls.Add(_installed);
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
            MaximumSize = new Size(840, 0),
            Text = "Der Client-Agent läuft unabhängig von der Oberfläche als automatischer Windows-Dienst. Der GUI-Autostart kann hier separat aktiviert oder deaktiviert werden."
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
        _config = JsonStore.LoadOrCreate(AppPaths.ClientConfig, () => new ClientConfig());

        var svc = await PowerShellRunner.RunAsync("(Get-Service -Name SimplePrintClient -ErrorAction SilentlyContinue).Status");
        _agent.Text = string.IsNullOrWhiteSpace(svc.StdOut) ? "nicht installiert" : svc.StdOut.Trim();
        _tray.Text = $"SimplePrint Client - {_agent.Text}";

        await RefreshAvailablePrintersAsync();
        RefreshInstalledGrid();
        RefreshStartupState();
    }

    private async Task RefreshAvailablePrintersAsync()
    {
        _config = JsonStore.LoadOrCreate(AppPaths.ClientConfig, () => new ClientConfig());

        var profile = await NetworkProfileHelper.GetStateAsync();
        if (profile.HasPublicProfile)
        {
            _servers = [];
            _scan.Text = "öffentliches Netzwerk";
            RefreshServerGrid();
            RefreshAvailableGrid();
            SetStatus("⚠ Öffentliches Netzwerk: Serversuche blockiert.");

            if (!_publicNetworkWarningShown)
            {
                _publicNetworkWarningShown = true;
                MessageBox.Show(
                    "Dieses Gerät befindet sich in einem öffentlichen Netzwerk.\r\n\r\nSimplePrint funktioniert nur in privaten oder Domänennetzwerken. Bitte stelle das Windows-Netzwerkprofil auf \"Privat\" oder verbinde das Gerät mit dem Domänennetzwerk.",
                    "SimplePrint Netzwerk",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return;
        }

        _publicNetworkWarningShown = false;
        SetBusy("Server werden gesucht …");
        _scan.Text = "suche ...";

        try
        {
            _servers = (await Discovery.DiscoverAsync(_config.DiscoveryPort, 1500, default, _config.ManualServer)).ToList();
            _scan.Text = _servers.Count == 0 ? "kein Server gefunden" : $"{_servers.Count} Server gefunden";
            RefreshServerGrid();
            RefreshAvailableGrid();
            SetStatus(_servers.Count == 0 ? "Kein Server gefunden." : $"✓ {_servers.Count} Server gefunden.");
        }
        catch (Exception ex)
        {
            _servers = [];
            _scan.Text = "Suche fehlgeschlagen";
            RefreshServerGrid();
            RefreshAvailableGrid();
            SetStatus("✗ Serversuche fehlgeschlagen.");
            MessageBox.Show(ex.Message, "Serversuche", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private void RefreshStartupState()
    {
        var enabled = StartupManager.IsSystemWideEnabled("SimplePrintClientGui");
        _startupStatus.Text = enabled ? "Status: Autostart aktiviert" : "Status: Autostart deaktiviert";
        _startup.Checked = enabled
            ? StartupManager.IsTrayModeEnabled("SimplePrintClientGui", true)
            : true;
    }

    private void RefreshServerGrid()
    {
        _serversGrid.Rows.Clear();
        foreach (var s in _servers)
        {
            var preferred = _config.PreferredServerId == s.Announcement.ServerId;
            var i = _serversGrid.Rows.Add(preferred ? "✓" : "", s.Announcement.ServerName, s.Address.ToString(), s.Announcement.GatewayPort, s.Announcement.Printers.Count);
            _serversGrid.Rows[i].Tag = s;
            if (preferred) _serversGrid.Rows[i].DefaultCellStyle.Font = new Font(_serversGrid.Font, FontStyle.Bold);
        }

        if (_config.PreferredServerId is Guid id)
        {
            var found = _servers.FirstOrDefault(x => x.Announcement.ServerId == id);
            _selectedServer.Text = found is null
                ? $"Fest ausgewählter Server: {id} (aktuell nicht gefunden)"
                : $"Fest ausgewählter Server: {found.Announcement.ServerName} ({found.Address})";
        }
        else
        {
            _selectedServer.Text = "Kein Server fest ausgewählt";
        }
    }

    private void RefreshAvailableGrid()
    {
        _available.Rows.Clear();
        if (_config.PreferredServerId is not Guid preferredId) return;

        var server = _servers.FirstOrDefault(s => s.Announcement.ServerId == preferredId);
        if (server is null) return;

        foreach (var p in server.Announcement.Printers)
        {
            var installed = _config.Mappings.Any(m => m.ServerId == server.Announcement.ServerId && m.PrinterId == p.Id);
            var i = _available.Rows.Add(installed, p.DisplayName, p.DriverName, p.Status);
            _available.Rows[i].Tag = new AvailableTag(server, p);
        }
    }

    private void RefreshInstalledGrid()
    {
        _installed.Rows.Clear();
        foreach (var m in _config.Mappings)
        {
            var i = _installed.Rows.Add(m.LocalPrinterName, m.ServerName, m.DriverName, m.LocalProxyPort);
            _installed.Rows[i].Tag = m.PortName;
        }
    }

    private void UseSelectedServer()
    {
        if (_serversGrid.SelectedRows.Count == 0)
        {
            MessageBox.Show("Bitte einen gefundenen Server markieren.");
            return;
        }

        var server = (DiscoveredServer)_serversGrid.SelectedRows[0].Tag;
        _config.PreferredServerId = server.Announcement.ServerId;
        JsonStore.Save(AppPaths.ClientConfig, _config);
        RefreshServerGrid();
        RefreshAvailableGrid();
        SetStatus($"✓ Server '{server.Announcement.ServerName}' ausgewählt.");
        MessageBox.Show($"'{server.Announcement.ServerName}' wird jetzt als fester SimplePrint-Server verwendet.");
    }

    private void ClearServerSelection()
    {
        _config.PreferredServerId = null;
        JsonStore.Save(AppPaths.ClientConfig, _config);
        RefreshServerGrid();
        RefreshAvailableGrid();
        SetStatus("✓ Serverauswahl aufgehoben.");
    }

    private async Task SavePrinterSelectionAsync()
    {
        if (_config.PreferredServerId is not Guid preferredId)
        {
            MessageBox.Show("Bitte zuerst im Reiter 'Server' einen Server fest auswählen.");
            return;
        }

        var server = _servers.FirstOrDefault(x => x.Announcement.ServerId == preferredId);
        if (server is null)
        {
            MessageBox.Show("Der ausgewählte Server wurde aktuell nicht gefunden.");
            return;
        }

        try
        {
            _available.EndEdit();
            var desired = new Dictionary<Guid, AvailableTag>();
            foreach (DataGridViewRow row in _available.Rows)
            {
                if (row.Tag is not AvailableTag tag) continue;
                if (row.Cells["use"].Value is bool use && use)
                    desired[tag.Printer.Id] = tag;
            }

            var existing = _config.Mappings.Where(m => m.ServerId == preferredId).ToList();

            foreach (var mapping in existing.Where(m => !desired.ContainsKey(m.PrinterId)).ToList())
            {
                await PrinterInstaller.RemoveAsync(mapping);
                _config.Mappings.Remove(mapping);
                JsonStore.Save(AppPaths.ClientConfig, _config);
            }

            foreach (var tag in desired.Values)
            {
                if (_config.Mappings.Any(m => m.ServerId == preferredId && m.PrinterId == tag.Printer.Id))
                    continue;

                await InstallPrinterAsync(tag);
            }

            await RefreshAllAsync();
            SetStatus("✓ Druckerauswahl wurde übernommen.");
            MessageBox.Show("Die Druckerauswahl wurde übernommen.", "SimplePrint");
        }
        catch (OperationCanceledException ex)
        {
            MessageBox.Show(ex.Message, "Administratorfreigabe abgebrochen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Druckerauswahl fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
            await RefreshAllAsync();
        }
    }

    private async Task InstallPrinterAsync(AvailableTag tag)
    {
        var drivers = await PrinterInstaller.GetDriverNamesAsync();
        if (drivers.Count == 0)
            throw new InvalidOperationException("Auf diesem PC wurden keine Druckertreiber gefunden.");

        var driver = drivers.FirstOrDefault(d => d.Equals(tag.Printer.DriverName, StringComparison.OrdinalIgnoreCase));
        if (driver is null) driver = ChooseDriver(drivers, tag.Printer.DriverName);
        if (driver is null) return;

        var localPort = AllocatePort();
        var shortServer = tag.Server.Announcement.ServerId.ToString("N")[..8];
        var shortPrinter = tag.Printer.Id.ToString("N")[..8];
        var portName = $"SimplePrint_{shortServer}_{shortPrinter}";
        var localName = UniqueLocalName($"{tag.Printer.DisplayName} (SimplePrint)");

        var mapping = new ClientPrinterMapping
        {
            ServerId = tag.Server.Announcement.ServerId,
            PrinterId = tag.Printer.Id,
            ServerName = tag.Server.Announcement.ServerName,
            PrinterDisplayName = tag.Printer.DisplayName,
            LocalPrinterName = localName,
            DriverName = driver,
            PortName = portName,
            LocalProxyPort = localPort
        };

        _config.Mappings.Add(mapping);
        JsonStore.Save(AppPaths.ClientConfig, _config);
        await Task.Delay(1600);

        try
        {
            await PrinterInstaller.InstallAsync(mapping);
        }
        catch
        {
            _config.Mappings.Remove(mapping);
            JsonStore.Save(AppPaths.ClientConfig, _config);
            throw;
        }
    }

    private string? ChooseDriver(List<string> drivers, string suggested)
    {
        using var dlg = new Form { Text = "Druckertreiber auswählen", Width = 700, Height = 210, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var info = new Label { Left = 20, Top = 15, Width = 640, Height = 40, Text = $"Der Server verwendet '{suggested}'. Wähle den passenden lokal installierten Treiber:" };
        var combo = new ComboBox { Left = 20, Top = 65, Width = 640, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(drivers.Cast<object>().ToArray());
        combo.SelectedIndex = 0;
        var ok = new Button { Text = "Verwenden", Left = 470, Top = 110, Width = 90, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Abbrechen", Left = 570, Top = 110, Width = 90, DialogResult = DialogResult.Cancel };
        dlg.Controls.AddRange([info, combo, ok, cancel]);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? combo.SelectedItem?.ToString() : null;
    }

    private int AllocatePort()
    {
        var used = _config.Mappings.Select(m => m.LocalProxyPort).ToHashSet();
        for (var p = _config.LocalPortStart; p <= _config.LocalPortEnd; p++)
        {
            if (used.Contains(p)) continue;
            try
            {
                var listener = new TcpListener(System.Net.IPAddress.Loopback, p);
                listener.Start();
                listener.Stop();
                return p;
            }
            catch
            {
            }
        }
        throw new InvalidOperationException("Kein freier lokaler SimplePrint-Port verfügbar.");
    }

    private string UniqueLocalName(string requested)
    {
        var names = _config.Mappings.Select(m => m.LocalPrinterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requested)) return requested;
        for (var i = 2; i < 100; i++)
        {
            var name = $"{requested} ({i})";
            if (!names.Contains(name)) return name;
        }
        return requested + " (SimplePrint)";
    }

    private async Task RemoveSelectedAsync()
    {
        if (_installed.SelectedRows.Count == 0)
        {
            MessageBox.Show("Bitte einen installierten Drucker auswählen.");
            return;
        }

        var portName = (string)_installed.SelectedRows[0].Tag;
        var mapping = _config.Mappings.First(m => m.PortName == portName);
        if (MessageBox.Show($"'{mapping.LocalPrinterName}' entfernen?", "Entfernen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try
        {
            SetBusy($"'{mapping.LocalPrinterName}' wird entfernt …");
            await PrinterInstaller.RemoveAsync(mapping);
            _config.Mappings.Remove(mapping);
            JsonStore.Save(AppPaths.ClientConfig, _config);
            await RefreshAllAsync();
            SetStatus($"✓ '{mapping.LocalPrinterName}' wurde entfernt.");
            MessageBox.Show($"'{mapping.LocalPrinterName}' wurde aus SimplePrint entfernt.", "SimplePrint");
        }
        catch (Exception ex)
        {
            SetStatus("✗ Drucker konnte nicht entfernt werden.");
            MessageBox.Show(ex.Message, "Entfernen fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task TestConnectionsAsync()
    {
        if (_config.PreferredServerId is not Guid preferredId)
        {
            MessageBox.Show("Es ist noch kein fester Server ausgewählt.", "Verbindung");
            return;
        }

        var server = _servers.FirstOrDefault(s => s.Announcement.ServerId == preferredId);
        if (server is null)
        {
            await RefreshAllAsync();
            server = _servers.FirstOrDefault(s => s.Announcement.ServerId == preferredId);
        }

        if (server is null)
        {
            MessageBox.Show("Der ausgewählte SimplePrint-Server wurde aktuell nicht gefunden.", "Verbindung");
            return;
        }

        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(server.Address, server.Announcement.GatewayPort, cts.Token);
            using var stream = tcp.GetStream();
            await stream.WriteAsync(Protocol.CreateGatewayHeader(Guid.Empty), cts.Token);
            var reply = new byte[6];
            var ok = await Protocol.ReadExactAsync(stream, reply, cts.Token) && System.Text.Encoding.ASCII.GetString(reply) == "SPROK1";
            SetStatus(ok ? "✓ Verbindung zum Server erfolgreich." : "✗ Server antwortet ungültig.");
            MessageBox.Show($"{server.Announcement.ServerName} ({server.Address}): {(ok ? "OK" : "keine gültige Antwort")}", "SimplePrint Verbindungstest");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{server.Announcement.ServerName} ({server.Address}): FEHLER\n{ex.Message}", "SimplePrint Verbindungstest", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TestPage()
    {
        if (_installed.SelectedRows.Count == 0) return;
        var portName = (string)_installed.SelectedRows[0].Tag;
        PrinterInstaller.PrintTestPage(_config.Mappings.First(m => m.PortName == portName).LocalPrinterName);
    }

    private async Task SetStartupAsync(bool enabled)
    {
        try
        {
            await StartupManager.SetSystemWideAsync(
                "SimplePrintClientGui",
                Application.ExecutablePath,
                enabled,
                _startup.Checked);

            RefreshStartupState();
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

    private void ShowAbout()
    {
        using var dlg = new AboutForm();
        dlg.ShowDialog(this);
    }

    private async Task CreateDiagnosticsAsync()
    {
        using var save = new SaveFileDialog { Filter = "ZIP-Datei|*.zip", FileName = $"SimplePrint-Client-Diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SetBusy("Diagnosepaket wird erstellt …");
            using var zip = ZipFile.Open(save.FileName, ZipArchiveMode.Create);
            if (File.Exists(AppPaths.ClientConfig)) zip.CreateEntryFromFile(AppPaths.ClientConfig, "config.json", CompressionLevel.Optimal);
            if (File.Exists(AppPaths.ClientLog)) zip.CreateEntryFromFile(AppPaths.ClientLog, "client.log", CompressionLevel.Optimal);

            var e = zip.CreateEntry("diagnostics.txt");
            await using (var stream = e.Open())
            await using (var w = new StreamWriter(stream))
            {
                await w.WriteAsync(await PrinterInstaller.GetDiagnosticsAsync());
            }

            var discovery = zip.CreateEntry("discovery.txt");
            await using (var stream = discovery.Open())
            await using (var dw = new StreamWriter(stream))
            {
                foreach (var server in _servers)
                    await dw.WriteLineAsync($"{server.Announcement.ServerName} {server.Address}:{server.Announcement.GatewayPort} id={server.Announcement.ServerId} printers={server.Announcement.Printers.Count}");
            }

            SetStatus("✓ Diagnosepaket wurde erstellt.");
            MessageBox.Show("Diagnosepaket wurde erstellt.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Diagnose", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
}
