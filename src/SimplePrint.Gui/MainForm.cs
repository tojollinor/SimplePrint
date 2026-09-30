using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimplePrint.Common;

namespace SimplePrint.Gui;

public sealed class MainForm : Form
{
    private sealed record NetworkPrinterTag(
        DevicePresence Device,
        DiscoveredPrinter Printer);

    private sealed record RemoteDiagnosticsFetch(
        DevicePresence Device,
        byte[]? Archive,
        string? Error);

    private readonly Label _deviceName = new() { AutoSize = true };
    private readonly Label _version = new() { AutoSize = true };
    private readonly Label _serviceStatus = new() { AutoSize = true };
    private readonly Label _settingsServiceStatus = ValueLabel();
    private readonly Label _settingsNetworkStatus = ValueLabel();
    private readonly Label _settingsFirewallStatus = ValueLabel();
    private readonly Label _startupStatus = ValueLabel();
    private readonly CheckBox _startupTray = new()
    {
        AutoSize = true,
        Text = "GUI beim Windows-Start im Infobereich starten",
        Checked = true
    };

    private readonly Label _overviewDevice = ValueLabel();
    private readonly Label _overviewShared = ValueLabel();
    private readonly Label _overviewNetwork = ValueLabel();
    private readonly Label _overviewServers = ValueLabel();
    private readonly Label _overviewClients = ValueLabel();
    private readonly Label _overviewPeers = ValueLabel();

    private readonly DataGridView _ownPrinters = Grid();
    private readonly TreeView _networkPrinters = new()
    {
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        HideSelection = false
    };
    private readonly DataGridView _servers = Grid();
    private readonly DataGridView _clients = Grid();
    private readonly DataGridView _jobs = Grid();
    private readonly TreeView _diagnostics = new()
    {
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        HideSelection = false
    };

