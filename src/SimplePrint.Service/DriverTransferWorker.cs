using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SimplePrint.Common;

namespace SimplePrint.Service;

/// <summary>
/// Server-Seite der einmaligen Treiberübertragung. Ein Client, der einen Treiber nicht
/// besitzt, fragt hier an. Der Server prüft Berechtigung, Treiber und Architektur,
/// lässt die Anfrage am Server bestätigen (Oberfläche) und sendet erst dann das Paket.
///
/// Der Port ist nur geöffnet (Listener und Firewallregel), solange mindestens ein
/// Drucker freigegeben ist.
/// </summary>
public sealed class DriverTransferWorker : BackgroundService
{
    private const int MaxPendingRequests = 3;
    private const int PeerLookupAttempts = 3;

    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PeerMaxAge = TimeSpan.FromSeconds(40);

    private readonly FileLog _log = new(AppPaths.DeviceLog);
    private readonly SemaphoreSlim _packageLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _activeRequesters = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private bool _startFailureLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        // Nach einem Neustart des Dienstes sind alte Anfragen nicht mehr gültig.
        DriverRequestStore.CleanupAll();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wanted = HasEnabledSharedPrinters();

                if (wanted && _listener is null)
                    StartListener(stoppingToken);
                else if (!wanted && _listener is not null)
                    StopListener(removeFirewallRule: true);

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            StopListener(removeFirewallRule: false);
        }
    }

    private bool HasEnabledSharedPrinters()
    {
        var config = ReadConfig();

        // Konfiguration kurz nicht lesbar: bisherigen Zustand beibehalten.
        if (config is null)
            return _listener is not null;

        return config.SharedPrinters.Any(x => x.Enabled);
    }

    private static SimplePrintConfig? ReadConfig()
    {
        try
        {
            using var stream = new FileStream(
                AppPaths.DeviceConfig,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return JsonSerializer.Deserialize<SimplePrintConfig>(stream, JsonStore.Options);
        }
        catch
        {
            return null;
        }
    }

    private void StartListener(CancellationToken serviceCt)
    {
        var listener = new TcpListener(IPAddress.Any, DriverTransferProtocol.Port);

        try
        {
            listener.Start(8);
        }
        catch (SocketException ex)
        {
            listener.Stop();

            if (!_startFailureLogged)
            {
                _log.Error(
                    $"Treiberübertragung: TCP {DriverTransferProtocol.Port} kann nicht geöffnet werden. " +
                    $"SimplePrint versucht es alle {PollInterval.TotalSeconds:0} s erneut",
                    ex);
                _startFailureLogged = true;
            }

            return;
        }

        _startFailureLogged = false;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
        _listener = listener;
        _listenerCts = cts;

        _log.Info($"Treiberübertragung lauscht auf TCP {DriverTransferProtocol.Port}.");

        _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token), CancellationToken.None);
        _ = Task.Run(() => SetFirewallRuleAsync(true), CancellationToken.None);
    }

    private void StopListener(bool removeFirewallRule)
    {
        var listener = _listener;
        var cts = _listenerCts;

        if (listener is null)
            return;

        _listener = null;
        _listenerCts = null;

        try
        {
            cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            listener.Stop();
        }
        catch
        {
        }

        if (removeFirewallRule)
        {
            _log.Info("Treiberübertragung beendet: Es ist kein Drucker mehr freigegeben.");
            _ = Task.Run(() => SetFirewallRuleAsync(false), CancellationToken.None);
        }
    }

    private async Task SetFirewallRuleAsync(bool enable)
    {
        try
        {
            var rule = PowerShellRunner.Quote(DriverTransferProtocol.FirewallRuleName);

            var script = enable
                ? $@"
$ErrorActionPreference='Stop'
$rule = Get-NetFirewallRule -Name {rule} -ErrorAction SilentlyContinue
if(-not $rule) {{
  New-NetFirewallRule -Name {rule} -DisplayName 'SimplePrint Driver Transfer' -Description 'SimplePrint einmalige Treiberuebertragung im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort {DriverTransferProtocol.Port} -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
}}
elseif([string]$rule.Enabled -ne 'True') {{
  $rule | Enable-NetFirewallRule
}}
"
                : $@"
$ErrorActionPreference='Stop'
Get-NetFirewallRule -Name {rule} -ErrorAction SilentlyContinue | Remove-NetFirewallRule
";

            var result = await PowerShellRunner.RunAsync(script);
            if (result.ExitCode != 0)
            {
                _log.Error(
                    "Firewallregel für die Treiberübertragung konnte nicht gesetzt werden: " +
                    result.StdErr.Trim());
            }
        }
        catch (Exception ex)
        {
            _log.Error("Firewallregel für die Treiberübertragung konnte nicht gesetzt werden", ex);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);

                _ = Task.Run(
                    () => HandleAsync(client, ct),
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Treiberübertragung: Verbindungsannahme ausgefallen", ex);
        }
    }

    private static DevicePresence? FindActivePeer(Guid requesterId, string remoteIp)
    {
        for (var attempt = 1; attempt <= PeerLookupAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    AppPaths.DevicePeers,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                var peers = JsonSerializer.Deserialize<List<DevicePresence>>(
                                stream,
                                JsonStore.Options)
                            ?? [];

                return peers.FirstOrDefault(p =>
                    p.DeviceId == requesterId &&
                    string.Equals(p.Address, remoteIp, StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.Now - p.LastSeen <= PeerMaxAge);
            }
            catch
            {
                // peers.json wird gerade ersetzt; kurz warten und noch einmal lesen.
                Thread.Sleep(150);
            }
        }

        return null;
    }

    private static bool ClientDisconnected(TcpClient client)
    {
        try
        {
            return client.Client.Poll(0, SelectMode.SelectRead) &&
                   client.Client.Available == 0;
        }
        catch
        {
            return true;
        }
    }

    private static Task SendAsync(
        Stream stream,
        string state,
        string message,
        CancellationToken ct) =>
        DriverTransferProtocol.WriteMessageAsync(
            stream,
            new DriverResponseMessage { State = state, Message = message },
            ct);

    private async Task HandleAsync(TcpClient client, CancellationToken serviceCt)
    {
        var remoteIp =
            (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";

        var requestId = Guid.NewGuid();
        var requesterId = Guid.Empty;
        var requesterRegistered = false;
        string? zipPath = null;

        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                using var total = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
                total.CancelAfter(DriverTransferProtocol.ApprovalTimeout + TimeSpan.FromMinutes(12));
                var ct = total.Token;

                // 1. Header und Anfrage lesen (kurze Frist, gegen hängende Verbindungen)
                DriverRequestMessage? request;

                using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    headerTimeout.CancelAfter(HeaderTimeout);

                    var header = new byte[DriverTransferProtocol.HeaderLength];
                    if (!await Protocol.ReadExactAsync(stream, header, headerTimeout.Token) ||
                        !DriverTransferProtocol.TryParseHeader(header, out requesterId))
                    {
                        return;
                    }

                    request = await DriverTransferProtocol.ReadMessageAsync<DriverRequestMessage>(
                        stream,
                        headerTimeout.Token);
                }

                if (request is null ||
                    string.IsNullOrWhiteSpace(request.DriverName) ||
                    request.DriverName.Length > 256)
                {
                    await SendAsync(stream, DriverStates.Error, "Ungültige Treiberanfrage.", ct);
                    return;
                }

                var driverName = request.DriverName.Trim();
                var clientArchitecture = (request.ClientArchitecture ?? "").Trim().ToLowerInvariant();

                // 2. Der Anfrager muss ein aktuell bekanntes SimplePrint-Gerät sein
                //    (gleiche Regel wie beim Diagnoseabruf: ID und Adresse müssen passen).
                var peer = FindActivePeer(requesterId, remoteIp);
                if (peer is null)
                {
                    _log.Info($"Treiberanfrage von {remoteIp} abgelehnt: kein aktiv bekanntes SimplePrint-Gerät.");
                    await SendAsync(
                        stream,
                        DriverStates.NotAuthorized,
                        "Der Server kennt dieses SimplePrint-Gerät aktuell nicht. " +
                        "Bitte in der Oberfläche aktualisieren und es erneut versuchen.",
                        ct);
                    return;
                }

                if (!_activeRequesters.TryAdd(requesterId, 0))
                {
                    await SendAsync(
                        stream,
                        DriverStates.Busy,
                        "Von diesem Gerät läuft bereits eine Treiberanfrage.",
                        ct);
                    return;
                }

                requesterRegistered = true;

                if (_activeRequesters.Count > MaxPendingRequests)
                {
                    await SendAsync(
                        stream,
                        DriverStates.Busy,
                        "Der Server bearbeitet gerade mehrere Treiberanfragen. Bitte später erneut versuchen.",
                        ct);
                    return;
                }

                // 3. Es werden nur Treiber angeboten, die ein freigegebener Drucker des
                //    Servers tatsächlich verwendet. Pfade kommen nie vom Client.
                var config = ReadConfig();
                var printers = config?.SharedPrinters
                                   .Where(p =>
                                       p.Enabled &&
                                       string.Equals(
                                           p.DriverName,
                                           driverName,
                                           StringComparison.OrdinalIgnoreCase))
                                   .ToList()
                               ?? [];

                if (printers.Count == 0)
                {
                    await SendAsync(
                        stream,
                        DriverStates.Unavailable,
                        $"Der Server hat keinen freigegebenen Drucker, der den Treiber '{driverName}' verwendet.",
                        ct);
                    return;
                }

                if (!DriverArchitecture.IsKnown(clientArchitecture))
                {
                    await SendAsync(
                        stream,
                        DriverStates.Error,
                        "Die Architektur des anfragenden PCs ist unbekannt.",
                        ct);
                    return;
                }

                var drivers = await DriverExporter.GetDriversAsync(driverName);
                if (drivers.Count == 0)
                {
                    await SendAsync(
                        stream,
                        DriverStates.Unavailable,
                        $"Der Treiber '{driverName}' ist auf dem Server nicht installiert.",
                        ct);
                    return;
                }

                // 4. Architekturprüfung: der Treiber muss zum Client passen.
                var match = drivers.FirstOrDefault(d =>
                    string.Equals(d.Architecture, clientArchitecture, StringComparison.OrdinalIgnoreCase));

                var serverArchitecture = DriverArchitecture.Local;

                if (match is null)
                {
                    var available = string.Join(
                        ", ",
                        drivers
                            .Select(d => DriverArchitecture.Describe(d.Architecture))
                            .Distinct());

                    _log.Info(
                        $"Treiberanfrage '{driverName}' von '{peer.DeviceName}' abgelehnt: " +
                        $"Client {clientArchitecture}, Treiber nur für {available}.");

                    await SendAsync(
                        stream,
                        DriverStates.ArchitectureMismatch,
                        $"Der Treiber passt nicht zu diesem PC: Der Server hat ihn nur für {available}, " +
                        $"dieser PC ist {DriverArchitecture.Describe(clientArchitecture)} " +
                        $"(Server-Betriebssystem: {DriverArchitecture.Describe(serverArchitecture)}). " +
                        "Ein Treiber lässt sich nur zwischen gleichen Architekturen übertragen.",
                        ct);
                    return;
                }

                try
                {
                    DriverExporter.GetPackageDirectory(match);
                }
                catch (InvalidOperationException ex)
                {
                    await SendAsync(stream, DriverStates.Unavailable, ex.Message, ct);
                    return;
                }

                var serverName = config?.DeviceName ?? Environment.MachineName;

                await DriverTransferProtocol.WriteMessageAsync(
                    stream,
                    new DriverResponseMessage
                    {
                        State = DriverStates.Offer,
                        Message =
                            $"Der Server '{serverName}' kann den Treiber bereitstellen. " +
                            "Bitte am Server fortfahren und die Anfrage dort bestätigen.",
                        DriverName = match.Name,
                        Manufacturer = match.Manufacturer,
                        DriverVersion = match.Version,
                        Architecture = match.Architecture,
                        ServerArchitecture = serverArchitecture
                    },
                    ct);

                // 5. Anfrage am Server bestätigen lassen (Oberfläche des angemeldeten Benutzers).
                var now = DateTimeOffset.Now;
                var record = new DriverRequestRecord
                {
                    Id = requestId,
                    RequesterId = requesterId,
                    RequesterName = peer.DeviceName,
                    RequesterAddress = remoteIp,
                    DriverName = match.Name,
                    PrinterName = string.IsNullOrWhiteSpace(printers[0].DisplayName)
                        ? printers[0].QueueName
                        : printers[0].DisplayName,
                    Architecture = match.Architecture,
                    CreatedAt = now,
                    ExpiresAt = now + DriverTransferProtocol.ApprovalTimeout
                };

                DriverRequestStore.SaveRequest(record);

                _log.Info(
                    $"Treiberanfrage {requestId:N}: '{peer.DeviceName}' ({remoteIp}) möchte " +
                    $"'{match.Name}' ({match.Architecture}). Warte auf Bestätigung am Server.");

                DriverDecision? decision = null;

                while (DateTimeOffset.Now < record.ExpiresAt)
                {
                    decision = DriverRequestStore.ReadDecision(requestId);
                    if (decision is not null)
                        break;

                    if (ClientDisconnected(client))
                    {
                        _log.Info($"Treiberanfrage {requestId:N}: Der Client hat die Verbindung beendet.");
                        return;
                    }

                    await Task.Delay(500, ct);
                }

                if (decision is null)
                {
                    _log.Info($"Treiberanfrage {requestId:N}: am Server nicht innerhalb der Frist bestätigt.");
                    await SendAsync(
                        stream,
                        DriverStates.Timeout,
                        "Die Anfrage wurde am Server nicht innerhalb von " +
                        $"{DriverTransferProtocol.ApprovalTimeout.TotalMinutes:0} Minuten bestätigt. " +
                        "Ist auf dem Server die SimplePrint-Oberfläche gestartet (auch im Infobereich)?",
                        ct);
                    return;
                }

                if (!decision.Approved)
                {
                    _log.Info($"Treiberanfrage {requestId:N}: am Server abgelehnt.");
                    await SendAsync(
                        stream,
                        DriverStates.Denied,
                        "Die Anfrage wurde am Server abgelehnt.",
                        ct);
                    return;
                }

                // 6. Paket erstellen und senden
                await SendAsync(
                    stream,
                    DriverStates.Preparing,
                    "Der Server hat bestätigt und bereitet das Treiberpaket vor …",
                    ct);

                await _packageLock.WaitAsync(ct);

                (string ZipPath, string Sha256, long Bytes) package;
                try
                {
                    package = await DriverExporter.CreatePackageAsync(match, ct);
                    zipPath = package.ZipPath;
                }
                finally
                {
                    _packageLock.Release();
                }

                await DriverTransferProtocol.WriteMessageAsync(
                    stream,
                    new DriverResponseMessage
                    {
                        State = DriverStates.Sending,
                        Message = "Das Treiberpaket wird übertragen …",
                        DriverName = match.Name,
                        Manufacturer = match.Manufacturer,
                        DriverVersion = match.Version,
                        Architecture = match.Architecture,
                        ServerArchitecture = serverArchitecture,
                        InfFileName = Path.GetFileName(match.InfPath),
                        PackageBytes = package.Bytes,
                        Sha256 = package.Sha256
                    },
                    ct);

                await using (var file = File.OpenRead(package.ZipPath))
                {
                    await file.CopyToAsync(stream, 64 * 1024, ct);
                }

                await stream.FlushAsync(ct);

                _log.Info(
                    $"Treiberpaket '{match.Name}' ({package.Bytes:N0} Byte) an '{peer.DeviceName}' übertragen.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error($"Treiberanfrage {requestId:N} von {remoteIp} fehlgeschlagen", ex);

                try
                {
                    if (client.Connected)
                    {
                        using var stream = client.GetStream();
                        await SendAsync(
                            stream,
                            DriverStates.Error,
                            "Der Server konnte das Treiberpaket nicht bereitstellen: " + ex.Message,
                            CancellationToken.None);
                    }
                }
                catch
                {
                }
            }
            finally
            {
                if (requesterRegistered)
                    _activeRequesters.TryRemove(requesterId, out _);

                DriverRequestStore.Cleanup(requestId);

                if (zipPath is not null)
                {
                    try
                    {
                        File.Delete(zipPath);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
