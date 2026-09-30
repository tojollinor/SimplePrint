using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using SimplePrint.Common;

namespace SimplePrint.Gui;

public sealed partial class MainForm
{
    // Diagnosebaum: "Dieses Gerät" sowie die Gruppen "Server", "Clients" und
    // "Weitere Geräte". Ein Haken an einer Gruppe wählt alle Geräte darunter;
    // ein Gerät, das in mehreren Gruppen steht, wird überall synchron angehakt.
    private readonly TreeView _diagnostics = new()
    {
        Dock = DockStyle.Fill,
        CheckBoxes = true,
        HideSelection = false,
        ItemHeight = 24
    };

    private TabPage? _diagnosticsTab;
    private bool _suppressDiagnosticsTreeCheck;
    private string _diagnosticsFingerprint = "";

    private TabPage CreateDiagnosticsTab()
    {
        var tab = new TabPage("Diagnose");
        _diagnosticsTab = tab;

        EnableDoubleBuffering(_diagnostics);
        _diagnostics.AfterCheck += OnDiagnosticsAfterCheck;

        _diagnostics.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
                return;

            var node = _diagnostics.GetNodeAt(e.X, e.Y);
            if (node is not null)
                _diagnostics.SelectedNode = node;
        };

        var menu = new ContextMenuStrip();

        menu.Items.Add("Geräteinfo …", null, (_, _) =>
        {
            if (_diagnostics.SelectedNode?.Tag is DevicePresence peer)
                ShowDeviceInfo(peer);
        });

        menu.Items.Add("Verbindung testen", null, async (_, _) =>
        {
            if (_diagnostics.SelectedNode?.Tag is DevicePresence peer)
                await TestConnectionAsync(peer);
        });

        menu.Opening += (_, e) =>
        {
            if (_diagnostics.SelectedNode?.Tag is not DevicePresence)
                e.Cancel = true;
        };