    private readonly ToolStripStatusLabel _operationStatus = new() { Text = "Bereit" };
    private readonly ToolStripProgressBar _operationProgress = new()
    {
        Style = ProgressBarStyle.Marquee,
        Visible = false,
        Width = 110
    };

    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 5000 };
    private readonly NotifyIcon _tray;

    private SimplePrintConfig _config = new();
    private List<DevicePresence> _peers = [];
    private bool _allowExit;
    private bool _suppressNetworkTreeCheck;
    private bool _suppressDiagnosticsTreeCheck;
    private bool _updateCheckRunning;
    private string? _lastOfferedUpdate;

    public MainForm()
    {
        Text = "SimplePrint";
        Width = 940;
        Height = 680;
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Branding.ApplyApplicationIcon(this);

        if (Program.StartInTray)
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
        }

        Controls.Add(CreateMainTabs());
        Controls.Add(CreateHeader());
        Controls.Add(CreateFooter());

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("SimplePrint öffnen", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Aktualisieren", null, async (_, _) => await RefreshAllAsync(true));
        trayMenu.Items.Add("Nach Updates suchen", null, async (_, _) => await CheckForUpdatesAsync(true));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Beenden", null, (_, _) => ExitApplication());

        _tray = new NotifyIcon
        {
            Icon = Branding.LoadFixedIcon() ?? (Icon)SystemIcons.Application.Clone(),
            Text = "SimplePrint",
            Visible = true,
            ContextMenuStrip = trayMenu
        };

        _tray.DoubleClick += (_, _) => ShowFromTray();

        _networkPrinters.AfterCheck += (_, e) =>
        {
            if (_suppressNetworkTreeCheck ||
                e.Node.Tag is not DevicePresence)
            {
                return;
            }

            _suppressNetworkTreeCheck = true;
            try
            {
                e.Node.Checked = false;
            }
            finally
            {
                _suppressNetworkTreeCheck = false;
            }
        };

        _diagnostics.AfterCheck += (_, e) =>
        {
            if (_suppressDiagnosticsTreeCheck ||
                e.Node.Tag is not string tag ||
                !tag.StartsWith("group:", StringComparison.Ordinal))
            {
                return;
            }

            _suppressDiagnosticsTreeCheck = true;
            try
            {
                e.Node.Checked = false;
            }
            finally
            {
                _suppressDiagnosticsTreeCheck = false;
            }
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideToTray();
        };

        FormClosing += (_, e) =>
        {
            if (_allowExit ||
                e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
            {
                return;
            }

            e.Cancel = true;
            HideToTray();
        };

        FormClosed += (_, _) =>
        {
            _liveTimer.Stop();
            _liveTimer.Dispose();
            _tray.Dispose();
        };

        _liveTimer.Tick += (_, _) => RefreshLiveState();
        _liveTimer.Start();

        Shown += async (_, _) =>
        {
            if (Program.StartInTray)
            {
                HideToTray();
                Opacity = 1;
            }

            await RefreshAllAsync(true);

            if (!string.IsNullOrWhiteSpace(Program.UpdateSuccessVersion))
            {
                ShowFromTray();
                MessageBox.Show(
                    $"SimplePrint wurde erfolgreich auf {Program.UpdateSuccessVersion} aktualisiert.",
                    "SimplePrint Update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        };
    }

    private Control CreateHeader()
    {
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 126,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 1
        };

        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(Branding.CreateGuiLogoBox(), 0, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10, 2, 0, 0)
        };

        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        right.Controls.Add(
            new Label
            {
                Text = "SimplePrint",
                AutoSize = true,
                Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
                Margin = new Padding(0, 5, 0, 0)
            },
            0,
            0);

        var status = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0)
        };

        status.Controls.AddRange(
        [
            BoldLabel("Gerät:"), _deviceName,
            Spacer(),
            BoldLabel("Dienst:"), _serviceStatus,
            Spacer(),
            BoldLabel("Version:"), _version
        ]);

        right.Controls.Add(status, 0, 1);
        top.Controls.Add(right, 1, 0);

        return top;
    }

    private Control CreateMainTabs()
    {
        ConfigureGrids();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateOverviewTab());
        tabs.TabPages.Add(CreatePrintersTab());
        tabs.TabPages.Add(CreateServersTab());
        tabs.TabPages.Add(CreateClientsTab());
        tabs.TabPages.Add(CreateJobsTab());
        tabs.TabPages.Add(CreateDiagnosticsTab());
        tabs.TabPages.Add(CreateSettingsTab());

        tabs.Selected += (_, e) =>
        {
            if (e.TabPage?.Text == "Druckaufträge")
                RefreshJobsGrid();
        };

        return tabs;
    }

    private Control CreateFooter()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 74 };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 6, 8, 2)
        };

        buttons.Controls.Add(MakeButton(
            "Aktualisieren",
            async (_, _) => await RefreshAllAsync(true)));

        buttons.Controls.Add(MakeButton(
            "In Infobereich minimieren",
            (_, _) => HideToTray()));

        var status = new StatusStrip
        {
            Dock = DockStyle.Bottom,
            SizingGrip = false
        };
        status.Items.Add(_operationStatus);
        status.Items.Add(new ToolStripStatusLabel { Spring = true });
        status.Items.Add(_operationProgress);

        panel.Controls.Add(buttons);
        panel.Controls.Add(status);
        return panel;
    }

    private TabPage CreateOverviewTab()
    {
        var tab = new TabPage("Übersicht");

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(22),
            ColumnCount = 2,
            RowCount = 6
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddOverviewRow(panel, 0, "Dieses Gerät", _overviewDevice);
        AddOverviewRow(panel, 1, "Eigene freigegebene Drucker", _overviewShared);
        AddOverviewRow(panel, 2, "Verwendete Netzwerkdrucker", _overviewNetwork);
        AddOverviewRow(panel, 3, "Verbundene Server", _overviewServers);
        AddOverviewRow(panel, 4, "Verbundene Clients", _overviewClients);
        AddOverviewRow(panel, 5, "Aktive SimplePrint-Geräte im Netzwerk", _overviewPeers);

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 60,
            Padding = new Padding(22, 8, 22, 8),
            Text =
                "Jede SimplePrint-Installation kann gleichzeitig eigene Drucker bereitstellen " +
                "und Drucker beliebig vieler anderer SimplePrint-Geräte verwenden."
        };

        tab.Controls.Add(info);
        tab.Controls.Add(panel);
        return tab;
    }

    private TabPage CreatePrintersTab()
    {
        var tab = new TabPage("Drucker");
        var inner = new TabControl { Dock = DockStyle.Fill };

        var own = new TabPage("Eigene Drucker");
        var ownInfo = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(10),
            Text =
                "Alle lokal installierten Windows-Drucker. Die Freigabe über SimplePrint " +
                "wird direkt pro Drucker verwaltet."
        };
        var ownButtons = BottomButtons();
        ownButtons.Controls.Add(MakeButton(
            "Druckerliste aktualisieren",
            async (_, _) => await RefreshOwnPrintersAsync()));
        ownButtons.Controls.Add(MakeButton(
            "Freigaben speichern",
            async (_, _) => await SaveOwnPrinterSelectionAsync()));
        ownButtons.Controls.Add(MakeButton(
            "Warteschlange öffnen",
            (_, _) => OpenSelectedOwnPrinterQueue()));
        ownButtons.Controls.Add(MakeButton(
            "Testseite",
            (_, _) => PrintSelectedOwnPrinterTestPage()));

        own.Controls.Add(_ownPrinters);
        own.Controls.Add(ownInfo);
        own.Controls.Add(ownButtons);

        var network = new TabPage("Netzwerkdrucker");
        var networkInfo = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(10),
            Text =
                "Freigegebene Drucker anderer aktiver SimplePrint-Geräte, nach Quellgerät gruppiert."
        };
        var networkButtons = BottomButtons();
        networkButtons.Controls.Add(MakeButton(
            "Netzwerkdrucker aktualisieren",
            (_, _) => RefreshNetworkPrinterTree()));
        networkButtons.Controls.Add(MakeButton(
            "Druckerauswahl speichern",
            async (_, _) => await SaveNetworkPrinterSelectionAsync()));
        networkButtons.Controls.Add(MakeButton(
            "Warteschlange öffnen",
            (_, _) => OpenSelectedNetworkPrinterQueue()));
        networkButtons.Controls.Add(MakeButton(
            "Testseite",
            (_, _) => PrintSelectedNetworkPrinterTestPage()));

        network.Controls.Add(_networkPrinters);
        network.Controls.Add(networkInfo);
        network.Controls.Add(networkButtons);

        inner.TabPages.Add(own);
        inner.TabPages.Add(network);
        tab.Controls.Add(inner);
        return tab;
    }

    private TabPage CreateServersTab()
    {
        var tab = new TabPage("Server");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(10),
            Text =
                "Aktive SimplePrint-Geräte, von denen dieses Gerät mindestens einen Drucker verwendet."
        };
        tab.Controls.Add(_servers);
        tab.Controls.Add(info);
        return tab;
    }

    private TabPage CreateClientsTab()
    {
        var tab = new TabPage("Clients");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(10),
            Text =
                "Aktive SimplePrint-Geräte, die mindestens einen von diesem Gerät bereitgestellten Drucker verwenden."
        };
        tab.Controls.Add(_clients);
        tab.Controls.Add(info);
        return tab;
    }

    private TabPage CreateJobsTab()
    {
        var tab = new TabPage("Druckaufträge");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(10),
            Text =
                "Ein- und ausgehende SimplePrint-Druckaufträge werden gemeinsam dargestellt."
        };

        var buttons = BottomButtons();
        buttons.Controls.Add(MakeButton("Aktualisieren", (_, _) => RefreshJobsGrid()));
        buttons.Controls.Add(MakeButton(
            "Abgeschlossene löschen",
            (_, _) => ClearCompletedJobs()));
        buttons.Controls.Add(MakeButton(
            "Alle löschen",
            (_, _) => ClearAllJobs()));

        tab.Controls.Add(_jobs);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private TabPage CreateDiagnosticsTab()
    {
        var tab = new TabPage("Diagnose");
        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 68,
            Padding = new Padding(10),
            Text =
                "Dieses Gerät sowie aktuell erreichbare Server und Clients können einzeln " +
                "für ein Diagnosepaket ausgewählt werden. Dieses Gerät ist standardmäßig aktiviert, aber abwählbar."
        };

        var buttons = BottomButtons();
        buttons.Controls.Add(MakeButton(
            "Diagnosepaket erstellen",
            async (_, _) => await CreateDiagnosticsAsync()));

        tab.Controls.Add(_diagnostics);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private TabPage CreateSettingsTab()
    {
        var tab = new TabPage("Einstellungen");

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(20)
        };

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Gerät"
        });

        var deviceTable = new TableLayoutPanel
        {
            AutoSize = true,
            Width = 820,
            ColumnCount = 2,
            RowCount = 7,
            Margin = new Padding(0, 8, 0, 12)
        };

        deviceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        deviceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 560));

        AddSettingsRow(deviceTable, 0, "Gerätename", "SettingsDeviceName");
        AddSettingsRow(deviceTable, 1, "Discovery-Port", "SettingsDiscovery");
        AddSettingsRow(deviceTable, 2, "Print-Gateway", "SettingsGateway");
        AddSettingsRow(deviceTable, 3, "Diagnose-Port", "SettingsDiagnostics");

        deviceTable.Controls.Add(BoldLabel("Dienst"), 0, 4);
        deviceTable.Controls.Add(_settingsServiceStatus, 1, 4);

        deviceTable.Controls.Add(BoldLabel("Netzwerk"), 0, 5);
        deviceTable.Controls.Add(_settingsNetworkStatus, 1, 5);

        deviceTable.Controls.Add(BoldLabel("Firewall"), 0, 6);
        deviceTable.Controls.Add(_settingsFirewallStatus, 1, 6);

        panel.Controls.Add(deviceTable);

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 6, 0, 4),
            Text = "Autostart & Dienst"
        });

        panel.Controls.Add(_startupStatus);
        panel.Controls.Add(_startupTray);

        var startupButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 6, 0, 12)
        };

        startupButtons.Controls.Add(MakeButton(
            "Autostart aktivieren",
            async (_, _) => await SetStartupAsync(true)));

        startupButtons.Controls.Add(MakeButton(
            "Autostart deaktivieren",
            async (_, _) => await SetStartupAsync(false)));

        startupButtons.Controls.Add(MakeButton(
            "SimplePrint-Dienst neu starten",
            async (_, _) => await RestartServiceAsync()));

        panel.Controls.Add(startupButtons);

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 6, 0, 4),
            Text = "Firewall"
        });

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Text =
                "SimplePrint benötigt eingehend UDP für die Geräteerkennung sowie TCP für " +
                "Druckdaten und Diagnose. Die Regeln gelten ausschließlich für Privat/Domäne " +
                "und LocalSubnet."
        });

        var firewallButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 6, 0, 12)
        };

        firewallButtons.Controls.Add(MakeButton(
            "Firewall-Regeln anwenden",
            async (_, _) => await ApplyFirewallAsync()));

        firewallButtons.Controls.Add(MakeButton(
            "Firewall-Regeln zurücksetzen",
            async (_, _) => await RemoveFirewallAsync()));

        firewallButtons.Controls.Add(MakeButton(
            "Firewallstatus aktualisieren",
            async (_, _) => await RefreshSystemManagementAsync()));

        panel.Controls.Add(firewallButtons);

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 6, 0, 4),
            Text = "Updates"
        });

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Text =
                "Die Updateprüfung verwendet das gemeinsame SimplePrint-Release. " +
                "Die eigentliche Aktualisierung läuft nach Administratorfreigabe automatisch."
        });

        var updateButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 6, 0, 12)
        };

        updateButtons.Controls.Add(MakeButton(
            "Nach Updates suchen",
            async (_, _) => await CheckForUpdatesAsync(true)));

        panel.Controls.Add(updateButtons);

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 6, 0, 4),
            Text = "Konfiguration"
        });

        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Text = AppPaths.DeviceConfig
        });

        tab.Controls.Add(panel);
        return tab;
    }

    private static void AddSettingsRow(
        TableLayoutPanel panel,
        int row,
        string title,
        string controlName)
    {
        panel.Controls.Add(BoldLabel(title), 0, row);
        panel.Controls.Add(
            new Label
            {
                Name = controlName,
                AutoSize = true,
                Text = "…"
            },
            1,
            row);
    }

    private void ConfigureGrids()
    {
        _ownPrinters.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "shared",
            HeaderText = "Freigegeben",
            Width = 80,
            ReadOnly = true
        });
        _ownPrinters.Columns.Add("name", "Drucker");
        _ownPrinters.Columns.Add("driver", "Treiber");
        _ownPrinters.Columns.Add("port", "Port");
        _ownPrinters.Columns.Add("status", "Windows-Status");
        _ownPrinters.CellMouseDown += OwnPrinterMouseDown;

        _servers.Columns.Add("name", "Server");
        _servers.Columns.Add("address", "IP-Adresse");
        _servers.Columns.Add("version", "Version");
        _servers.Columns.Add("printers", "Verwendete Drucker");
        _servers.Columns.Add("seen", "Zuletzt gesehen");

        _clients.Columns.Add("name", "Client");
        _clients.Columns.Add("address", "IP-Adresse");
        _clients.Columns.Add("version", "Version");
        _clients.Columns.Add("printers", "Verwendet von diesem Gerät");
        _clients.Columns.Add("seen", "Zuletzt gesehen");

        _jobs.Columns.Add("time", "Zeit");
        _jobs.Columns.Add("direction", "Richtung");
        _jobs.Columns.Add("peer", "Gegenstelle");
        _jobs.Columns.Add("printer", "Drucker");
        _jobs.Columns.Add("status", "Status");
        _jobs.Columns.Add("bytes", "Bytes");
        _jobs.Columns.Add("message", "Meldung");
    }

    private async Task RefreshAllAsync(bool refreshNetworkPrinters)
    {
        try
        {
            SetBusy("SimplePrint wird aktualisiert …");

            _config = UnifiedConfigStore.LoadOrMigrate();
            LoadActivePeers();
            RefreshHeader();
            RefreshOverview();
            RefreshRelationshipGrids();
            RefreshJobsGrid();
            RefreshDiagnosticsTree();
            RefreshSettings();

            if (refreshNetworkPrinters)
                RefreshNetworkPrinterTree();

            await RefreshOwnPrintersAsync(false);
            await RefreshServiceStatusAsync();

            SetStatus("✓ Aktualisiert");
        }
        catch (Exception ex)
        {
            SetStatus("✗ Aktualisierung fehlgeschlagen");
            MessageBox.Show(
                ex.Message,
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private void RefreshLiveState()
    {
        try
        {
            _config = UnifiedConfigStore.LoadOrMigrate();
            LoadActivePeers();
            RefreshHeader();
            RefreshOverview();
            RefreshRelationshipGrids();
            RefreshDiagnosticsTree();

            if (Visible)
                RefreshJobsGrid();
        }
        catch
        {
            // Die periodische Oberflächenaktualisierung darf die GUI nicht stören.
        }
    }

    private void LoadActivePeers()
    {
        var all = JsonStore.LoadOrCreate(
            AppPaths.DevicePeers,
            () => new List<DevicePresence>());

        var cutoff = DateTimeOffset.Now - TimeSpan.FromSeconds(35);
        _peers = all
            .Where(x => x.DeviceId != Guid.Empty && x.LastSeen >= cutoff)
            .GroupBy(x => x.DeviceId)
            .Select(x => x.OrderByDescending(y => y.LastSeen).First())
            .OrderBy(x => x.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void RefreshHeader()
    {
        _deviceName.Text = string.IsNullOrWhiteSpace(_config.DeviceName)
            ? Environment.MachineName
            : _config.DeviceName;

        var version = typeof(MainForm).Assembly.GetName().Version;
        _version.Text = version is null
            ? "unbekannt"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void RefreshOverview()
    {
        var serverIds = _config.NetworkPrinters
            .Where(x => x.Enabled)
            .Select(x => x.SourceDeviceId)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToHashSet();

        var clientIds = _peers
            .Where(x => x.Subscriptions.Any(s => s.SourceDeviceId == _config.DeviceId))
            .Select(x => x.DeviceId)
            .Distinct()
            .ToHashSet();

        _overviewDevice.Text = _deviceName.Text;
        _overviewShared.Text = _config.SharedPrinters.Count(x => x.Enabled).ToString();
        _overviewNetwork.Text = _config.NetworkPrinters.Count(x => x.Enabled).ToString();
        _overviewServers.Text = _peers.Count(x => serverIds.Contains(x.DeviceId)).ToString();
        _overviewClients.Text = clientIds.Count.ToString();
        _overviewPeers.Text = _peers.Count.ToString();

        var level = _serviceStatus.Text.Equals("Läuft", StringComparison.OrdinalIgnoreCase)
            ? "Green"
            : "Yellow";

        var next = Branding.CreateStatusIcon(level);
        if (next is not null)
        {
            var previous = _tray.Icon;
            _tray.Icon = next;
            previous?.Dispose();
        }

        _tray.Text = level == "Green"
            ? "SimplePrint - bereit"
            : "SimplePrint - Dienst prüfen";
    }

    private void OwnPrinterMouseDown(
        object? sender,
        DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.Button != MouseButtons.Left ||
            e.ColumnIndex != _ownPrinters.Columns["shared"]!.Index)
        {
            return;
        }

        var cellRect = _ownPrinters.GetCellDisplayRectangle(
            e.ColumnIndex,
            e.RowIndex,
            false);

        using var graphics = _ownPrinters.CreateGraphics();
        var glyphSize = CheckBoxRenderer.GetGlyphSize(
            graphics,
            System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);

        var glyphBounds = new Rectangle(
            cellRect.Left + Math.Max(0, (cellRect.Width - glyphSize.Width) / 2),
            cellRect.Top + Math.Max(0, (cellRect.Height - glyphSize.Height) / 2),
            glyphSize.Width,
            glyphSize.Height);

        var click = new Point(cellRect.Left + e.X, cellRect.Top + e.Y);
        if (!glyphBounds.Contains(click))
            return;

        var cell = _ownPrinters.Rows[e.RowIndex].Cells["shared"];
        cell.Value = !(cell.Value is bool current && current);
    }

    private async Task RefreshOwnPrintersAsync(bool showBusy = true)
    {
        try
        {
            if (showBusy)
                SetBusy("Lokale Drucker werden gelesen …");

            var allPrinters = await UnifiedPrinterHelper.GetPrintersAsync();
            var blocked = allPrinters
                .Where(UnifiedPrinterHelper.IsUnsafeSimplePrintLoop)
                .ToList();

            var printers = allPrinters
                .Where(x => !UnifiedPrinterHelper.IsUnsafeSimplePrintLoop(x))
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _ownPrinters.Rows.Clear();

            foreach (var printer in printers)
            {
                var shared = _config.SharedPrinters.Any(x =>
                    x.Enabled &&
                    x.QueueName.Equals(
                        printer.Name,
                        StringComparison.OrdinalIgnoreCase));

                var row = _ownPrinters.Rows.Add(
                    shared,
                    printer.Name,
                    printer.DriverName,
                    printer.PortName,
                    printer.PrinterStatus);

                _ownPrinters.Rows[row].Tag = printer;
            }

            if (showBusy)
            {
                SetStatus(
                    blocked.Count == 0
                        ? $"✓ {printers.Count} lokale Drucker geladen"
                        : $"✓ {printers.Count} lokale Drucker geladen · {blocked.Count} SimplePrint-Schleife(n) ausgeblendet");
            }
        }
        catch (Exception ex)
        {
            if (showBusy)
                SetStatus("✗ Lokale Drucker konnten nicht gelesen werden");

            MessageBox.Show(
                ex.Message,
                "Drucker",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (showBusy)
                SetIdle();
        }
    }

    private async Task SaveOwnPrinterSelectionAsync()
    {
        try
        {
            SetBusy("Druckerfreigaben werden gespeichert …");

            var selected = _ownPrinters.Rows
                .Cast<DataGridViewRow>()
                .Where(row =>
                    row.Tag is LocalPrinterInfo &&
                    row.Cells["shared"].Value is bool use &&
                    use)
                .Select(row => (LocalPrinterInfo)row.Tag!)
                .ToList();

            var old = _config.SharedPrinters
                .GroupBy(x => x.QueueName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Key,
                    x => x.First(),
                    StringComparer.OrdinalIgnoreCase);

            var next = new List<SharedPrinterConfig>();

            foreach (var printer in selected)
            {
                if (old.TryGetValue(printer.Name, out var existing))
                {
                    existing.DisplayName = printer.Name;
                    existing.DriverName = printer.DriverName;
                    existing.PortName = printer.PortName;
                    existing.TransportMode = printer.TransportMode;
                    existing.DirectAddress = printer.DirectAddress;
                    existing.DeviceUuid = printer.DeviceUuid;
                    existing.Enabled = true;
                    next.Add(existing);
                }
                else
                {
                    next.Add(new SharedPrinterConfig
                    {
                        QueueName = printer.Name,
                        DisplayName = printer.Name,
                        DriverName = printer.DriverName,
                        PortName = printer.PortName,
                        TransportMode = printer.TransportMode,
                        DirectAddress = printer.DirectAddress,
                        DeviceUuid = printer.DeviceUuid,
                        Enabled = true
                    });
                }
            }

            _config.SharedPrinters = next;
            UnifiedConfigStore.Save(_config);

            await Task.Delay(1400);
            await RefreshAllAsync(false);

            SetStatus($"✓ {next.Count} Druckerfreigabe(n) gespeichert");
            MessageBox.Show(
                $"{next.Count} eigene Druckerfreigabe(n) wurden gespeichert.",
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException ex)
        {
            SetStatus("Freigabe abgebrochen");
            MessageBox.Show(
                ex.Message,
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Druckerfreigaben konnten nicht gespeichert werden");
            MessageBox.Show(
                ex.Message,
                "Freigaben speichern",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task SaveNetworkPrinterSelectionAsync()
    {
        try
        {
            SetBusy("Netzwerkdruckerauswahl wird übernommen …");

            var visibleSourceIds = _networkPrinters.Nodes
                .Cast<TreeNode>()
                .Where(x => x.Tag is DevicePresence)
                .Select(x => ((DevicePresence)x.Tag!).DeviceId)
                .Where(x => x != Guid.Empty)
                .ToHashSet();

            var desired = new Dictionary<(Guid DeviceId, Guid PrinterId), NetworkPrinterTag>();

            foreach (TreeNode root in _networkPrinters.Nodes)
            {
                foreach (TreeNode child in root.Nodes)
                {
                    if (child.Tag is not NetworkPrinterTag tag || !child.Checked)
                        continue;

                    if (tag.Device.ProtocolVersion != Protocol.Version)
                    {
                        throw new InvalidOperationException(
                            $"'{tag.Device.DeviceName}' verwendet Protokoll {tag.Device.ProtocolVersion}; " +
                            $"benötigt wird P{Protocol.Version}.");
                    }

                    desired[(tag.Device.DeviceId, tag.Printer.Id)] = tag;
                }
            }

            var existingFromVisiblePeers = _config.NetworkPrinters
                .Where(x => visibleSourceIds.Contains(x.SourceDeviceId))
                .ToList();

            foreach (var mapping in existingFromVisiblePeers)
            {
                if (desired.ContainsKey((mapping.SourceDeviceId, mapping.PrinterId)))
                    continue;

                SetBusy($"'{mapping.PrinterDisplayName}' wird entfernt …");
                await PrinterInstaller.RemoveAsync(mapping);
                _config.NetworkPrinters.Remove(mapping);
                UnifiedConfigStore.Save(_config);
            }

            foreach (var tag in desired.Values)
            {
                var current = _config.NetworkPrinters.FirstOrDefault(x =>
                    x.SourceDeviceId == tag.Device.DeviceId &&
                    x.PrinterId == tag.Printer.Id);

                if (current is not null)
                {
                    var shareFallbackMatches =
                        string.Equals(
                            current.TransportMode,
                            PrinterTransport.WindowsShare,
                            StringComparison.OrdinalIgnoreCase) &&
                        PrinterTransport.IsDeviceDirect(tag.Printer.TransportMode) &&
                        string.Equals(
                            current.DirectAddress,
                            PrinterTransport.GetWindowsSharePath(
                                tag.Device.Address,
                                tag.Printer.Id),
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            current.DeviceUuid,
                            tag.Printer.DeviceUuid,
                            StringComparison.OrdinalIgnoreCase);

                    var routeChanged =
                        !shareFallbackMatches &&
                        (!string.Equals(
                             current.TransportMode,
                             tag.Printer.TransportMode,
                             StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(
                             current.DirectAddress,
                             tag.Printer.DirectAddress,
                             StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(
                             current.DeviceUuid,
                             tag.Printer.DeviceUuid,
                             StringComparison.OrdinalIgnoreCase));

                    var classDriverMismatch =
                        !PrinterTransport.IsDirect(tag.Printer.TransportMode) &&
                        PrinterTransport.IsClassDriver(tag.Printer.DriverName) &&
                        !string.Equals(
                            current.DriverName,
                            tag.Printer.DriverName,
                            StringComparison.OrdinalIgnoreCase);

                    if (!routeChanged && !classDriverMismatch)
                    {
                        current.SourceDeviceName = tag.Device.DeviceName;
                        current.PrinterDisplayName = tag.Printer.DisplayName;
                        current.Enabled = true;
                        continue;
                    }

                    SetBusy($"Druckpfad für '{tag.Printer.DisplayName}' wird aktualisiert …");
                    await PrinterInstaller.RemoveAsync(current);
                    _config.NetworkPrinters.Remove(current);
                    UnifiedConfigStore.Save(_config);
                }

                await InstallNetworkPrinterAsync(tag);
            }

            UnifiedConfigStore.Save(_config);
            await Task.Delay(1400);
            await RefreshAllAsync(true);

            SetStatus("✓ Netzwerkdruckerauswahl wurde übernommen");
            MessageBox.Show(
                "Die Netzwerkdruckerauswahl wurde übernommen.",
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException ex)
        {
            SetStatus("Administratorfreigabe abgebrochen");
            MessageBox.Show(
                ex.Message,
                "Administratorfreigabe abgebrochen",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            await RefreshAllAsync(true);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Netzwerkdruckerauswahl fehlgeschlagen");
            MessageBox.Show(
                ex.Message,
                "Druckerauswahl fehlgeschlagen",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            await RefreshAllAsync(true);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task InstallNetworkPrinterAsync(NetworkPrinterTag tag)
    {
        var hasDirectTarget =
            !string.IsNullOrWhiteSpace(tag.Printer.DirectAddress) ||
            (string.Equals(
                 tag.Printer.TransportMode,
                 PrinterTransport.Wsd,
                 StringComparison.OrdinalIgnoreCase) &&
             !string.IsNullOrWhiteSpace(tag.Printer.DeviceUuid));

        var direct =
            PrinterTransport.IsDirect(tag.Printer.TransportMode) &&
            hasDirectTarget;

        if (PrinterTransport.IsMicrosoftIppClassDriver(tag.Printer.DriverName) &&
            !direct)
        {
            throw new InvalidOperationException(
                $"'{tag.Printer.DisplayName}' verwendet den Microsoft IPP Class Driver, " +
                $"aber '{tag.Device.DeviceName}' hat keine direkt nutzbare IPP-/WSD-Adresse " +
                "oder WSD-Geräte-UUID veröffentlicht. Ein RAW-Tunnel wird für diesen Treiber " +
                "nicht angelegt.");
        }

        ReusableDirectPrinter? reusable = null;

        if (direct)
        {
            reusable = await PrinterInstaller.FindReusableDirectPrinterAsync(
                tag.Printer.TransportMode,
                tag.Printer.DirectAddress,
                tag.Printer.DeviceUuid,
                tag.Printer.PortName,
                tag.Printer.DisplayName);
        }

        string driver;

        if (direct)
        {
            driver = reusable?.DriverName ?? tag.Printer.DriverName;
        }
        else
        {
            var drivers = await PrinterInstaller.GetDriverNamesAsync();
            if (drivers.Count == 0)
            {
                throw new InvalidOperationException(
                    "Auf diesem PC wurden keine Druckertreiber gefunden.");
            }

            driver = drivers.FirstOrDefault(x =>
                         x.Equals(
                             tag.Printer.DriverName,
                             StringComparison.OrdinalIgnoreCase))
                     ?? "";

            if (PrinterTransport.IsClassDriver(tag.Printer.DriverName))
            {
                if (string.IsNullOrWhiteSpace(driver))
                {
                    SetBusy(
                        $"Treiber '{tag.Printer.DriverName}' wird aus dem Windows-Treiberspeicher installiert …");

                    await PrinterInstaller.EnsureDriverInstalledAsync(
                        tag.Printer.DriverName);

                    drivers = await PrinterInstaller.GetDriverNamesAsync();
                    driver = drivers.FirstOrDefault(x =>
                                 x.Equals(
                                     tag.Printer.DriverName,
                                     StringComparison.OrdinalIgnoreCase))
                             ?? "";
                }

                if (string.IsNullOrWhiteSpace(driver))
                {
                    throw new InvalidOperationException(
                        $"Der erforderliche Treiber '{tag.Printer.DriverName}' ist auf diesem Gerät nicht verfügbar.");
                }
            }
            else if (string.IsNullOrWhiteSpace(driver))
            {
                driver = ChooseDriver(drivers, tag.Printer.DriverName) ?? "";
                if (string.IsNullOrWhiteSpace(driver))
                {
                    throw new OperationCanceledException(
                        "Die Treiberauswahl wurde abgebrochen.");
                }
            }
        }

        var shortDevice = tag.Device.DeviceId.ToString("N")[..8];
        var shortPrinter = tag.Printer.Id.ToString("N")[..8];
        var localPort = direct ? 0 : AllocatePort();

        var portName = reusable?.PortName ??
            (direct
                ? $"SimplePrintDirect_{shortDevice}_{shortPrinter}"
                : $"SimplePrint_{shortDevice}_{shortPrinter}");

        var localName = reusable?.Name ??
            UniqueLocalName($"{tag.Printer.DisplayName} (SimplePrint)");

        var mapping = new NetworkPrinterMapping
        {
            SourceDeviceId = tag.Device.DeviceId,
            PrinterId = tag.Printer.Id,
            SourceDeviceName = tag.Device.DeviceName,
            PrinterDisplayName = tag.Printer.DisplayName,
            LocalPrinterName = localName,
            DriverName = driver,
            PortName = portName,
            LocalProxyPort = localPort,
            TransportMode = direct
                ? tag.Printer.TransportMode
                : PrinterTransport.Tunnel,
            DirectAddress = direct
                ? tag.Printer.DirectAddress
                : "",
            DeviceUuid = direct
                ? tag.Printer.DeviceUuid
                : "",
            UseExistingQueue = reusable is not null,
            Enabled = true
        };

        _config.NetworkPrinters.Add(mapping);
        UnifiedConfigStore.Save(_config);

        try
        {
            if (!direct)
            {
                SetBusy(
                    $"Lokaler SimplePrint-Proxy auf Port {localPort} wird gestartet …");

                if (!await WaitForLocalProxyAsync(
                        localPort,
                        TimeSpan.FromSeconds(8)))
                {
                    throw new InvalidOperationException(
                        $"Der SimplePrint-Dienst lauscht nicht auf 127.0.0.1:{localPort}. " +
                        "Die Windows-Druckerqueue wurde deshalb nicht angelegt.");
                }
            }
            else if (mapping.UseExistingQueue)
            {
                SetBusy(
                    $"Vorhandene Windows-Druckerqueue '{mapping.LocalPrinterName}' wird verwendet …");
            }
            else
            {
                SetBusy(
                    $"Direkte {mapping.TransportMode}-Druckerqueue wird eingerichtet …");
            }

            try
            {
                await PrinterInstaller.InstallAsync(mapping);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception directError)
                when (direct &&
                      !mapping.UseExistingQueue &&
                      PrinterTransport.IsDeviceDirect(mapping.TransportMode))
            {
                var originalMode = mapping.TransportMode;
                var sharePath = PrinterTransport.GetWindowsSharePath(
                    tag.Device.Address,
                    tag.Printer.Id);

                SetBusy(
                    $"Direkte {originalMode}-Verbindung nicht möglich. " +
                    "SimplePrint-Druckerfreigabe wird als Fallback versucht …");

                mapping.TransportMode = PrinterTransport.WindowsShare;
                mapping.DirectAddress = sharePath;
                mapping.LocalPrinterName = sharePath;
                mapping.PortName = sharePath;
                mapping.LocalProxyPort = 0;
                mapping.UseExistingQueue = false;
                UnifiedConfigStore.Save(_config);

                try
                {
                    await PrinterInstaller.InstallAsync(mapping);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception shareError)
                {
                    throw new InvalidOperationException(
                        $"Der Drucker konnte weder direkt per {originalMode} noch über die " +
                        $"SimplePrint-Freigabe verbunden werden.\r\n\r\n" +
                        $"Direkte Verbindung: {directError.Message}\r\n\r\n" +
                        $"Freigabe {sharePath}: {shareError.Message}",
                        shareError);
                }
            }
        }
        catch
        {
            _config.NetworkPrinters.Remove(mapping);
            UnifiedConfigStore.Save(_config);
            throw;
        }
    }

    private static async Task<bool> WaitForLocalProxyAsync(
        int port,
        TimeSpan timeout)
    {
        var started = DateTime.UtcNow;

        while (DateTime.UtcNow - started < timeout)
        {
            try
            {
                var listeners = System.Net.NetworkInformation.IPGlobalProperties
                    .GetIPGlobalProperties()
                    .GetActiveTcpListeners();

                if (listeners.Any(x =>
                        x.Port == port &&
                        System.Net.IPAddress.IsLoopback(x.Address)))
                {
                    return true;
                }
            }
            catch
            {
            }

            await Task.Delay(250);
        }

        return false;
    }

    private int AllocatePort()
    {
        var used = _config.NetworkPrinters
            .Where(x => x.LocalProxyPort > 0)
            .Select(x => x.LocalProxyPort)
            .ToHashSet();

        for (var port = _config.LocalPortStart;
             port <= _config.LocalPortEnd;
             port++)
        {
            if (used.Contains(port))
                continue;

            try
            {
                var listener = new System.Net.Sockets.TcpListener(
                    System.Net.IPAddress.Loopback,
                    port);

                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
            }
        }

        throw new InvalidOperationException(
            "Kein freier lokaler SimplePrint-Port verfügbar.");
    }

    private string UniqueLocalName(string requested)
    {
        var names = _config.NetworkPrinters
            .Select(x => x.LocalPrinterName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!names.Contains(requested))
            return requested;

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{requested} ({i})";
            if (!names.Contains(candidate))
                return candidate;
        }

        return requested + " (weitere)";
    }

    private string? ChooseDriver(
        List<string> drivers,
        string suggested)
    {
        using var dialog = new Form
        {
            Text = "Druckertreiber auswählen",
            Width = 700,
            Height = 210,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var info = new Label
        {
            Left = 20,
            Top = 15,
            Width = 640,
            Height = 40,
            Text =
                $"Das Quellgerät verwendet '{suggested}'. " +
                "Wähle den passenden lokal installierten Treiber:"
        };

        var combo = new ComboBox
        {
            Left = 20,
            Top = 65,
            Width = 640,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        combo.Items.AddRange(drivers.Cast<object>().ToArray());
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;

        var ok = new Button
        {
            Text = "Verwenden",
            Left = 470,
            Top = 110,
            Width = 90,
            DialogResult = DialogResult.OK
        };

        var cancel = new Button
        {
            Text = "Abbrechen",
            Left = 570,
            Top = 110,
            Width = 90,
            DialogResult = DialogResult.Cancel
        };

        dialog.Controls.AddRange([info, combo, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog(this) == DialogResult.OK
            ? combo.SelectedItem?.ToString()
            : null;
    }

    private void RefreshNetworkPrinterTree()
    {
        _networkPrinters.BeginUpdate();
        try
        {
            _networkPrinters.Nodes.Clear();

            var activeIds = _peers
                .Select(x => x.DeviceId)
                .ToHashSet();

            foreach (var peer in _peers.Where(x =>
                         x.Printers.Count > 0 ||
                         _config.NetworkPrinters.Any(m =>
                             m.Enabled &&
                             m.SourceDeviceId == x.DeviceId)))
            {
                var device = new TreeNode(
                    $"{peer.DeviceName}  ·  {peer.Address}")
                {
                    Tag = peer,
                    NodeFont = new Font(_networkPrinters.Font, FontStyle.Bold)
                };

                var advertisedIds = new HashSet<Guid>();

                foreach (var printer in peer.Printers
                             .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    advertisedIds.Add(printer.Id);

                    var selected = _config.NetworkPrinters.Any(x =>
                        x.Enabled &&
                        x.SourceDeviceId == peer.DeviceId &&
                        x.PrinterId == printer.Id);

                    device.Nodes.Add(new TreeNode(
                        $"{printer.DisplayName}   [{printer.Status}]")
                    {
                        Tag = new NetworkPrinterTag(peer, printer),
                        Checked = selected
                    });
                }

                foreach (var mapping in _config.NetworkPrinters
                             .Where(x =>
                                 x.Enabled &&
                                 x.SourceDeviceId == peer.DeviceId &&
                                 !advertisedIds.Contains(x.PrinterId))
                             .OrderBy(
                                 x => x.PrinterDisplayName,
                                 StringComparer.CurrentCultureIgnoreCase))
                {
                    var noLongerShared = new DiscoveredPrinter
                    {
                        Id = mapping.PrinterId,
                        DisplayName = mapping.PrinterDisplayName,
                        DriverName = mapping.DriverName,
                        PortName = mapping.PortName,
                        TransportMode = mapping.TransportMode,
                        DirectAddress = mapping.DirectAddress,
                        DeviceUuid = mapping.DeviceUuid,
                        Status = "nicht mehr freigegeben"
                    };

                    device.Nodes.Add(new TreeNode(
                        $"{noLongerShared.DisplayName}   [nicht mehr freigegeben]")
                    {
                        Tag = new NetworkPrinterTag(peer, noLongerShared),
                        Checked = true,
                        ForeColor = SystemColors.GrayText
                    });
                }

                device.Expand();
                _networkPrinters.Nodes.Add(device);
            }

            foreach (var group in _config.NetworkPrinters
                         .Where(x =>
                             x.Enabled &&
                             x.SourceDeviceId != Guid.Empty &&
                             !activeIds.Contains(x.SourceDeviceId))
                         .GroupBy(x => x.SourceDeviceId)
                         .OrderBy(x =>
                             x.First().SourceDeviceName,
                             StringComparer.CurrentCultureIgnoreCase))
            {
                var first = group.First();
                var offlinePeer = new DevicePresence
                {
                    DeviceId = group.Key,
                    DeviceName = string.IsNullOrWhiteSpace(first.SourceDeviceName)
                        ? group.Key.ToString()
                        : first.SourceDeviceName,
                    Address = "",
                    AppVersion = "nicht erreichbar",
                    ProtocolVersion = Protocol.Version,
                    GatewayPort = Protocol.DefaultGatewayPort,
                    DiagnosticsPort = Protocol.DefaultDiagnosticsPort,
                    LastSeen = DateTimeOffset.MinValue
                };

                var device = new TreeNode(
                    $"{offlinePeer.DeviceName}  ·  nicht erreichbar")
                {
                    Tag = offlinePeer,
                    NodeFont = new Font(_networkPrinters.Font, FontStyle.Bold),
                    ForeColor = SystemColors.GrayText
                };

                foreach (var mapping in group.OrderBy(
                             x => x.PrinterDisplayName,
                             StringComparer.CurrentCultureIgnoreCase))
                {
                    var printer = new DiscoveredPrinter
                    {
                        Id = mapping.PrinterId,
                        DisplayName = mapping.PrinterDisplayName,
                        DriverName = mapping.DriverName,
                        PortName = mapping.PortName,
                        TransportMode = mapping.TransportMode,
                        DirectAddress = mapping.DirectAddress,
                        DeviceUuid = mapping.DeviceUuid,
                        Status = "Quellgerät nicht erreichbar"
                    };

                    device.Nodes.Add(new TreeNode(
                        $"{printer.DisplayName}   [nicht erreichbar]")
                    {
                        Tag = new NetworkPrinterTag(offlinePeer, printer),
                        Checked = true,
                        ForeColor = SystemColors.GrayText
                    });
                }

                device.Expand();
                _networkPrinters.Nodes.Add(device);
            }

            if (_networkPrinters.Nodes.Count == 0)
            {
                _networkPrinters.Nodes.Add(
                    new TreeNode("Keine aktiven SimplePrint-Drucker gefunden."));
            }
        }
        finally
        {
            _networkPrinters.EndUpdate();
        }
    }

    private void RefreshRelationshipGrids()
    {
        var serverIds = _config.NetworkPrinters
            .Where(x => x.Enabled)
            .Select(x => x.SourceDeviceId)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToHashSet();

        _servers.Rows.Clear();
        foreach (var peer in _peers.Where(x => serverIds.Contains(x.DeviceId)))
        {
            var printerNames = _config.NetworkPrinters
                .Where(x => x.Enabled && x.SourceDeviceId == peer.DeviceId)
                .Select(x => x.PrinterDisplayName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.CurrentCultureIgnoreCase);

            _servers.Rows.Add(
                peer.DeviceName,
                peer.Address,
                peer.AppVersion,
                string.Join(", ", printerNames),
                FormatSeen(peer.LastSeen));
        }

        _clients.Rows.Clear();
        foreach (var peer in _peers.Where(x =>
                     x.Subscriptions.Any(s => s.SourceDeviceId == _config.DeviceId)))
        {
            var usedIds = peer.Subscriptions
                .Where(x => x.SourceDeviceId == _config.DeviceId)
                .Select(x => x.PrinterId)
                .ToHashSet();

            var printerNames = _config.SharedPrinters
                .Where(x => usedIds.Contains(x.Id))
                .Select(x => string.IsNullOrWhiteSpace(x.DisplayName) ? x.QueueName : x.DisplayName);

            _clients.Rows.Add(
                peer.DeviceName,
                peer.Address,
                peer.AppVersion,
                string.Join(", ", printerNames),
                FormatSeen(peer.LastSeen));
        }
    }

    private void OpenSelectedOwnPrinterQueue()
    {
        if (_ownPrinters.SelectedRows.Count == 0 ||
            _ownPrinters.SelectedRows[0].Tag is not LocalPrinterInfo printer)
        {
            MessageBox.Show(
                "Bitte zuerst einen eigenen Drucker markieren.",
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            UnifiedPrinterHelper.OpenQueue(printer.Name);
            SetStatus($"✓ Warteschlange '{printer.Name}' geöffnet");
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

    private void PrintSelectedOwnPrinterTestPage()
    {
        if (_ownPrinters.SelectedRows.Count == 0 ||
            _ownPrinters.SelectedRows[0].Tag is not LocalPrinterInfo printer)
        {
            MessageBox.Show(
                "Bitte zuerst einen eigenen Drucker markieren.",
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            UnifiedPrinterHelper.PrintTestPage(printer.Name);
            SetStatus($"✓ Testseite für '{printer.Name}' gestartet");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Testseite",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private NetworkPrinterMapping? GetSelectedNetworkMapping()
    {
        if (_networkPrinters.SelectedNode?.Tag is not NetworkPrinterTag tag)
            return null;

        return _config.NetworkPrinters.FirstOrDefault(x =>
            x.Enabled &&
            x.SourceDeviceId == tag.Device.DeviceId &&
            x.PrinterId == tag.Printer.Id);
    }

    private void OpenSelectedNetworkPrinterQueue()
    {
        var mapping = GetSelectedNetworkMapping();
        if (mapping is null)
        {
            MessageBox.Show(
                "Bitte zuerst einen auf diesem Gerät installierten Netzwerkdrucker markieren.",
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            UnifiedPrinterHelper.OpenQueue(mapping.LocalPrinterName);
            SetStatus($"✓ Warteschlange '{mapping.PrinterDisplayName}' geöffnet");
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

    private void PrintSelectedNetworkPrinterTestPage()
    {
        var mapping = GetSelectedNetworkMapping();
        if (mapping is null)
        {
            MessageBox.Show(
                "Bitte zuerst einen auf diesem Gerät installierten Netzwerkdrucker markieren.",
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            UnifiedPrinterHelper.PrintTestPage(mapping.LocalPrinterName);
            SetStatus($"✓ Testseite für '{mapping.PrinterDisplayName}' gestartet");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Testseite",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ClearCompletedJobs()
    {
        var jobs = JsonStore.LoadOrCreate(
            AppPaths.DeviceJobs,
            () => new List<PrintJobRecord>());

        var completed = new HashSet<string>(
            ["Gedruckt", "Abgeschlossen", "Ignoriert", "Fehler"],
            StringComparer.OrdinalIgnoreCase);

        var count = jobs.RemoveAll(x => completed.Contains(x.Status));
        JsonStore.Save(AppPaths.DeviceJobs, jobs);
        RefreshJobsGrid();
        SetStatus($"✓ {count} abgeschlossene Druckaufträge gelöscht");
    }

    private void ClearAllJobs()
    {
        if (MessageBox.Show(
                "Die gesamte gespeicherte Druckauftragshistorie dieses Geräts löschen?",
                "Druckaufträge löschen",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        JsonStore.Save(AppPaths.DeviceJobs, new List<PrintJobRecord>());
        RefreshJobsGrid();
        SetStatus("✓ Druckauftragshistorie gelöscht");
    }

    private void RefreshJobsGrid()
    {
        List<PrintJobRecord> jobs;
        try
        {
            jobs = JsonStore.LoadOrCreate(
                AppPaths.DeviceJobs,
                () => new List<PrintJobRecord>());
        }
        catch
        {
            jobs = [];
        }

        _jobs.Rows.Clear();

        foreach (var job in jobs
                     .OrderByDescending(x => x.UpdatedAt)
                     .Take(200))
        {
            var incoming = job.ServerId == _config.DeviceId;
            var peer = incoming ? job.ClientName : job.ServerName;

            _jobs.Rows.Add(
                job.UpdatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss"),
                incoming ? "Eingehend" : "Ausgehend",
                peer,
                job.PrinterName,
                job.Status,
                job.Bytes.ToString("N0"),
                job.Message);
        }
    }

    private void RefreshDiagnosticsTree()
    {
        _diagnostics.BeginUpdate();
        try
        {
            var checkedIds = CollectCheckedDiagnosticDeviceIds();
            var localWasChecked =
                _diagnostics.Nodes.Count == 0 ||
                _diagnostics.Nodes
                    .Cast<TreeNode>()
                    .Any(x => x.Tag is string s && s == "local" && x.Checked);

            _diagnostics.Nodes.Clear();

            var local = new TreeNode(
                $"Dieses Gerät · {_config.DeviceName}")
            {
                Tag = "local",
                Checked = localWasChecked
            };
            _diagnostics.Nodes.Add(local);

            var serverRoot = new TreeNode("Server")
            {
                Tag = "group:servers"
            };
            var serverIds = _config.NetworkPrinters
                .Where(x => x.Enabled)
                .Select(x => x.SourceDeviceId)
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToHashSet();

            foreach (var peer in _peers.Where(x => serverIds.Contains(x.DeviceId)))
            {
                serverRoot.Nodes.Add(new TreeNode(
                    $"{peer.DeviceName} · {peer.Address}")
                {
                    Tag = peer,
                    Checked = checkedIds.Contains(peer.DeviceId)
                });
            }

            var clientRoot = new TreeNode("Clients")
            {
                Tag = "group:clients"
            };
            foreach (var peer in _peers.Where(x =>
                         x.Subscriptions.Any(s => s.SourceDeviceId == _config.DeviceId)))
            {
                clientRoot.Nodes.Add(new TreeNode(
                    $"{peer.DeviceName} · {peer.Address}")
                {
                    Tag = peer,
                    Checked = checkedIds.Contains(peer.DeviceId)
                });
            }

            if (serverRoot.Nodes.Count > 0)
            {
                serverRoot.Expand();
                _diagnostics.Nodes.Add(serverRoot);
            }

            if (clientRoot.Nodes.Count > 0)
            {
                clientRoot.Expand();
                _diagnostics.Nodes.Add(clientRoot);
            }
        }
        finally
        {
            _diagnostics.EndUpdate();
        }
    }

    private HashSet<Guid> CollectCheckedDiagnosticDeviceIds()
    {
        var result = new HashSet<Guid>();

        foreach (TreeNode root in _diagnostics.Nodes)
        {
            if (root.Tag is DevicePresence peer && root.Checked)
                result.Add(peer.DeviceId);

            foreach (TreeNode child in root.Nodes)
            {
                if (child.Tag is DevicePresence childPeer && child.Checked)
                    result.Add(childPeer.DeviceId);
            }
        }

        return result;
    }

    private async Task CreateDiagnosticsAsync()
    {
        var includeLocal = _diagnostics.Nodes
            .Cast<TreeNode>()
            .Any(x =>
                x.Tag is string tag &&
                tag == "local" &&
                x.Checked);

        var selectedPeers = new Dictionary<Guid, DevicePresence>();

        foreach (TreeNode root in _diagnostics.Nodes)
        {
            foreach (TreeNode child in root.Nodes)
            {
                if (child.Tag is DevicePresence peer &&
                    child.Checked &&
                    peer.DeviceId != Guid.Empty)
                {
                    selectedPeers[peer.DeviceId] = peer;
                }
            }
        }

        if (!includeLocal && selectedPeers.Count == 0)
        {
            MessageBox.Show(
                "Bitte mindestens ein Gerät für das Diagnosepaket auswählen.",
                "SimplePrint Diagnose",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var save = new SaveFileDialog
        {
            Filter = "ZIP-Datei|*.zip",
            FileName = $"SimplePrint-Diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };

        if (save.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            SetBusy("Diagnosepakete werden gesammelt …");

            var manifest = new List<string>
            {
                $"SimplePrint Diagnose · {DateTimeOffset.Now:O}",
                $"Erstellt auf: {_config.DeviceName} ({_config.DeviceId})",
                $"Dieses Gerät ausgewählt: {(includeLocal ? "Ja" : "Nein")}",
                $"Entfernte Geräte ausgewählt: {selectedPeers.Count}",
                ""
            };

            await using var output = new FileStream(
                save.FileName,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None);

            using var zip = new ZipArchive(
                output,
                ZipArchiveMode.Create,
                leaveOpen: false);

            var included = 0;
            var failed = 0;

            if (includeLocal)
            {
                SetBusy($"Diagnose von '{_config.DeviceName}' wird erstellt …");

                var localArchive = await UnifiedDiagnosticsBuilder.CreateArchiveAsync(
                    _config,
                    _peers);

                await AddArchiveEntryAsync(
                    zip,
                    $"devices/{MakeSafeEntryName(_config.DeviceName)}-{_config.DeviceId.ToString("N")[..8]}.zip",
                    localArchive);

                included++;
                manifest.Add(
                    $"OK | Dieses Gerät | {_config.DeviceName} | {_config.DeviceId}");
            }

            if (selectedPeers.Count > 0)
            {
                using var limiter = new SemaphoreSlim(4, 4);

                var tasks = selectedPeers.Values.Select(async peer =>
                {
                    await limiter.WaitAsync();
                    try
                    {
                        return await FetchRemoteDiagnosticsAsync(peer);
                    }
                    finally
                    {
                        limiter.Release();
                    }
                });

                var results = await Task.WhenAll(tasks);

                foreach (var result in results.OrderBy(
                             x => x.Device.DeviceName,
                             StringComparer.CurrentCultureIgnoreCase))
                {
                    var device = result.Device;
                    var safeName = MakeSafeEntryName(device.DeviceName);
                    var shortId = device.DeviceId.ToString("N")[..8];

                    if (result.Archive is not null)
                    {
                        await AddArchiveEntryAsync(
                            zip,
                            $"devices/{safeName}-{shortId}.zip",
                            result.Archive);

                        included++;
                        manifest.Add(
                            $"OK | {device.DeviceName} | {device.DeviceId} | " +
                            $"{device.Address}:{device.DiagnosticsPort} | Version {device.AppVersion}");
                    }
                    else
                    {
                        failed++;
                        var error =
                            result.Error ??
                            "Das Gerät hat kein Diagnosepaket geliefert.";

                        var errorEntry = zip.CreateEntry(
                            $"errors/{safeName}-{shortId}.txt",
                            CompressionLevel.Optimal);

                        await using (var stream = errorEntry.Open())
                        await using (var writer = new StreamWriter(stream, Encoding.UTF8))
                        {
                            await writer.WriteLineAsync(
                                $"Gerät: {device.DeviceName}");
                            await writer.WriteLineAsync(
                                $"DeviceId: {device.DeviceId}");
                            await writer.WriteLineAsync(
                                $"Adresse: {device.Address}:{device.DiagnosticsPort}");
                            await writer.WriteLineAsync(
                                $"Fehler: {error}");
                        }

                        manifest.Add(
                            $"FEHLER | {device.DeviceName} | {device.DeviceId} | " +
                            $"{device.Address}:{device.DiagnosticsPort} | {error}");
                    }
                }
            }

            var manifestEntry = zip.CreateEntry(
                "manifest.txt",
                CompressionLevel.Optimal);

            await using (var stream = manifestEntry.Open())
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                await writer.WriteAsync(
                    string.Join(Environment.NewLine, manifest));
            }

            SetStatus(
                failed == 0
                    ? $"✓ Diagnosepaket erstellt · {included} Gerät(e) enthalten"
                    : $"⚠ Diagnosepaket erstellt · {included} enthalten · {failed} nicht abrufbar");

            MessageBox.Show(
                failed == 0
                    ? $"Diagnosepaket wurde erstellt.\r\n\r\nEnthaltene Geräte: {included}"
                    : $"Diagnosepaket wurde erstellt.\r\n\r\n" +
                      $"Enthaltene Geräte: {included}\r\n" +
                      $"Nicht abrufbar: {failed}\r\n\r\n" +
                      "Fehlgeschlagene Abrufe sind im Ordner 'errors' dokumentiert.",
                "SimplePrint Diagnose",
                MessageBoxButtons.OK,
                failed == 0
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Diagnosepaket konnte nicht erstellt werden");
            MessageBox.Show(
                ex.Message,
                "SimplePrint Diagnose",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }

    private async Task<RemoteDiagnosticsFetch> FetchRemoteDiagnosticsAsync(
        DevicePresence device)
    {
        if (device.DiagnosticsPort <= 0)
        {
            return new RemoteDiagnosticsFetch(
                device,
                null,
                "Das Gerät veröffentlicht keinen Diagnose-Port.");
        }

        if (device.ProtocolVersion != Protocol.Version)
        {
            return new RemoteDiagnosticsFetch(
                device,
                null,
                $"Inkompatibles Protokoll P{device.ProtocolVersion}; benötigt wird P{Protocol.Version}.");
        }

        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(70));

            using var tcp = new TcpClient { NoDelay = true };

            await tcp.ConnectAsync(
                device.Address,
                device.DiagnosticsPort,
                timeout.Token);

            using var stream = tcp.GetStream();

            await stream.WriteAsync(
                Protocol.CreateDiagnosticsRequest(_config.DeviceId),
                timeout.Token);

            await stream.FlushAsync(timeout.Token);

            var response = await Protocol.ReadDiagnosticsResponseAsync(
                stream,
                timeout.Token);

            return response.Archive is null
                ? new RemoteDiagnosticsFetch(
                    device,
                    null,
                    response.Error ?? "Das Gerät hat kein Diagnosepaket geliefert.")
                : new RemoteDiagnosticsFetch(
                    device,
                    response.Archive,
                    null);
        }
        catch (Exception ex)
        {
            return new RemoteDiagnosticsFetch(
                device,
                null,
                ex.Message);
        }
    }

    private static async Task AddArchiveEntryAsync(
        ZipArchive zip,
        string entryName,
        byte[] archive)
    {
        var entry = zip.CreateEntry(
            entryName,
            CompressionLevel.NoCompression);

        await using var stream = entry.Open();
        await stream.WriteAsync(archive);
    }

    private static string MakeSafeEntryName(string value)
    {
        var safe = string.IsNullOrWhiteSpace(value)
            ? "Gerät"
            : value.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');

        return safe
            .Replace('/', '_')
            .Replace('\\', '_');
    }

    private void RefreshSettings()
    {
        SetNamedLabel("SettingsDeviceName", _config.DeviceName);
        SetNamedLabel("SettingsDiscovery", _config.DiscoveryPort.ToString());
        SetNamedLabel("SettingsGateway", _config.GatewayPort.ToString());
        SetNamedLabel("SettingsDiagnostics", _config.DiagnosticsPort.ToString());
    }

    private void SetNamedLabel(string name, string text)
    {
        var matches = Controls.Find(name, true);
        if (matches.FirstOrDefault() is Label label)
            label.Text = text;
    }

    private async Task RefreshServiceStatusAsync()
    {
        const string script = """
$service = Get-Service -Name 'SimplePrint' -ErrorAction SilentlyContinue
if($null -eq $service) { 'Nicht installiert' } else { [string]$service.Status }
""";

        var result = await PowerShellRunner.RunAsync(script);
        var status = result.StdOut.Trim();

        _serviceStatus.Text = status.Equals("Running", StringComparison.OrdinalIgnoreCase)
            ? "Läuft"
            : string.IsNullOrWhiteSpace(status)
                ? "Unbekannt"
                : status;

        RefreshOverview();
    }

    private static string FormatSeen(DateTimeOffset value)
    {
        var age = DateTimeOffset.Now - value;

        if (age < TimeSpan.FromSeconds(10))
            return "gerade eben";

        if (age < TimeSpan.FromMinutes(1))
            return $"vor {(int)age.TotalSeconds} s";

        return $"vor {(int)age.TotalMinutes} min";
    }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false
    };

    private static FlowLayoutPanel BottomButtons() => new()
    {
        Dock = DockStyle.Bottom,
        Height = 52,
        Padding = new Padding(8)
    };

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(0, 30),
            Text = text
        };
        button.Click += click;
        return button;
    }

    private static Label BoldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
        Margin = new Padding(3, 3, 3, 8)
    };

    private static Label ValueLabel() => new()
    {
        AutoSize = true,
        Text = "0",
        Margin = new Padding(3, 3, 3, 8)
    };

    private static Label Spacer() => new()
    {
        AutoSize = true,
        Text = "    "
    };

    private static void AddOverviewRow(
        TableLayoutPanel panel,
        int row,
        string title,
        Label value)
    {
        panel.Controls.Add(BoldLabel(title), 0, row);
        panel.Controls.Add(value, 1, row);
    }

    private void SetBusy(string text)
    {
        _operationStatus.Text = text;
        _operationProgress.Visible = true;
    }

    private void SetIdle()
    {
        _operationProgress.Visible = false;
    }

    private void SetStatus(string text)
    {
        _operationStatus.Text = text;
        SetIdle();
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
