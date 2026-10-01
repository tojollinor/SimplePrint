using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SimplePrint.Common;

namespace SimplePrint.Gui;

public sealed partial class MainForm
{
    private sealed record DeviceRow(
        Guid Id,
        string Name,
        string Address,
        string Version,
        UiHealth VersionHealth,
        string Role,
        string Printers,
        DateTimeOffset? Seen);

    private readonly DataGridView _devices = Grid();
    private string _devicesFingerprint = "";
    private Version? _latestRelease;
    private DateTime _latestReleaseCheckedUtc = DateTime.MinValue;
    private bool _releaseCheckRunning;

    private TabPage CreateDevicesTab()
    {
        var tab = new TabPage("Geräte");

        _devices.Columns.Add("name", "Gerät");
        _devices.Columns.Add("address", "IP-Adresse");
        _devices.Columns.Add("version", "Version");
        _devices.Columns.Add("role", "Rolle");
        _devices.Columns.Add("printers", "Drucker");
        _devices.Columns.Add("seen", "Zuletzt gesehen");

        _devices.Columns["name"]!.FillWeight = 120;
        _devices.Columns["address"]!.FillWeight = 85;
        _devices.Columns["version"]!.FillWeight = 150;
        _devices.Columns["role"]!.FillWeight = 90;
        _devices.Columns["printers"]!.FillWeight = 90;
        _devices.Columns["seen"]!.FillWeight = 80;

        _devices.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0)
                return;

            _devices.ClearSelection();
            _devices.Rows[e.RowIndex].Selected = true;