        _diagnostics.ContextMenuStrip = menu;

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 68,
            Padding = new Padding(10),
            ForeColor = UiColors.Muted,
            Text =
                "Wähle die Geräte für das Diagnosepaket. Ein Haken an „Server“ oder „Clients“ wählt " +
                "die ganze Gruppe. Geräte erscheinen auch dann, wenn sie keine Drucker freigegeben haben. " +
                "Dieses Gerät ist standardmäßig ausgewählt."
        };

        var buttons = BottomButtons();
        buttons.Controls.Add(MakeActionButton(
            "Diagnosepaket erstellen",
            CreateDiagnosticsAsync));
        buttons.Controls.Add(MakeButton(
            "Alle auswählen",
            (_, _) => SetAllDiagnosticsChecked(true)));
        buttons.Controls.Add(MakeButton(
            "Keine auswählen",
            (_, _) => SetAllDiagnosticsChecked(false)));

        tab.Controls.Add(_diagnostics);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private void OnDiagnosticsAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_suppressDiagnosticsTreeCheck || e.Node is null)
            return;

        _suppressDiagnosticsTreeCheck = true;
        try
        {
            var node = e.Node;

            if (node.Tag is string tag &&
                tag.StartsWith("group:", StringComparison.Ordinal))
            {
                foreach (TreeNode child in node.Nodes)
                    SetDeviceChecked(child, node.Checked);
            }
            else if (node.Tag is DevicePresence)
            {
                SetDeviceChecked(node, node.Checked);
            }

            UpdateDiagnosticsGroupStates();
        }
        finally
        {
            _suppressDiagnosticsTreeCheck = false;
        }
    }

    // Setzt den Haken bei allen Knoten desselben Geräts (es kann in mehreren Gruppen stehen).
    private void SetDeviceChecked(TreeNode source, bool value)
    {
        if (source.Tag is not DevicePresence peer)
            return;

        foreach (TreeNode group in _diagnostics.Nodes)
        {
            foreach (TreeNode child in group.Nodes)
            {
                if (child.Tag is DevicePresence other &&
                    other.DeviceId == peer.DeviceId)
                {
                    child.Checked = value;
                }
            }
        }
    }

    // Eine Gruppe ist angehakt, wenn alle Geräte darunter angehakt sind.
    private void UpdateDiagnosticsGroupStates()
    {
        foreach (TreeNode group in _diagnostics.Nodes)
        {
            if (group.Tag is string tag &&
                tag.StartsWith("group:", StringComparison.Ordinal))
            {
                group.Checked =
                    group.Nodes.Count > 0 &&
                    group.Nodes.Cast<TreeNode>().All(x => x.Checked);
            }
        }
    }

    private void SetAllDiagnosticsChecked(bool value)
    {
        _suppressDiagnosticsTreeCheck = true;
        try
        {
            foreach (TreeNode root in _diagnostics.Nodes)
            {
                root.Checked = value;

                foreach (TreeNode child in root.Nodes)
                    child.Checked = value;
            }

            UpdateDiagnosticsGroupStates();
        }
        finally
        {
            _suppressDiagnosticsTreeCheck = false;
        }
    }

    // Wählt genau ein Gerät aus und wechselt zum Reiter "Diagnose".
    private void OpenDiagnosticsFor(DevicePresence peer)
    {
        RefreshDiagnosticsTree(true);

        _suppressDiagnosticsTreeCheck = true;
        try
        {
            foreach (TreeNode root in _diagnostics.Nodes)
            {
                if (root.Tag is string tag && tag == "local")
                    root.Checked = peer.DeviceId == _config.DeviceId;

                foreach (TreeNode child in root.Nodes)
                {
                    if (child.Tag is DevicePresence other)
                        child.Checked = other.DeviceId == peer.DeviceId;
                }
            }

            UpdateDiagnosticsGroupStates();
        }
        finally
        {
            _suppressDiagnosticsTreeCheck = false;
        }

        if (_tabs is not null && _diagnosticsTab is not null)
            _tabs.SelectedTab = _diagnosticsTab;

        SetStatus($"Diagnose: '{peer.DeviceName}' ausgewählt – „Diagnosepaket erstellen“ klicken");
    }

    private static string DiagnosticsFingerprintLine(string kind, DevicePresence peer) =>
        $"{kind}|{peer.DeviceId:N}|{peer.DeviceName}|{peer.Address}|{peer.DiagnosticsPort}|" +
        $"{peer.ProtocolVersion}|{peer.Printers.Count}";

    private void RefreshDiagnosticsTree(bool force = false)
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
            .ToHashSet();

        var servers = _peers.Where(x => serverIds.Contains(x.DeviceId)).ToList();
        var clients = _peers.Where(x => clientIds.Contains(x.DeviceId)).ToList();
        var others = _peers
            .Where(x => !serverIds.Contains(x.DeviceId) && !clientIds.Contains(x.DeviceId))
            .ToList();

        var fingerprint = string.Join(
            "\n",
            new[] { $"L|{_config.DeviceName}" }
                .Concat(servers.Select(x => DiagnosticsFingerprintLine("S", x)))
                .Concat(clients.Select(x => DiagnosticsFingerprintLine("C", x)))
                .Concat(others.Select(x => DiagnosticsFingerprintLine("O", x))));

        if (!force &&
            _diagnostics.Nodes.Count > 0 &&
            string.Equals(fingerprint, _diagnosticsFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _diagnosticsFingerprint = fingerprint;

        _diagnostics.BeginUpdate();
        _suppressDiagnosticsTreeCheck = true;
        try
        {
            var checkedIds = CollectCheckedDiagnosticDeviceIds();
            var localWasChecked =
                _diagnostics.Nodes.Count == 0 ||
                _diagnostics.Nodes
                    .Cast<TreeNode>()
                    .Any(x => x.Tag is string s && s == "local" && x.Checked);

            _diagnostics.Nodes.Clear();

            _diagnostics.Nodes.Add(new TreeNode(
                $"Dieses Gerät · {_config.DeviceName}")
            {
                Tag = "local",
                Checked = localWasChecked,
                NodeFont = new Font(_diagnostics.Font, FontStyle.Bold)
            });

            AddDiagnosticsGroup("group:servers", "Server", servers, checkedIds);
            AddDiagnosticsGroup("group:clients", "Clients", clients, checkedIds);
            AddDiagnosticsGroup("group:others", "Weitere Geräte", others, checkedIds);

            UpdateDiagnosticsGroupStates();
        }
        finally
        {
            _suppressDiagnosticsTreeCheck = false;
            _diagnostics.EndUpdate();
        }
    }

    private void AddDiagnosticsGroup(
        string tag,
        string title,
        List<DevicePresence> devices,
        HashSet<Guid> checkedIds)
    {
        if (devices.Count == 0)
            return;

        var group = new TreeNode($"{title} ({devices.Count})")
        {
            Tag = tag,
            NodeFont = new Font(_diagnostics.Font, FontStyle.Bold)
        };

        foreach (var peer in devices)
        {
            var printers = peer.Printers.Count == 0
                ? "keine Drucker"
                : $"{peer.Printers.Count} Drucker";

            group.Nodes.Add(new TreeNode(
                $"{peer.DeviceName} · {peer.Address} · {printers}")
            {
                Tag = peer,
                Checked = checkedIds.Contains(peer.DeviceId)
            });
        }

        _diagnostics.Nodes.Add(group);
        group.Expand();
    }

    private HashSet<Guid> CollectCheckedDiagnosticDeviceIds()
    {
        var result = new HashSet<Guid>();

        foreach (TreeNode root in _diagnostics.Nodes)
        {
            foreach (TreeNode child in root.Nodes)
            {
                if (child.Tag is DevicePresence peer && child.Checked)
                    result.Add(peer.DeviceId);
            }
        }

        return result;
    }

    // ---------- Diagnosepaket erstellen ----------

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
}
