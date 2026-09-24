using System.IO.Compression;
using System.Net.Sockets;
using System.Text.Json;
using SimplePrint.Common;

namespace SimplePrint.Client.Gui;

public sealed class MainForm : Form
{
    private readonly Label _agent = new() { AutoSize = true };
    private readonly Label _scan = new() { AutoSize = true };
    private readonly Label _version = new() { AutoSize = true };
    private readonly Label _selectedServer = new() { AutoSize = true, Text = "Kein Server fest ausgewählt" };
    private readonly DataGridView _serversGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    private readonly DataGridView _available = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    private readonly DataGridView _installed = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    private readonly DataGridView _jobs = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    private readonly System.Windows.Forms.Timer _jobTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private readonly Label _startupStatus = new() { AutoSize = true, Text = "Status: wird ermittelt ..." };
    private readonly CheckBox _startup = new() { Text = "Beim Autostart direkt im Infobereich starten", AutoSize = true, Checked = true };
    private readonly NotifyIcon _tray;
    private readonly ToolStripStatusLabel _operationStatus = new() { Text = "Bereit" };
    private readonly ToolStripProgressBar _operationProgress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 100 };
    private bool _publicNetworkWarningShown;
    private bool _updateCheckRunning;
    private string? _lastOfferedUpdate;

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
            new Label { Text = "   Suche:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _scan,
            new Label { Text = "   Version:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, _version
        ]);
        headerText.Controls.Add(status, 0, 1);
        top.Controls.Add(headerText, 1, 0);

        ConfigureGrids();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateServerTab());
        tabs.TabPages.Add(CreateAvailableTab());
        tabs.TabPages.Add(CreateInstalledTab());
        tabs.TabPages.Add(CreateJobsTab());
        tabs.TabPages.Add(CreateSettingsTab());
        tabs.Selected += async (_, e) =>
        {
            if (e.TabPage?.Text == "Verfügbare Drucker")
                await RefreshAvailablePrintersAsync();
            else if (e.TabPage?.Text == "Druckaufträge")
                RefreshJobsGrid();
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
        _jobTimer.Tick += (_, _) => RefreshJobsGrid();
        _jobTimer.Start();
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        _updateTimer.Start();

        FormClosed += (_, _) =>
        {
            _jobTimer.Stop();
            _jobTimer.Dispose();
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

    private void ConfigureGrids()
    {
        _serversGrid.Columns.Add("selected", "Verwendet");
        _serversGrid.Columns.Add("server", "Server");
        _serversGrid.Columns.Add("ip", "IP-Adresse");
        _serversGrid.Columns.Add("gateway", "Gateway-Port");
        _serversGrid.Columns.Add("version", "Version");
        _serversGrid.Columns.Add("protocol", "Protokoll");
        _serversGrid.Columns.Add("compat", "Kompatibilität");
        _serversGrid.Columns.Add("printers", "Drucker");

        _available.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "Verwenden", Width = 75, FillWeight = 20, ReadOnly = true });
        _available.Columns.Add("printer", "Verfügbarer Drucker");
        _available.Columns.Add("driver", "Treiberhinweis");
        _available.Columns.Add("status", "Status");
        _available.Columns["printer"]!.ReadOnly = true;
        _available.Columns["driver"]!.ReadOnly = true;
        _available.Columns["status"]!.ReadOnly = true;
        _available.CellContentClick += AvailablePrinterCheckBoxClicked;

        _installed.Columns.Add("printer", "Installierter Drucker");
        _installed.Columns.Add("server", "Server");
        _installed.Columns.Add("driver", "Treiber");
        _installed.Columns.Add("port", "Druckpfad");

        _jobs.Columns.Add("id", "Job-ID");
        _jobs.Columns.Add("time", "Zeit");
        _jobs.Columns.Add("printer", "Drucker");
        _jobs.Columns.Add("server", "Server");
        _jobs.Columns.Add("status", "Status");
        _jobs.Columns.Add("bytes", "Bytes");
        _jobs.Columns.Add("message", "Meldung");

        _serversGrid.Cursor = Cursors.Default;
        _available.Cursor = Cursors.Default;
        _installed.Cursor = Cursors.Default;
        _jobs.Cursor = Cursors.Default;
    }

    private void AvailablePrinterCheckBoxClicked(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != _available.Columns["use"]!.Index)
            return;

        var row = _available.Rows[e.RowIndex];
        var cell = row.Cells["use"];
        cell.Value = !(cell.Value is bool value && value);
        row.Selected = true;
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
        buttons.Controls.Add(MakeButton("Druckbereitschaft", async (_, _) => await ShowInstalledPrinterHealthAsync()));
        buttons.Controls.Add(MakeButton("Testseite", (_, _) => TestPage()));
        buttons.Controls.Add(MakeButton("Schnelldiagnose", async (_, _) => await QuickDiagnosisAsync()));
        tab.Controls.Add(_installed);
        tab.Controls.Add(buttons);
        return tab;
    }

    private TabPage CreateJobsTab()
    {
        var tab = new TabPage("Druckaufträge");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(10),
            Text = "Tunnel-Jobs: Windows → lokaler Proxy → Server → Server-Spooler. Direkte IPP/WSD-Jobs gehen unmittelbar zum Gerät und erscheinen nicht in dieser Tunnel-Historie."
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
            var update = await GitHubUpdateService.CheckAsync(current, SimplePrintComponent.Client);

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
        _config = JsonStore.LoadOrCreate(AppPaths.ClientConfig, () => new ClientConfig());

        var svc = await PowerShellRunner.RunAsync("(Get-Service -Name SimplePrintClient -ErrorAction SilentlyContinue).Status");
        _agent.Text = string.IsNullOrWhiteSpace(svc.StdOut) ? "nicht installiert" : svc.StdOut.Trim();

        var assemblyVersion = typeof(MainForm).Assembly.GetName().Version;
        _version.Text = assemblyVersion is null
            ? $"unbekannt · P{Protocol.Version}"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build} · P{Protocol.Version}";

        await RefreshAvailablePrintersAsync();
        RefreshInstalledGrid();
        RefreshJobsGrid();
        RefreshStartupState();
        UpdateTrayStatus();
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
            var incompatible = _servers.Count(x => x.Announcement.Version != Protocol.Version);
            _scan.Text = _servers.Count == 0
                ? "kein Server gefunden"
                : incompatible == 0
                    ? $"{_servers.Count} Server gefunden"
                    : $"{_servers.Count} gefunden · {incompatible} inkompatibel";

            RefreshServerGrid();
            RefreshAvailableGrid();

            SetStatus(
                _servers.Count == 0
                    ? "Kein Server gefunden."
                    : incompatible == 0
                        ? $"✓ {_servers.Count} Server gefunden."
                        : $"⚠ {_servers.Count} Server gefunden, davon {incompatible} inkompatibel.");
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

        foreach (var server in _servers)
        {
            var preferred = _config.PreferredServerId == server.Announcement.ServerId;
            var compatible = server.Announcement.Version == Protocol.Version;

            var row = _serversGrid.Rows.Add(
                preferred ? "✓" : "",
                server.Announcement.ServerName,
                server.Address.ToString(),
                server.Announcement.GatewayPort,
                string.IsNullOrWhiteSpace(server.Announcement.AppVersion)
                    ? "unbekannt"
                    : server.Announcement.AppVersion,
                server.Announcement.Version,
                compatible ? "OK" : $"benötigt P{Protocol.Version}",
                server.Announcement.Printers.Count);

            _serversGrid.Rows[row].Tag = server;

            if (preferred)
                _serversGrid.Rows[row].DefaultCellStyle.Font =
                    new Font(_serversGrid.Font, FontStyle.Bold);

            if (!compatible)
                _serversGrid.Rows[row].DefaultCellStyle.ForeColor = Color.DarkRed;
        }

        if (_config.PreferredServerId is Guid id)
        {
            var found = _servers.FirstOrDefault(x => x.Announcement.ServerId == id);

            _selectedServer.Text = found is null
                ? $"Fest ausgewählter Server: {id} (aktuell nicht gefunden)"
                : found.Announcement.Version == Protocol.Version
                    ? $"Fest ausgewählter Server: {found.Announcement.ServerName} ({found.Address}) · kompatibel"
                    : $"Fest ausgewählter Server: {found.Announcement.ServerName} ({found.Address}) · INKOMPATIBEL P{found.Announcement.Version}";
        }
        else
        {
            _selectedServer.Text = "Kein Server fest ausgewählt";
        }
    }

    private void RefreshAvailableGrid()
    {
        _available.Rows.Clear();

        if (_config.PreferredServerId is not Guid preferredId)
            return;

        var server = _servers.FirstOrDefault(
            x => x.Announcement.ServerId == preferredId);

        if (server is null)
            return;

        if (server.Announcement.Version != Protocol.Version)
        {
            SetStatus(
                $"⚠ Server '{server.Announcement.ServerName}' verwendet Protokoll {server.Announcement.Version}; benötigt wird {Protocol.Version}.");
            return;
        }

        foreach (var printer in server.Announcement.Printers)
        {
            var installed = _config.Mappings.Any(
                m => m.ServerId == server.Announcement.ServerId &&
                     m.PrinterId == printer.Id);

            var row = _available.Rows.Add(
                installed,
                printer.DisplayName,
                PrinterTransport.IsDirect(printer.TransportMode)
                    ? $"{printer.DriverName} · Direkt {printer.TransportMode}"
                    : printer.DriverName,
                printer.Status);

            _available.Rows[row].Tag =
                new AvailableTag(server, printer);
        }
    }

    private void RefreshInstalledGrid()
    {
        _installed.Rows.Clear();
        foreach (var m in _config.Mappings)
        {
            var transport = PrinterTransport.IsDirect(m.TransportMode)
                ? $"Direkt {m.TransportMode}"
                : m.LocalProxyPort.ToString();

            var i = _installed.Rows.Add(m.LocalPrinterName, m.ServerName, m.DriverName, transport);
            _installed.Rows[i].Tag = m.PortName;
        }
    }

    private void RefreshJobsGrid()
    {
        List<PrintJobRecord> jobs;
        try
        {
            jobs = JsonStore.LoadOrCreate(AppPaths.ClientJobs, () => new List<PrintJobRecord>());
        }
        catch
        {
            jobs = [];
        }

        _jobs.Rows.Clear();

        foreach (var job in jobs.OrderByDescending(x => x.CreatedAt).Take(100))
        {
            var row = _jobs.Rows.Add(
                job.JobId.ToString("N")[..8],
                job.CreatedAt.ToLocalTime().ToString("dd.MM. HH:mm:ss"),
                string.IsNullOrWhiteSpace(job.LocalPrinterName) ? job.PrinterName : job.LocalPrinterName,
                job.ServerName,
                job.Status,
                job.Bytes == 0 ? "" : job.Bytes.ToString("N0"),
                job.Message);

            _jobs.Rows[row].Tag = job.JobId;

            if (job.Status.Equals("Fehler", StringComparison.OrdinalIgnoreCase))
                _jobs.Rows[row].DefaultCellStyle.ForeColor = Color.DarkRed;
        }
    }

    private void ClearCompletedJobs()
    {
        var jobs = JsonStore.LoadOrCreate(
            AppPaths.ClientJobs,
            () => new List<PrintJobRecord>());

        var completed = new HashSet<string>(
            ["Gedruckt", "Abgeschlossen", "Ignoriert", "Fehler"],
            StringComparer.OrdinalIgnoreCase);

        var count = jobs.RemoveAll(x => completed.Contains(x.Status));
        JsonStore.Save(AppPaths.ClientJobs, jobs);
        RefreshJobsGrid();
        SetStatus($"✓ {count} abgeschlossene Druckaufträge gelöscht.");
    }

    private void ClearAllJobs()
    {
        if (MessageBox.Show(
                "Die gesamte lokale Druckauftragshistorie löschen?",
                "Druckaufträge löschen",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        JsonStore.Save(AppPaths.ClientJobs, new List<PrintJobRecord>());
        RefreshJobsGrid();
        SetStatus("✓ Druckauftragshistorie gelöscht.");
    }

    private void UseSelectedServer()
    {
        if (_serversGrid.SelectedRows.Count == 0)
        {
            MessageBox.Show("Bitte einen gefundenen Server markieren.");
            return;
        }

        var server = (DiscoveredServer)_serversGrid.SelectedRows[0].Tag;

        if (server.Announcement.Version != Protocol.Version)
        {
            MessageBox.Show(
                $"Dieser Server verwendet SimplePrint-Protokoll {server.Announcement.Version}. " +
                $"Der Client benötigt Protokoll {Protocol.Version}.\r\n\r\n" +
                "Bitte Client und Server auf dieselbe SimplePrint-Version aktualisieren.",
                "Inkompatibler Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _config.PreferredServerId = server.Announcement.ServerId;
        JsonStore.Save(AppPaths.ClientConfig, _config);

        RefreshServerGrid();
        RefreshAvailableGrid();

        SetStatus($"✓ Server '{server.Announcement.ServerName}' ausgewählt.");
        MessageBox.Show(
            $"'{server.Announcement.ServerName}' wird jetzt als fester SimplePrint-Server verwendet.");
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

        if (server.Announcement.Version != Protocol.Version)
        {
            MessageBox.Show(
                $"Der ausgewählte Server verwendet Protokoll {server.Announcement.Version}; benötigt wird {Protocol.Version}.",
                "Inkompatibler Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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
                var current = _config.Mappings.FirstOrDefault(
                    m => m.ServerId == preferredId && m.PrinterId == tag.Printer.Id);

                if (current is not null)
                {
                    var routeChanged =
                        !string.Equals(current.TransportMode, tag.Printer.TransportMode, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(current.DirectAddress, tag.Printer.DirectAddress, StringComparison.OrdinalIgnoreCase);

                    var classDriverMismatch =
                        PrinterTransport.IsClassDriver(tag.Printer.DriverName) &&
                        !string.Equals(current.DriverName, tag.Printer.DriverName, StringComparison.OrdinalIgnoreCase);

                    if (!routeChanged && !classDriverMismatch)
                        continue;

                    await PrinterInstaller.RemoveAsync(current);
                    _config.Mappings.Remove(current);
                    JsonStore.Save(AppPaths.ClientConfig, _config);
                }

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
        var direct = PrinterTransport.IsDirect(tag.Printer.TransportMode) &&
                     !string.IsNullOrWhiteSpace(tag.Printer.DirectAddress);

        if (PrinterTransport.IsMicrosoftIppClassDriver(tag.Printer.DriverName) && !direct)
        {
            throw new InvalidOperationException(
                $"'{tag.Printer.DisplayName}' verwendet den Microsoft IPP Class Driver, " +
                "aber der Server konnte keine direkte IPP-/WSD-Geräteadresse ermitteln.\r\n\r\n" +
                "Die Queue wird nicht mehr über den RAW-Tunnel angelegt, weil dieser Treiber eine echte " +
                "IPP-/WSD-Gegenstelle erwartet. Bitte die Druckerfreigabe am Server neu speichern oder " +
                "den Drucker dort mit einer erreichbaren IPP-/WSD-Verbindung installieren.");
        }

        string driver;

        if (direct)
        {
            driver = tag.Printer.DriverName;
        }
        else
        {
            var drivers = await PrinterInstaller.GetDriverNamesAsync();
            if (drivers.Count == 0)
                throw new InvalidOperationException("Auf diesem PC wurden keine Druckertreiber gefunden.");

            driver = drivers.FirstOrDefault(
                         d => d.Equals(tag.Printer.DriverName, StringComparison.OrdinalIgnoreCase))
                     ?? "";

            if (PrinterTransport.IsClassDriver(tag.Printer.DriverName))
            {
                if (string.IsNullOrWhiteSpace(driver))
                {
                    SetBusy($"Treiber '{tag.Printer.DriverName}' wird aus dem Windows-Treiberspeicher installiert …");

                    try
                    {
                        await PrinterInstaller.EnsureDriverInstalledAsync(tag.Printer.DriverName);
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new InvalidOperationException(
                            $"Für die Class-Driver-Queue '{tag.Printer.DisplayName}' muss auf Client und Server " +
                            $"exakt derselbe Treiber verwendet werden: '{tag.Printer.DriverName}'.\r\n\r\n" +
                            "Windows konnte diesen Treiber auf dem Client nicht aus dem lokalen Treiberspeicher installieren. " +
                            "Ein beliebiger Ersatztreiber wird aus Sicherheitsgründen nicht mehr verwendet.\r\n\r\n" +
                            ex.Message,
                            ex);
                    }

                    drivers = await PrinterInstaller.GetDriverNamesAsync();
                    driver = drivers.FirstOrDefault(
                                 d => d.Equals(tag.Printer.DriverName, StringComparison.OrdinalIgnoreCase))
                             ?? "";
                }

                if (string.IsNullOrWhiteSpace(driver))
                {
                    throw new InvalidOperationException(
                        $"Der erforderliche Server-Treiber '{tag.Printer.DriverName}' ist auf diesem Client nicht verfügbar. " +
                        "Für Class-Driver-Drucker lässt SimplePrint keinen abweichenden Ersatztreiber mehr zu.");
                }
            }
            else if (string.IsNullOrWhiteSpace(driver))
            {
                driver = ChooseDriver(drivers, tag.Printer.DriverName) ?? "";
                if (string.IsNullOrWhiteSpace(driver))
                    return;
            }
        }

        var shortServer = tag.Server.Announcement.ServerId.ToString("N")[..8];
        var shortPrinter = tag.Printer.Id.ToString("N")[..8];
        var localPort = direct ? 0 : AllocatePort();
        var portName = direct
            ? $"SimplePrintDirect_{shortServer}_{shortPrinter}"
            : $"SimplePrint_{shortServer}_{shortPrinter}";
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
            LocalProxyPort = localPort,
            TransportMode = direct ? tag.Printer.TransportMode : PrinterTransport.Tunnel,
            DirectAddress = direct ? tag.Printer.DirectAddress : "",
            DeviceUuid = direct ? tag.Printer.DeviceUuid : ""
        };

        _config.Mappings.Add(mapping);
        JsonStore.Save(AppPaths.ClientConfig, _config);

        try
        {
            if (!direct)
            {
                SetBusy($"Lokaler SimplePrint-Proxy auf Port {localPort} wird gestartet …");

                if (!await WaitForLocalProxyAsync(localPort, TimeSpan.FromSeconds(8)))
                    throw new InvalidOperationException(
                        $"Der SimplePrint Client-Agent lauscht nicht auf 127.0.0.1:{localPort}. Die Windows-Druckerqueue wurde deshalb nicht angelegt.");
            }
            else
            {
                SetBusy($"Direkte {mapping.TransportMode}-Druckerqueue wird eingerichtet …");
            }

            await PrinterInstaller.InstallAsync(mapping);
        }
        catch
        {
            _config.Mappings.Remove(mapping);
            JsonStore.Save(AppPaths.ClientConfig, _config);
            throw;
        }
    }

    private static async Task<bool> WaitForLocalProxyAsync(int port, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;

        while (DateTime.UtcNow - started < timeout)
        {
            try
            {
                var listeners = System.Net.NetworkInformation.IPGlobalProperties
                    .GetIPGlobalProperties()
                    .GetActiveTcpListeners();

                if (listeners.Any(x => x.Port == port && System.Net.IPAddress.IsLoopback(x.Address)))
                    return true;
            }
            catch
            {
            }

            await Task.Delay(250);
        }

        return false;
    }

    private static bool IsGenericClassDriver(string driver) =>
        PrinterTransport.IsClassDriver(driver);

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

    private async Task ShowInstalledPrinterHealthAsync()
    {
        var mapping = GetSelectedInstalledMapping();
        if (mapping is null)
        {
            MessageBox.Show("Bitte zuerst einen installierten Drucker markieren.");
            return;
        }

        SetBusy("End-to-End-Druckbereitschaft wird geprüft …");

        try
        {
            var local = await PrinterInstaller.GetLocalReadinessAsync(mapping);
            var health = await QueryServerPrinterHealthAsync(mapping);

            health.ClientTransportStatus = local.Detail;
            health.ServerTransportStatus =
                $"Server '{mapping.ServerName}' erreichbar · Protokoll {Protocol.Version}";

            foreach (var warning in local.Warnings)
            {
                if (!health.Warnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
                    health.Warnings.Insert(0, warning);
            }

            if (!local.Ready)
            {
                health.Level = "Red";
                health.Summary =
                    "Nicht druckbereit: Die lokale Client-Druckkette ist nicht vollständig funktionsfähig.";
            }
            else if (local.Warnings.Count > 0 &&
                     health.Level.Equals("Green", StringComparison.OrdinalIgnoreCase))
            {
                health.Level = "Yellow";
                health.Summary =
                    "Druck wahrscheinlich möglich, aber der Client meldet einen Treiberhinweis.";
            }

            using var dialog = new PrinterHealthForm(health);
            SetStatus(
                health.Level == "Green"
                    ? "✓ End-to-End-Druckbereitschaft: bereit."
                    : health.Level == "Red"
                        ? "✗ End-to-End-Druckbereitschaft: nicht bereit."
                        : "⚠ End-to-End-Druckbereitschaft: mit Hinweisen.");

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

    private async Task<PrinterHealthStatus> QueryServerPrinterHealthAsync(
        ClientPrinterMapping mapping)
    {
        var server = _servers.FirstOrDefault(
            x => x.Announcement.ServerId == mapping.ServerId);

        if (server is null)
        {
            await RefreshAvailablePrintersAsync();
            server = _servers.FirstOrDefault(
                x => x.Announcement.ServerId == mapping.ServerId);
        }

        if (server is null)
            throw new InvalidOperationException(
                $"Server '{mapping.ServerName}' wurde aktuell nicht gefunden.");

        if (server.Announcement.Version != Protocol.Version)
            throw new InvalidOperationException(
                $"Server '{server.Announcement.ServerName}' verwendet Protokoll " +
                $"{server.Announcement.Version}; benötigt wird {Protocol.Version}.");

        using var tcp = new TcpClient { NoDelay = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        await tcp.ConnectAsync(
            server.Address,
            server.Announcement.GatewayPort,
            timeout.Token);

        using var stream = tcp.GetStream();
        await stream.WriteAsync(
            Protocol.CreateGatewayHeader(mapping.PrinterId, Guid.Empty),
            timeout.Token);

        var health = await Protocol.ReadPrinterHealthAsync(
            stream,
            timeout.Token);

        if (health is null)
            throw new InvalidOperationException(
                "Der Server hat keine gültige Druckerstatus-Antwort geliefert.");

        return health;
    }

    private ClientPrinterMapping? GetSelectedInstalledMapping()
    {
        if (_installed.SelectedRows.Count == 0)
            return null;

        var portName = _installed.SelectedRows[0].Tag as string;
        if (string.IsNullOrWhiteSpace(portName))
            return null;

        return _config.Mappings.FirstOrDefault(
            x => x.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task QuickDiagnosisAsync()
    {
        if (_installed.SelectedRows.Count == 0)
        {
            MessageBox.Show("Bitte zuerst einen installierten Drucker markieren.");
            return;
        }

        var portName = (string)_installed.SelectedRows[0].Tag;
        var mapping = _config.Mappings.FirstOrDefault(m => m.PortName == portName);

        if (mapping is null)
        {
            MessageBox.Show("Für diese Zeile wurde keine SimplePrint-Zuordnung gefunden.");
            return;
        }

        SetBusy("Schnelldiagnose wird ausgeführt …");

        try
        {
            var local = await PrinterInstaller.GetQuickDiagnosisAsync(mapping);
            var server = _servers.FirstOrDefault(x => x.Announcement.ServerId == mapping.ServerId);

            var serverState = server is null
                ? $"Server '{mapping.ServerName}': aktuell nicht per Discovery gefunden"
                : $"Server '{server.Announcement.ServerName}': {server.Address}:{server.Announcement.GatewayPort} gefunden";

            var gatewayState = PrinterTransport.IsDirect(mapping.TransportMode)
                ? $"Druckpfad: Direkt {mapping.TransportMode} zum Gerät; SimplePrint-Gateway wird für Druckdaten nicht verwendet."
                : "Gateway-Test: nicht möglich";

            if (server is not null && !PrinterTransport.IsDirect(mapping.TransportMode))
            {
                try
                {
                    using var tcp = new TcpClient();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

                    await tcp.ConnectAsync(
                        server.Address,
                        server.Announcement.GatewayPort,
                        timeout.Token);

                    using var stream = tcp.GetStream();
                    await stream.WriteAsync(
                        Protocol.CreateGatewayHeader(Guid.Empty),
                        timeout.Token);

                    var reply = new byte[6];
                    var ok = await Protocol.ReadExactAsync(stream, reply, timeout.Token) &&
                             System.Text.Encoding.ASCII.GetString(reply) == "SPROK1";

                    gatewayState = ok
                        ? "Gateway-Test: OK"
                        : "Gateway-Test: ungültige Serverantwort";
                }
                catch (Exception ex)
                {
                    gatewayState = "Gateway-Test: FEHLER - " + ex.Message;
                }
            }

            List<PrintJobRecord> jobs;
            try
            {
                jobs = JsonStore.LoadOrCreate(
                    AppPaths.ClientJobs,
                    () => new List<PrintJobRecord>());
            }
            catch
            {
                jobs = [];
            }

            var recent = jobs
                .Where(x => x.PrinterId == mapping.PrinterId &&
                            x.ServerId == mapping.ServerId)
                .OrderByDescending(x => x.CreatedAt)
                .Take(5)
                .ToList();

            var jobLines = recent.Count == 0
                ? "Keine bisherigen Druckaufträge für diesen Drucker gespeichert."
                : string.Join(
                    Environment.NewLine,
                    recent.Select(x =>
                        $"{x.JobId.ToString("N")[..8]} · {x.CreatedAt.ToLocalTime():HH:mm:ss} · {x.Status} · {x.Message}"));

            SetStatus("✓ Schnelldiagnose abgeschlossen.");

            MessageBox.Show(
                serverState + Environment.NewLine +
                gatewayState + Environment.NewLine + Environment.NewLine +
                local + Environment.NewLine +
                "=== LETZTE DRUCKAUFTRÄGE ===" + Environment.NewLine +
                jobLines,
                "SimplePrint Schnelldiagnose",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Schnelldiagnose fehlgeschlagen.");
            MessageBox.Show(ex.Message, "Schnelldiagnose", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        if (server.Announcement.Version != Protocol.Version)
        {
            MessageBox.Show(
                $"Server gefunden, aber inkompatibel. Server-Protokoll: {server.Announcement.Version}, Client-Protokoll: {Protocol.Version}.",
                "Verbindung",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            SetBusy("Verbindung zum Server wird getestet …");
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
            SetStatus("✗ Verbindungstest fehlgeschlagen.");
            MessageBox.Show($"{server.Announcement.ServerName} ({server.Address}): FEHLER\n{ex.Message}", "SimplePrint Verbindungstest", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TestPage()
    {
        if (_installed.SelectedRows.Count == 0)
        {
            MessageBox.Show("Bitte zuerst einen installierten Drucker markieren.");
            return;
        }

        var portName = (string)_installed.SelectedRows[0].Tag;
        var mapping = _config.Mappings.First(m => m.PortName == portName);
        PrinterInstaller.PrintTestPage(mapping.LocalPrinterName);
        SetStatus($"✓ Testseite für '{mapping.LocalPrinterName}' wurde gestartet.");
    }

    private async Task SetStartupAsync(bool enabled)
    {
        try
        {
            SetBusy(enabled ? "Autostart wird aktiviert …" : "Autostart wird deaktiviert …");
            await StartupManager.SetSystemWideAsync(
                "SimplePrintClientGui",
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
            if (File.Exists(AppPaths.ClientJobs)) zip.CreateEntryFromFile(AppPaths.ClientJobs, "jobs.json", CompressionLevel.Optimal);

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
                    await dw.WriteLineAsync(
                        $"{server.Announcement.ServerName} {server.Address}:{server.Announcement.GatewayPort} " +
                        $"app={server.Announcement.AppVersion} protocol={server.Announcement.Version} " +
                        $"compatible={server.Announcement.Version == Protocol.Version} " +
                        $"id={server.Announcement.ServerId} printers={server.Announcement.Printers.Count}");
            }

            var healthResults = new List<PrinterHealthStatus>();

            foreach (var mapping in _config.Mappings)
            {
                try
                {
                    var local = await PrinterInstaller.GetLocalReadinessAsync(mapping);
                    var health = await QueryServerPrinterHealthAsync(mapping);

                    health.ClientTransportStatus = local.Detail;
                    health.ServerTransportStatus =
                        $"Server '{mapping.ServerName}' · Protokoll {Protocol.Version}";

                    foreach (var warning in local.Warnings)
                    {
                        if (!health.Warnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
                            health.Warnings.Add(warning);
                    }

                    if (!local.Ready)
                    {
                        health.Level = "Red";
                        health.Summary = "Lokale Client-Druckkette nicht vollständig funktionsfähig.";
                    }

                    healthResults.Add(health);
                }
                catch (Exception ex)
                {
                    healthResults.Add(new PrinterHealthStatus
                    {
                        PrinterId = mapping.PrinterId,
                        PrinterName = mapping.LocalPrinterName,
                        Level = "Red",
                        Summary = "End-to-End-Prüfung fehlgeschlagen.",
                        Warnings = [ex.Message]
                    });
                }
            }

            var healthEntry = zip.CreateEntry("printer-health.json");
            await using (var stream = healthEntry.Open())
            await using (var hw = new StreamWriter(stream))
            {
                await hw.WriteAsync(
                    JsonSerializer.Serialize(
                        healthResults,
                        JsonStore.Options));
            }

            SetStatus("✓ Diagnosepaket wurde erstellt.");
            MessageBox.Show("Diagnosepaket wurde erstellt.");
        }
        catch (Exception ex)
        {
            SetStatus("✗ Diagnosepaket konnte nicht erstellt werden.");
            MessageBox.Show(ex.Message, "Diagnose", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        var agentOk = _agent.Text.Equals("Running", StringComparison.OrdinalIgnoreCase);

        var preferred = _config.PreferredServerId is Guid preferredId
            ? _servers.FirstOrDefault(x => x.Announcement.ServerId == preferredId)
            : null;

        var publicNetwork = _scan.Text.Contains(
            "öffentlich",
            StringComparison.OrdinalIgnoreCase);

        var level = !agentOk || publicNetwork
            ? "Red"
            : preferred is not null &&
              preferred.Announcement.Version == Protocol.Version
                ? "Green"
                : "Yellow";

        var next = Branding.CreateStatusIcon(level);
        if (next is null) return;

        var previous = _tray.Icon;
        _tray.Icon = next;
        previous?.Dispose();

        _tray.Text = level switch
        {
            "Green" => "SimplePrint Client - bereit",
            "Yellow" => "SimplePrint Client - Serverauswahl prüfen",
            _ => "SimplePrint Client - Problem"
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
}