            if (SelectedDevice() is { } peer)
                ShowDeviceInfo(peer);
        };

        _devices.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0)
                return;

            _devices.ClearSelection();
            _devices.Rows[e.RowIndex].Selected = true;
        };

        _devices.ContextMenuStrip = BuildDeviceMenu();

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(10),
            ForeColor = UiColors.Muted,
            Text =
                "Alle SimplePrint-Geräte im Netzwerk. Die Version wird mit dem neuesten " +
                "GitHub-Release verglichen. Rechtsklick oder Doppelklick zeigt Details."
        };

        var buttons = BottomButtons();
        buttons.Controls.Add(MakeButton(
            "Geräteinfo",
            (_, _) =>
            {
                if (SelectedDevice() is { } peer)
                    ShowDeviceInfo(peer);
            }));
        buttons.Controls.Add(MakeActionButton(
            "Verbindung testen",
            async () =>
            {
                if (SelectedDevice() is { } peer)
                    await TestConnectionAsync(peer);
            }));
        buttons.Controls.Add(MakeActionButton(
            "Nach neuer Version suchen",
            async () =>
            {
                await RefreshLatestReleaseAsync(true);
                SetStatus(_latestRelease is null
                    ? "Neueste Version konnte nicht ermittelt werden"
                    : $"✓ Neuestes Release: {_latestRelease.ToString(3)}");
            }));

        tab.Controls.Add(_devices);
        tab.Controls.Add(info);
        tab.Controls.Add(buttons);
        return tab;
    }

    private ContextMenuStrip BuildDeviceMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Geräteinfo …", null, (_, _) =>
        {
            if (SelectedDevice() is { } peer)
                ShowDeviceInfo(peer);
        });

        menu.Items.Add("Verbindung testen", null, async (_, _) =>
        {
            if (SelectedDevice() is { } peer)
                await TestConnectionAsync(peer);
        });

        menu.Items.Add("Diagnose dieses Geräts …", null, (_, _) =>
        {
            if (SelectedDevice() is { } peer)
                OpenDiagnosticsFor(peer);
        });

        menu.Opening += (_, e) =>
        {
            if (SelectedDevice() is null)
                e.Cancel = true;
        };

        return menu;
    }

    private DevicePresence? SelectedDevice()
    {
        if (_devices.SelectedRows.Count == 0 ||
            _devices.SelectedRows[0].Tag is not Guid id)
        {
            return null;
        }

        return id == _config.DeviceId
            ? BuildSelfPresence()
            : _peers.FirstOrDefault(x => x.DeviceId == id);
    }

    // ---------- Tabelle ----------

    private void RefreshDevicesGrid(bool force = false)
    {
        var rows = BuildDeviceRows();

        var fingerprint =
            (_latestRelease?.ToString() ?? "-") + "\n" +
            string.Join(
                "\n",
                rows.Select(x =>
                    $"{x.Id:N}|{x.Name}|{x.Address}|{x.Version}|{x.Role}|{x.Printers}"));

        if (!force &&
            string.Equals(fingerprint, _devicesFingerprint, StringComparison.Ordinal))
        {
            // Nur die Spalte "Zuletzt gesehen" aktualisieren (kein Flackern).
            var byId = rows.ToDictionary(x => x.Id);
            foreach (DataGridViewRow gridRow in _devices.Rows)
            {
                if (gridRow.Tag is Guid id && byId.TryGetValue(id, out var row))
                    gridRow.Cells["seen"].Value = FormatSeenOrNow(row.Seen);
            }

            return;
        }

        _devicesFingerprint = fingerprint;

        var selectedId =
            _devices.SelectedRows.Count > 0 && _devices.SelectedRows[0].Tag is Guid selected
                ? selected
                : Guid.Empty;

        _devices.SuspendLayout();
        try
        {
            _devices.Rows.Clear();

            foreach (var row in rows)
            {
                var index = _devices.Rows.Add(
                    row.Name,
                    row.Address,
                    row.Version,
                    row.Role,
                    row.Printers,
                    FormatSeenOrNow(row.Seen));

                var gridRow = _devices.Rows[index];
                gridRow.Tag = row.Id;

                if (row.VersionHealth is UiHealth.Ok or UiHealth.Warning)
                    gridRow.Cells["version"].Style.ForeColor = UiColors.For(row.VersionHealth);

                if (row.Id == selectedId)
                {
                    _devices.ClearSelection();
                    gridRow.Selected = true;
                }
            }
        }
        finally
        {
            _devices.ResumeLayout();
        }
    }

    private List<DeviceRow> BuildDeviceRows()
    {
        var rows = new List<DeviceRow>();

        var self = BuildSelfPresence();
        var (selfVersion, selfHealth) = DescribeVersion(self.AppVersion);

        rows.Add(new DeviceRow(
            self.DeviceId,
            self.DeviceName,
            string.IsNullOrWhiteSpace(self.Address) ? "–" : self.Address,
            selfVersion,
            selfHealth,
            "Dieses Gerät",
            DescribePrinterCount(self),
            null));

        foreach (var peer in _peers)
        {
            var (version, health) = DescribeVersion(peer.AppVersion);

            rows.Add(new DeviceRow(
                peer.DeviceId,
                peer.DeviceName,
                peer.Address,
                version,
                health,
                DescribeRole(peer),
                DescribePrinterCount(peer),
                peer.LastSeen));
        }

        return rows;
    }

    private static string DescribePrinterCount(DevicePresence peer) =>
        peer.Printers.Count == 0
            ? "keine freigegeben"
            : $"{peer.Printers.Count} freigegeben";

    private string DescribeRole(DevicePresence peer)
    {
        if (peer.DeviceId == _config.DeviceId)
            return "Dieses Gerät";

        // "Server": du nutzt dessen Drucker. "Client": das Gerät nutzt deine Drucker.
        var isServer = _config.NetworkPrinters.Any(x =>
            x.Enabled && x.SourceDeviceId == peer.DeviceId);

        var isClient = peer.Subscriptions.Any(x =>
            x.SourceDeviceId == _config.DeviceId);

        return isServer && isClient
            ? "Server + Client"
            : isServer
                ? "Server"
                : isClient
                    ? "Client"
                    : "–";
    }

    private static Version NormalizeVersion(Version value) =>
        new(
            value.Major,
            Math.Max(0, value.Minor),
            Math.Max(0, value.Build),
            Math.Max(0, value.Revision));

    private (string Text, UiHealth Health) DescribeVersion(string? appVersion)
    {
        var text = string.IsNullOrWhiteSpace(appVersion)
            ? "unbekannt"
            : appVersion.Trim();

        if (_latestRelease is null || !Version.TryParse(text, out var parsed))
            return (text, UiHealth.Unknown);

        var compare = NormalizeVersion(parsed).CompareTo(NormalizeVersion(_latestRelease));

        if (compare == 0)
            return ($"{text}   ✔ aktuell", UiHealth.Ok);

        if (compare < 0)
            return ($"{text}   ⬆ Update verfügbar ({_latestRelease.ToString(3)})", UiHealth.Warning);

        return ($"{text}   ● neuer als Release", UiHealth.Unknown);
    }

    private static string FormatSeen(DateTimeOffset value)
    {
        if (value == DateTimeOffset.MinValue)
            return "–";

        var age = DateTimeOffset.Now - value;

        if (age < TimeSpan.FromSeconds(10))
            return "gerade eben";

        if (age < TimeSpan.FromMinutes(1))
            return $"vor {(int)age.TotalSeconds} s";

        return $"vor {(int)age.TotalMinutes} min";
    }

    private static string FormatSeenOrNow(DateTimeOffset? value) =>
        value is null ? "jetzt" : FormatSeen(value.Value);

    // ---------- Eigenes Gerät als Geräteeintrag ----------

    private DevicePresence BuildSelfPresence()
    {
        var version = typeof(MainForm).Assembly.GetName().Version;

        return new DevicePresence
        {
            DeviceId = _config.DeviceId,
            DeviceName = string.IsNullOrWhiteSpace(_config.DeviceName)
                ? Environment.MachineName
                : _config.DeviceName,
            Address = GetLocalAddress(),
            AppVersion = version is null
                ? "unbekannt"
                : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}",
            ProtocolVersion = Protocol.Version,
            GatewayPort = _config.GatewayPort,
            DiagnosticsPort = _config.DiagnosticsPort,
            Printers = _config.SharedPrinters
                .Where(x => x.Enabled)
                .Select(x => new DiscoveredPrinter
                {
                    Id = x.Id,
                    DisplayName = string.IsNullOrWhiteSpace(x.DisplayName)
                        ? x.QueueName
                        : x.DisplayName,
                    DriverName = x.DriverName,
                    PortName = x.PortName,
                    TransportMode = x.TransportMode,
                    DirectAddress = x.DirectAddress,
                    DeviceUuid = x.DeviceUuid,
                    Status = "freigegeben"
                })
                .ToList(),
            LastSeen = DateTimeOffset.Now
        };
    }

    private static string GetLocalAddress()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType is
                    System.Net.NetworkInformation.NetworkInterfaceType.Loopback or
                    System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();

                var hasGateway = properties.GatewayAddresses.Any(x =>
                    x.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !x.Address.Equals(IPAddress.Any));

                if (!hasGateway)
                    continue;

                var address = properties.UnicastAddresses.FirstOrDefault(x =>
                    x.Address.AddressFamily == AddressFamily.InterNetwork);

                if (address is not null)
                    return address.Address.ToString();
            }
        }
        catch
        {
        }

        return "";
    }

    // ---------- Neueste Version (GitHub-Release) ----------

    private async Task RefreshLatestReleaseAsync(bool force)
    {
        if (_releaseCheckRunning)
            return;

        if (!force &&
            _latestRelease is not null &&
            DateTime.UtcNow - _latestReleaseCheckedUtc < TimeSpan.FromMinutes(30))
        {
            return;
        }

        _releaseCheckRunning = true;

        try
        {
            // Mit Version 0.0.0.0 liefert die Prüfung immer das neueste Release.
            var latest = await GitHubUpdateService.CheckAsync(
                new Version(0, 0, 0, 0),
                SimplePrintComponent.Unified);

            if (latest is not null)
            {
                var tag = (latest.TagName ?? "").Trim().TrimStart('v', 'V');

                if (Version.TryParse(tag, out var version))
                {
                    _latestRelease = version;
                    _latestReleaseCheckedUtc = DateTime.UtcNow;
                    RefreshDevicesGrid(true);
                }
            }
        }
        catch
        {
            // Ohne Internet bleibt die Versionsspalte ohne Bewertung.
        }
        finally
        {
            _releaseCheckRunning = false;
        }
    }

    // ---------- Geräteinfo ----------

    private void ShowDeviceInfo(DevicePresence peer)
    {
        using var form = new Form
        {
            Text = $"Geräteinfo – {peer.DeviceName}",
            Width = 660,
            Height = 540,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };

        var text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.5f),
            BackColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            Text = BuildDeviceInfoText(peer)
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };

        var close = new Button
        {
            Text = "Schließen",
            AutoSize = true,
            MinimumSize = new Size(100, 30),
            DialogResult = DialogResult.Cancel
        };

        var test = new Button
        {
            Text = "Verbindung testen",
            AutoSize = true,
            MinimumSize = new Size(150, 30)
        };

        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            try
            {
                var result = await DeviceConnectionTest.RunAsync(peer);

                if (!form.IsDisposed)
                {
                    text.Text =
                        BuildDeviceInfoText(peer) +
                        Environment.NewLine +
                        Environment.NewLine +
                        result;
                }
            }
            finally
            {
                if (!form.IsDisposed)
                    test.Enabled = true;
            }
        };

        buttons.Controls.Add(close);
        buttons.Controls.Add(test);

        form.Controls.Add(text);
        form.Controls.Add(buttons);
        form.CancelButton = close;

        form.ShowDialog(Visible ? this : null);
    }

    private string BuildDeviceInfoText(DevicePresence peer)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Name:             {peer.DeviceName}");
        sb.AppendLine($"IP-Adresse:       {(string.IsNullOrWhiteSpace(peer.Address) ? "–" : peer.Address)}");
        sb.AppendLine($"Version:          {peer.AppVersion}  (Protokoll P{peer.ProtocolVersion})");
        sb.AppendLine($"Rolle:            {DescribeRole(peer)}");
        sb.AppendLine($"Zuletzt gesehen:  {(peer.DeviceId == _config.DeviceId ? "jetzt" : FormatSeen(peer.LastSeen))}");
        sb.AppendLine($"Gateway-Port:     {peer.GatewayPort}");
        sb.AppendLine($"Diagnose-Port:    {peer.DiagnosticsPort}");
        sb.AppendLine();
        sb.AppendLine($"Freigegebene Drucker ({peer.Printers.Count}):");

        if (peer.Printers.Count == 0)
        {
            sb.AppendLine("  – keine –");
        }

        foreach (var printer in peer.Printers)
        {
            sb.AppendLine($"  • {printer.DisplayName}");
            sb.AppendLine($"      Treiber:    {printer.DriverName}");
            sb.AppendLine($"      Verbindung: {printer.TransportMode}");
            sb.AppendLine($"      Status:     {printer.Status}");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task TestConnectionAsync(DevicePresence peer)
    {
        try
        {
            SetBusy($"Verbindung zu '{peer.DeviceName}' wird getestet …");

            var result = await DeviceConnectionTest.RunAsync(peer);

            SetStatus("✓ Verbindungstest abgeschlossen");
            MessageBox.Show(
                result,
                $"Verbindungstest – {peer.DeviceName}",
                MessageBoxButtons.OK,
                result.Contains('✖')
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("✗ Verbindungstest fehlgeschlagen");
            MessageBox.Show(
                ex.Message,
                "Verbindungstest",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetIdle();
        }
    }
}

/// <summary>
/// Prüft, ob ein anderes SimplePrint-Gerät erreichbar ist: Protokollversion,
/// Print-Gateway (mit Handshake und Antwortzeit) und Diagnose-Port.
/// </summary>
internal static class DeviceConnectionTest
{
    public static async Task<string> RunAsync(
        DevicePresence peer,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Verbindungstest zu {peer.DeviceName}");
        sb.AppendLine();

        if (!IPAddress.TryParse(peer.Address, out var ip))
        {
            sb.AppendLine("✖ Das Gerät ist aktuell nicht erreichbar (keine Adresse bekannt).");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine(
            peer.ProtocolVersion == Protocol.Version
                ? $"✔ Protokoll P{peer.ProtocolVersion} ist kompatibel."
                : $"✖ Protokoll P{peer.ProtocolVersion} – dieser PC benötigt P{Protocol.Version}.");

        sb.AppendLine(await ProbeGatewayAsync(ip, peer.GatewayPort, ct));
        sb.AppendLine(await ProbePortAsync(ip, peer.DiagnosticsPort, "Diagnose-Port", ct));

        return sb.ToString().TrimEnd();
    }

    private static async Task<string> ProbeGatewayAsync(
        IPAddress ip,
        int port,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(ip, port, timeout.Token);
            var connectMs = watch.ElapsedMilliseconds;

            using var stream = tcp.GetStream();
            await stream.WriteAsync(
                Protocol.CreateGatewayHeader(Guid.Empty, Guid.Empty),
                timeout.Token);

            var reply = new byte[6];
            var complete = await Protocol.ReadExactAsync(stream, reply, timeout.Token);
            watch.Stop();

            return complete && Encoding.ASCII.GetString(reply) == "SPROK2"
                ? $"✔ Print-Gateway (Port {port}) antwortet in {watch.ElapsedMilliseconds} ms (Verbindungsaufbau {connectMs} ms)."
                : $"✖ Print-Gateway (Port {port}) ist erreichbar, antwortet aber nicht wie erwartet.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"✖ Print-Gateway (Port {port}): Zeitüberschreitung – Firewall oder Dienst prüfen.";
        }
        catch (Exception ex)
        {
            return $"✖ Print-Gateway (Port {port}): {ex.Message}";
        }
    }

    private static async Task<string> ProbePortAsync(
        IPAddress ip,
        int port,
        string name,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(ip, port, timeout.Token);
            watch.Stop();

            return $"✔ {name} (Port {port}) erreichbar in {watch.ElapsedMilliseconds} ms.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"✖ {name} (Port {port}): Zeitüberschreitung – Firewall prüfen.";
        }
        catch (Exception ex)
        {
            return $"✖ {name} (Port {port}): {ex.Message}";
        }
    }
}
