using System.Reflection;
using SimplePrint.Common;

namespace SimplePrint.Gui;

/// <summary>
/// Hauptfenster. Die Oberfläche ist auf mehrere Dateien aufgeteilt:
/// MainForm.cs (Kern, Aktualisierung), .Overview, .Printers, .Devices,
/// .Jobs, .Diagnostics und .Settings (je ein Reiter).
/// </summary>
public sealed partial class MainForm : Form
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

    private readonly ToolStripStatusLabel _operationStatus = new() { Text = "Bereit" };
    private readonly ToolStripProgressBar _operationProgress = new()
    {
        Style = ProgressBarStyle.Marquee,
        Visible = false,
        Width = 110
    };

    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 5000 };
    private readonly NotifyIcon _tray;

    // Buttons, die während einer laufenden Aktion gesperrt werden
    // (verhindert Doppelklicks und damit z. B. Doppelinstallationen).
    private readonly List<Button> _actionButtons = [];

    private TabControl? _tabs;
    private SimplePrintConfig _config = new();
    private List<DevicePresence> _peers = [];
    private List<DevicePresence> _peerCache = [];
    private bool _allowExit;
    private bool _updateCheckRunning;
    private bool _operationRunning;
    private bool _refreshRunning;
    private string? _lastOfferedUpdate;
    private string _trayLevel = "";
    private DateTime _configWriteUtc = DateTime.MinValue;
    private DateTime _peersWriteUtc = DateTime.MinValue;

    public MainForm()
    {
        Text = "SimplePrint";
        Width = 1000;
        Height = 700;
        MinimumSize = new Size(820, 580);
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
        trayMenu.Items.Add("Aktualisieren", null, async (_, _) => await RefreshRequestedAsync());
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
            // Ein zweiter Programmstart holt dieses Fenster nach vorne.
            Program.RegisterShowRequestHandler(() =>
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke(new Action(ShowFromTray));
                }
                catch
                {
                }
            });

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
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateOverviewTab());
        tabs.TabPages.Add(CreatePrintersTab());
        tabs.TabPages.Add(CreateDevicesTab());
        tabs.TabPages.Add(CreateJobsTab());
        tabs.TabPages.Add(CreateDiagnosticsTab());
        tabs.TabPages.Add(CreateSettingsTab());

        tabs.Selected += (_, e) =>
        {
            switch (e.TabPage?.Text)
            {
                case "Druckaufträge":
                    RefreshJobsGrid();
                    break;

                case "Geräte":
                    RefreshDevicesGrid(true);
                    _ = RefreshLatestReleaseAsync(false);
                    break;
            }
        };

        _tabs = tabs;
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

        var refreshButton = MakeButton(
            "Aktualisieren",
            async (_, _) => await RefreshRequestedAsync());
        _actionButtons.Add(refreshButton);
        buttons.Controls.Add(refreshButton);

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

    // ---------- Zustand laden, speichern, aktualisieren ----------

    private static DateTime GetWriteUtc(string path)
    {
        try
        {
            return File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private bool ReloadConfig(bool force)
    {
        var write = GetWriteUtc(AppPaths.DeviceConfig);

        if (!force &&
            write != DateTime.MinValue &&
            write == _configWriteUtc)
        {
            return false;
        }

        _config = UnifiedConfigStore.LoadOrMigrate();
        _configWriteUtc = GetWriteUtc(AppPaths.DeviceConfig);
        return true;
    }

    private void SaveConfig()
    {
        UnifiedConfigStore.Save(_config);
        _configWriteUtc = GetWriteUtc(AppPaths.DeviceConfig);
    }

    private void LoadActivePeers(bool force = false)
    {
        var write = GetWriteUtc(AppPaths.DevicePeers);

        if (force || write != _peersWriteUtc)
        {
            _peerCache = JsonStore.LoadOrCreate(
                AppPaths.DevicePeers,
                () => new List<DevicePresence>());
            _peersWriteUtc = write;
        }

        var cutoff = DateTimeOffset.Now - TimeSpan.FromSeconds(35);
        _peers = _peerCache
            .Where(x => x.DeviceId != Guid.Empty && x.LastSeen >= cutoff)
            .GroupBy(x => x.DeviceId)
            .Select(x => x.OrderByDescending(y => y.LastSeen).First())
            .OrderBy(x => x.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task RefreshAllAsync(bool refreshNetworkPrinters)
    {
        if (_refreshRunning)
            return;

        _refreshRunning = true;

        try
        {
            SetBusy("SimplePrint wird aktualisiert …");

            ReloadConfig(true);
            LoadActivePeers(true);
            RefreshHeader();
            RefreshDevicesGrid(true);
            RefreshJobsGrid();
            RefreshDiagnosticsTree(true);
            RefreshSettings();

            if (refreshNetworkPrinters)
                RefreshNetworkPrinterTree();

            // Die langsamen Windows-Abfragen laufen parallel statt nacheinander.
            await Task.WhenAll(
                RefreshOwnPrintersAsync(false),
                RefreshSystemManagementAsync());

            RefreshOverview();
            _ = RefreshLatestReleaseAsync(false);

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
            _refreshRunning = false;
            SetIdle();
        }
    }

    private Task RefreshRequestedAsync()
    {
        if (_operationRunning)
        {
            _operationStatus.Text =
                "Es läuft gerade eine Aktion. Die Ansicht wird danach automatisch aktualisiert.";
            return Task.CompletedTask;
        }

        return RefreshAllAsync(true);
    }

    private void RefreshLiveState(bool forceUi = false)
    {
        // Während einer Benutzeraktion oder Komplettaktualisierung darf die
        // periodische Aktualisierung _config und die Ansichten nicht anfassen.
        if (_operationRunning || _refreshRunning)
            return;

        try
        {
            var configChanged = ReloadConfig(false);
            LoadActivePeers();

            ApplyServiceStatus(ServiceStatusReader.GetStatusText("SimplePrint"));

            if (configChanged)
                RefreshSettings();

            RefreshHeader();
            RefreshOverview();

            // Im Infobereich (Fenster verborgen) genügt der Status; die
            // aufwendigen Ansichten werden erst beim Einblenden aktualisiert.
            if (!Visible)
                return;

            RefreshDevicesGrid(forceUi);
            RefreshDiagnosticsTree(forceUi);

            var networkChanged =
                !string.Equals(
                    BuildNetworkCatalogFingerprint(),
                    _networkCatalogFingerprint,
                    StringComparison.Ordinal);

            if (forceUi || networkChanged)
                RefreshNetworkPrinterTree();

            RefreshJobsGrid(forceUi);
        }
        catch
        {
            // Die periodische Oberflächenaktualisierung darf die GUI nicht stören.
        }
    }

    private void RefreshHeader()
    {
        _deviceName.Text = string.IsNullOrWhiteSpace(_config.DeviceName)
            ? Environment.MachineName
            : _config.DeviceName;

        var version = typeof(MainForm).Assembly.GetName().Version;
        _version.Text = version is null
            ? $"unbekannt · P{Protocol.Version}"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)} · P{Protocol.Version}";
    }

    // ---------- Aktionen exklusiv ausführen ----------

    /// <summary>
    /// Führt eine Benutzeraktion exklusiv aus: Der Live-Timer greift währenddessen
    /// nicht in _config ein, und alle Aktionsbuttons sind gesperrt.
    /// </summary>
    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (_operationRunning)
        {
            _operationStatus.Text = "Es läuft bereits eine Aktion. Bitte kurz warten …";
            return;
        }

        _operationRunning = true;
        SetActionButtonsEnabled(false);

        try
        {
            await action();
        }
        finally
        {
            _operationRunning = false;
            SetActionButtonsEnabled(true);
        }
    }

    private Button MakeActionButton(string text, Func<Task> action)
    {
        var button = MakeButton(
            text,
            async (_, _) => await RunExclusiveAsync(action));

        _actionButtons.Add(button);
        return button;
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        foreach (var button in _actionButtons)
            button.Enabled = enabled;
    }

    // ---------- Hilfsfunktionen für die Oberfläche ----------

    private static void EnableDoubleBuffering(Control control)
    {
        try
        {
            typeof(Control)
                .GetProperty(
                    "DoubleBuffered",
                    BindingFlags.Instance | BindingFlags.NonPublic)?
                .SetValue(control, true);
        }
        catch
        {
        }
    }

    private static DataGridView Grid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(232, 234, 237)
        };

        grid.RowTemplate.Height = 28;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 242, 245);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(240, 242, 245);
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(217, 232, 250);
        grid.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;

        EnableDoubleBuffering(grid);
        return grid;
    }

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
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
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

        // Beim Einblenden aus dem Infobereich alle Ansichten auffrischen,
        // weil sie im verborgenen Zustand nicht mitgeführt wurden.
        RefreshLiveState(true);
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
