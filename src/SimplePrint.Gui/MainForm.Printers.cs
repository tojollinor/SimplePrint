using SimplePrint.Common;

namespace SimplePrint.Gui;

public sealed partial class MainForm
{
    // Ein gemeinsamer Baum: oben die eigenen Drucker (freigeben), darunter alle anderen
    // SimplePrint-Geräte mit ihren Druckern (verwenden).
    private readonly TreeView _printerTree = new()
    {
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        HideSelection = false,
        ItemHeight = 24
    };

    private readonly TreeNode _ownRoot = new() { Tag = "own" };
    private readonly TreeNode _otherRoot = new() { Tag = "others" };
    private bool _suppressPrinterTreeCheck;
    private string _networkCatalogFingerprint = "";

    private TabPage CreatePrintersTab()
    {
        var tab = new TabPage("Drucker");

        EnableDoubleBuffering(_printerTree);

        var bold = new Font(_printerTree.Font, FontStyle.Bold);
        _ownRoot.Text = "Dieses Gerät – Drucker freigeben";
        _ownRoot.NodeFont = bold;
        _otherRoot.Text = "Andere Geräte – Drucker verwenden";
        _otherRoot.NodeFont = bold;

        _printerTree.Nodes.Add(_ownRoot);
        _printerTree.Nodes.Add(_otherRoot);
        _printerTree.AfterCheck += OnPrinterTreeAfterCheck;

        _printerTree.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
                return;

            var node = _printerTree.GetNodeAt(e.X, e.Y);
            if (node is not null)
                _printerTree.SelectedNode = node;
        };

