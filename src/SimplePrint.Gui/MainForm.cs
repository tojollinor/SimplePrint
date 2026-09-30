using System.Text.Json;
using SimplePrint.Common;

namespace SimplePrint.Gui;

public sealed class MainForm : Form
{
    private sealed class LocalPrinterRow
    {
        public string Name { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string PortName { get; set; } = "";
        public string PrinterStatus { get; set; } = "";
    }

    private readonly Label _deviceName = new() { AutoSize = true };
    private readonly Label _version = new() { AutoSize = true };
    private readonly Label _serviceStatus = new() { AutoSize = true };

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

        tab.Controls.Add(_diagnostics);
        tab.Controls.Add(info);
        return tab;
    }

    private TabPage CreateSettingsTab()
    {
        var tab = new TabPage("Einstellungen");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(20),
            ColumnCount = 2,
            RowCount = 5
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(BoldLabel("Gerätename"), 0, 0);
        panel.Controls.Add(new Label
        {
            Name = "SettingsDeviceName",
            AutoSize = true,
            Text = Environment.MachineName
        }, 1, 0);

        panel.Controls.Add(BoldLabel("Discovery-Port"), 0, 1);
        panel.Controls.Add(new Label
        {
            Name = "SettingsDiscovery",
            AutoSize = true,
            Text = Protocol.DefaultDiscoveryPort.ToString()
        }, 1, 1);

        panel.Controls.Add(BoldLabel("Print-Gateway"), 0, 2);
        panel.Controls.Add(new Label
        {
            Name = "SettingsGateway",
            AutoSize = true,
            Text = Protocol.DefaultGatewayPort.ToString()
        }, 1, 2);

        panel.Controls.Add(BoldLabel("Diagnose-Port"), 0, 3);
        panel.Controls.Add(new Label
        {
            Name = "SettingsDiagnostics",
            AutoSize = true,
            Text = Protocol.DefaultDiagnosticsPort.ToString()
        }, 1, 3);

        panel.Controls.Add(BoldLabel("Konfiguration"), 0, 4);
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Text = AppPaths.DeviceConfig
        }, 1, 4);

        tab.Controls.Add(panel);
        return tab;
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

    private async Task RefreshOwnPrintersAsync(bool showBusy = true)
    {
        try
        {
            if (showBusy)
                SetBusy("Lokale Drucker werden gelesen …");

            const string script = """
$items = @(
  Get-Printer -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{
      Name = [string]$_.Name
      DriverName = [string]$_.DriverName
      PortName = [string]$_.PortName
      PrinterStatus = [string]$_.PrinterStatus
    }
  }
)
ConvertTo-Json -InputObject $items -Compress -Depth 3
""";

            var result = await PowerShellRunner.RunAsync(script);
            var printers = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut)
                ? JsonSerializer.Deserialize<List<LocalPrinterRow>>(
                      result.StdOut.Trim(),
                      JsonStore.Options) ?? []
                : [];

            _ownPrinters.Rows.Clear();

            foreach (var printer in printers.OrderBy(
                         x => x.Name,
                         StringComparer.CurrentCultureIgnoreCase))
            {
                var shared = _config.SharedPrinters.Any(x =>
                    x.Enabled &&
                    x.QueueName.Equals(
                        printer.Name,
                        StringComparison.OrdinalIgnoreCase));

                _ownPrinters.Rows.Add(
                    shared,
                    printer.Name,
                    printer.DriverName,
                    printer.PortName,
                    printer.PrinterStatus);
            }

            if (showBusy)
                SetStatus($"✓ {printers.Count} lokale Drucker geladen");
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

    private void RefreshNetworkPrinterTree()
    {
        _networkPrinters.BeginUpdate();
        try
        {
            _networkPrinters.Nodes.Clear();

            foreach (var peer in _peers.Where(x => x.Printers.Count > 0))
            {
                var device = new TreeNode(
                    $"{peer.DeviceName}  ·  {peer.Address}")
                {
                    Tag = peer,
                    NodeFont = new Font(_networkPrinters.Font, FontStyle.Bold)
                };

                foreach (var printer in peer.Printers
                             .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    var selected = _config.NetworkPrinters.Any(x =>
                        x.Enabled &&
                        x.SourceDeviceId == peer.DeviceId &&
                        x.PrinterId == printer.Id);

                    device.Nodes.Add(new TreeNode(
                        $"{printer.DisplayName}   [{printer.Status}]")
                    {
                        Tag = printer,
                        Checked = selected
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

            var serverRoot = new TreeNode("Server");
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

            var clientRoot = new TreeNode("Clients");
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