        _printerTree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node.Tag is DevicePresence peer)
                ShowDeviceInfo(peer);
        };

        _printerTree.ContextMenuStrip = BuildPrinterTreeMenu();

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(10),
            ForeColor = UiColors.Muted,
            Text =
                "Oben legst du fest, welche deiner Drucker freigegeben werden. Darunter stehen alle " +
                "SimplePrint-Geräte im Netzwerk. Ein Haken am Gerät wählt alle seine Drucker. " +
                "Rechtsklick auf ein Gerät zeigt Info und Verbindungstest."
        };

        var buttons = BottomButtons();
        buttons.Controls.Add(MakeActionButton(
            "Auswahl speichern",
            SavePrinterSelectionAsync));
        buttons.Controls.Add(MakeActionButton(
            "Drucker neu laden",
            async () =>
            {
                RefreshNetworkPrinterTree();
                await RefreshOwnPrintersAsync();
            }));
        buttons.Controls.Add(MakeButton(
            "Warteschlange öffnen",
            (_, _) => OpenSelectedPrinterQueue()));
        buttons.Controls.Add(MakeButton(
            "Testseite",
            (_, _) => PrintSelectedPrinterTestPage()));

        tab.Controls.Add(_printerTree);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private ContextMenuStrip BuildPrinterTreeMenu()
    {
        var menu = new ContextMenuStrip();

        var infoItem = new ToolStripMenuItem("Geräteinfo …", null, (_, _) =>
        {
            if (_printerTree.SelectedNode?.Tag is DevicePresence peer)
                ShowDeviceInfo(peer);
        });

        var testItem = new ToolStripMenuItem("Verbindung testen", null, async (_, _) =>
        {
            if (_printerTree.SelectedNode?.Tag is DevicePresence peer)
                await TestConnectionAsync(peer);
        });

        var diagItem = new ToolStripMenuItem("Diagnose dieses Geräts …", null, (_, _) =>
        {
            if (_printerTree.SelectedNode?.Tag is DevicePresence peer)
                OpenDiagnosticsFor(peer);
        });

        var separator = new ToolStripSeparator();

        var queueItem = new ToolStripMenuItem(
            "Warteschlange öffnen",
            null,
            (_, _) => OpenSelectedPrinterQueue());

        var pageItem = new ToolStripMenuItem(
            "Testseite drucken",
            null,
            (_, _) => PrintSelectedPrinterTestPage());

        menu.Items.AddRange(new ToolStripItem[]
        {
            infoItem,
            testItem,
            diagItem,
            separator,
            queueItem,
            pageItem
        });

        menu.Opening += (_, e) =>
        {
            var tag = _printerTree.SelectedNode?.Tag;
            var isDevice = tag is DevicePresence;
            var isPrinter = tag is LocalPrinterInfo || tag is NetworkPrinterTag;

            infoItem.Visible = isDevice;
            testItem.Visible = isDevice;
            diagItem.Visible = isDevice;
            separator.Visible = false;
            queueItem.Visible = isPrinter;
            pageItem.Visible = isPrinter;

            if (!isDevice && !isPrinter)
                e.Cancel = true;
        };

        return menu;
    }

    private void OnPrinterTreeAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_suppressPrinterTreeCheck || e.Node is null)
            return;

        _suppressPrinterTreeCheck = true;
        try
        {
            var node = e.Node;

            switch (node.Tag)
            {
                case DevicePresence when node.Nodes.Count > 0:
                    foreach (TreeNode child in node.Nodes)
                        child.Checked = node.Checked;

                    UpdateDeviceNodeText(node);
                    break;

                case NetworkPrinterTag when node.Parent is not null:
                    node.Parent.Checked = node.Parent.Nodes
                        .Cast<TreeNode>()
                        .All(x => x.Checked);

                    UpdateDeviceNodeText(node.Parent);
                    break;

                case LocalPrinterInfo:
                    break;

                default:
                    // Wurzeln, Geräte ohne Drucker und Platzhalter sind nicht auswählbar.
                    node.Checked = false;
                    break;
            }
        }
        finally
        {
            _suppressPrinterTreeCheck = false;
        }
    }

    private static void UpdateDeviceNodeText(TreeNode node)
    {
        if (node.Tag is not DevicePresence peer)
            return;

        var total = node.Nodes.Count;
        var selected = node.Nodes.Cast<TreeNode>().Count(x => x.Checked);

        var address = string.IsNullOrWhiteSpace(peer.Address)
            ? "nicht erreichbar"
            : peer.Address;

        var suffix = total == 0
            ? "keine Drucker freigegeben"
            : selected == 0 || selected == total
                ? $"{total} Drucker"
                : $"{selected} von {total} ausgewählt";

        node.Text = $"{peer.DeviceName}  ·  {address}  ·  {suffix}";
    }

    private static void FinishDeviceNode(TreeNode device)
    {
        device.Checked =
            device.Nodes.Count > 0 &&
            device.Nodes.Cast<TreeNode>().All(x => x.Checked);

        if (device.Nodes.Count == 0)
            device.ForeColor = SystemColors.GrayText;

        UpdateDeviceNodeText(device);
    }

    // ---------- Eigene Drucker ----------

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

            // Noch nicht gespeicherte Haken bleiben beim Neuladen erhalten.
            var pending = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (TreeNode node in _ownRoot.Nodes)
            {
                if (node.Tag is LocalPrinterInfo existing)
                    pending[existing.Name] = node.Checked;
            }

            _printerTree.BeginUpdate();
            _suppressPrinterTreeCheck = true;
            try
            {
                _ownRoot.Nodes.Clear();

                foreach (var printer in printers)
                {
                    var shared = pending.TryGetValue(printer.Name, out var pendingShared)
                        ? pendingShared
                        : _config.SharedPrinters.Any(x =>
                            x.Enabled &&
                            x.QueueName.Equals(
                                printer.Name,
                                StringComparison.OrdinalIgnoreCase));

                    _ownRoot.Nodes.Add(new TreeNode(
                        $"{printer.Name}   ·   {printer.DriverName}   [{printer.PrinterStatus}]")
                    {
                        Tag = printer,
                        Checked = shared
                    });
                }

                if (printers.Count == 0)
                {
                    _ownRoot.Nodes.Add(new TreeNode("Keine lokalen Drucker gefunden.")
                    {
                        ForeColor = SystemColors.GrayText
                    });
                }

                _ownRoot.Expand();
            }
            finally
            {
                _suppressPrinterTreeCheck = false;
                _printerTree.EndUpdate();
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

    // ---------- Andere Geräte ----------

    private void RefreshNetworkPrinterTree()
    {
        var pending = CollectNetworkPrinterSelections();

        _printerTree.BeginUpdate();
        _suppressPrinterTreeCheck = true;
        try
        {
            _otherRoot.Nodes.Clear();

            var activeIds = _peers
                .Select(x => x.DeviceId)
                .ToHashSet();

            // Alle aktiven Geräte, auch solche ohne freigegebene Drucker.
            foreach (var peer in _peers)
            {
                var device = new TreeNode { Tag = peer };
                var advertisedIds = new HashSet<Guid>();

                foreach (var printer in peer.Printers
                             .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    advertisedIds.Add(printer.Id);

                    var selected =
                        pending.TryGetValue((peer.DeviceId, printer.Id), out var pendingSelected)
                            ? pendingSelected
                            : _config.NetworkPrinters.Any(x =>
                                x.Enabled &&
                                x.SourceDeviceId == peer.DeviceId &&
                                x.PrinterId == printer.Id);

                    device.Nodes.Add(new TreeNode(
                        $"{printer.DisplayName}   ·   {printer.DriverName}   [{printer.Status}]")
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

                    var keepSelected =
                        pending.TryGetValue(
                            (peer.DeviceId, noLongerShared.Id),
                            out var pendingKept)
                            ? pendingKept
                            : true;

                    device.Nodes.Add(new TreeNode(
                        $"{noLongerShared.DisplayName}   [nicht mehr freigegeben]")
                    {
                        Tag = new NetworkPrinterTag(peer, noLongerShared),
                        Checked = keepSelected,
                        ForeColor = SystemColors.GrayText
                    });
                }

                FinishDeviceNode(device);
                _otherRoot.Nodes.Add(device);
                device.Expand();
            }

            // Bereits verwendete Geräte, die gerade nicht erreichbar sind.
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

                var offlineDevice = new TreeNode
                {
                    Tag = offlinePeer,
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

                    var offlineSelected =
                        pending.TryGetValue(
                            (offlinePeer.DeviceId, printer.Id),
                            out var pendingOffline)
                            ? pendingOffline
                            : true;

                    offlineDevice.Nodes.Add(new TreeNode(
                        $"{printer.DisplayName}   [nicht erreichbar]")
                    {
                        Tag = new NetworkPrinterTag(offlinePeer, printer),
                        Checked = offlineSelected,
                        ForeColor = SystemColors.GrayText
                    });
                }

                FinishDeviceNode(offlineDevice);
                offlineDevice.ForeColor = SystemColors.GrayText;
                _otherRoot.Nodes.Add(offlineDevice);
                offlineDevice.Expand();
            }

            if (_otherRoot.Nodes.Count == 0)
            {
                _otherRoot.Nodes.Add(new TreeNode("Keine weiteren SimplePrint-Geräte gefunden.")
                {
                    ForeColor = SystemColors.GrayText
                });
            }

            _otherRoot.Expand();
        }
        finally
        {
            _suppressPrinterTreeCheck = false;
            _printerTree.EndUpdate();
            _networkCatalogFingerprint = BuildNetworkCatalogFingerprint();
        }
    }

    private Dictionary<(Guid DeviceId, Guid PrinterId), bool>
        CollectNetworkPrinterSelections()
    {
        var result =
            new Dictionary<(Guid DeviceId, Guid PrinterId), bool>();

        foreach (TreeNode device in _otherRoot.Nodes)
        {
            foreach (TreeNode child in device.Nodes)
            {
                if (child.Tag is NetworkPrinterTag tag)
                {
                    result[(tag.Device.DeviceId, tag.Printer.Id)] =
                        child.Checked;
                }
            }
        }

        return result;
    }

    private string BuildNetworkCatalogFingerprint()
    {
        var lines = new List<string>();

        foreach (var peer in _peers
                     .OrderBy(x => x.DeviceId))
        {
            lines.Add(
                $"P|{peer.DeviceId:N}|{peer.Address}|{peer.ProtocolVersion}|{peer.AppVersion}");

            foreach (var printer in peer.Printers
                         .OrderBy(x => x.Id))
            {
                lines.Add(
                    $"D|{peer.DeviceId:N}|{printer.Id:N}|{printer.DisplayName}|{printer.Status}|" +
                    $"{printer.TransportMode}|{printer.DirectAddress}|{printer.DeviceUuid}");
            }
        }

        foreach (var mapping in _config.NetworkPrinters
                     .Where(x => x.Enabled)
                     .OrderBy(x => x.SourceDeviceId)
                     .ThenBy(x => x.PrinterId))
        {
            lines.Add(
                $"M|{mapping.SourceDeviceId:N}|{mapping.PrinterId:N}|{mapping.PrinterDisplayName}|" +
                $"{mapping.TransportMode}|{mapping.DirectAddress}|{mapping.DeviceUuid}");
        }

        return string.Join("\n", lines);
    }

    // ---------- Auswahl speichern ----------

    private async Task SavePrinterSelectionAsync()
    {
        try
        {
            SetBusy("Druckerauswahl wird übernommen …");

            var sharedCount = ApplyOwnPrinterSelection();
            var networkSelected = await ApplyNetworkPrinterSelectionAsync();

            await RefreshAllAsync(true);

            var parts = new List<string>();
            if (sharedCount is not null)
                parts.Add($"{sharedCount} eigene Druckerfreigabe(n)");

            parts.Add($"{networkSelected} Netzwerkdrucker ausgewählt");

            SetStatus("✓ Druckerauswahl gespeichert");
            MessageBox.Show(
                "Gespeichert: " + string.Join(" · ", parts) + ".",
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException ex)
        {
            SetStatus("Vorgang abgebrochen");
            MessageBox.Show(
                ex.Message,
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            await RefreshAllAsync(true);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Druckerauswahl fehlgeschlagen");
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

    // Gibt die Anzahl freigegebener eigener Drucker zurück, oder null, wenn die Liste
    // der lokalen Drucker noch nicht geladen ist (dann bleibt die Konfiguration unverändert).
    private int? ApplyOwnPrinterSelection()
    {
        var printerNodes = _ownRoot.Nodes
            .Cast<TreeNode>()
            .Where(x => x.Tag is LocalPrinterInfo)
            .ToList();

        if (printerNodes.Count == 0)
            return null;

        var selected = printerNodes
            .Where(x => x.Checked)
            .Select(x => (LocalPrinterInfo)x.Tag!)
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
        SaveConfig();
        return next.Count;
    }

    private async Task<int> ApplyNetworkPrinterSelectionAsync()
    {
        var visibleSourceIds = _otherRoot.Nodes
            .Cast<TreeNode>()
            .Where(x => x.Tag is DevicePresence)
            .Select(x => ((DevicePresence)x.Tag!).DeviceId)
            .Where(x => x != Guid.Empty)
            .ToHashSet();

        var desired = new Dictionary<(Guid DeviceId, Guid PrinterId), NetworkPrinterTag>();

        foreach (TreeNode device in _otherRoot.Nodes)
        {
            foreach (TreeNode child in device.Nodes)
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
            SaveConfig();
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
                SaveConfig();
            }

            await InstallNetworkPrinterAsync(tag);
        }

        SaveConfig();
        return desired.Count;
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
                // Herstellerspezifischer Treiber fehlt: zuerst die einmalige Übertragung
                // vom Server versuchen, erst danach die manuelle Auswahl anbieten.
                driver = await TryObtainDriverAsync(tag) ?? "";

                if (string.IsNullOrWhiteSpace(driver))
                {
                    drivers = await PrinterInstaller.GetDriverNamesAsync();
                    driver = ChooseDriver(drivers, tag.Printer.DriverName) ?? "";
                    if (string.IsNullOrWhiteSpace(driver))
                    {
                        throw new OperationCanceledException(
                            "Die Treiberauswahl wurde abgebrochen.");
                    }
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
        SaveConfig();

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
                SaveConfig();

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
            SaveConfig();
            throw;
        }
    }

    // Versucht, einen fehlenden herstellerspezifischen Treiber einmalig vom Server zu
    // beziehen. Gibt den Treibernamen zurück oder null, wenn das nicht möglich war
    // (dann wird die manuelle Treiberauswahl angeboten).
    private async Task<string?> TryObtainDriverAsync(NetworkPrinterTag tag)
    {
        try
        {
            SetBusy($"Treiber '{tag.Printer.DriverName}' wird vom Server beschafft …");

            await PrinterInstaller.EnsureDriverInstalledAsync(tag.Printer.DriverName);

            var drivers = await PrinterInstaller.GetDriverNamesAsync();
            return drivers.FirstOrDefault(x =>
                x.Equals(
                    tag.Printer.DriverName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Der Treiber '{tag.Printer.DriverName}' konnte nicht vom Server bezogen werden:" +
                $"\r\n\r\n{ex.Message}\r\n\r\n" +
                "Du kannst im nächsten Schritt einen lokal vorhandenen Treiber auswählen.",
                "Treiber",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return null;
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

    // ---------- Warteschlange und Testseite ----------

    // Liefert den Windows-Namen der Queue des markierten Druckers (eigener oder
    // verbundener Netzwerkdrucker) oder null.
    private string? GetSelectedPrinterQueueName(out string displayName)
    {
        displayName = "";

        var tag = _printerTree.SelectedNode?.Tag;

        if (tag is LocalPrinterInfo own)
        {
            displayName = own.Name;
            return own.Name;
        }

        if (tag is NetworkPrinterTag network)
        {
            var mapping = _config.NetworkPrinters.FirstOrDefault(x =>
                x.Enabled &&
                x.SourceDeviceId == network.Device.DeviceId &&
                x.PrinterId == network.Printer.Id);

            if (mapping is not null)
            {
                displayName = mapping.PrinterDisplayName;
                return mapping.LocalPrinterName;
            }
        }

        return null;
    }

    private void OpenSelectedPrinterQueue()
    {
        var queue = GetSelectedPrinterQueueName(out var display);
        if (queue is null)
        {
            MessageBox.Show(
                "Bitte zuerst einen eigenen oder einen auf diesem Gerät installierten Netzwerkdrucker markieren.",
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            UnifiedPrinterHelper.OpenQueue(queue);
            SetStatus($"✓ Warteschlange '{display}' geöffnet");
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

    private void PrintSelectedPrinterTestPage()
    {
        var queue = GetSelectedPrinterQueueName(out var display);
        if (queue is null)
        {
            MessageBox.Show(
                "Bitte zuerst einen eigenen oder einen auf diesem Gerät installierten Netzwerkdrucker markieren.",
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            UnifiedPrinterHelper.PrintTestPage(queue);
            SetStatus($"✓ Testseite für '{display}' gestartet");
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
}
