using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SimplePrint.Common;

namespace SimplePrint.Service;

public sealed class DeviceWorker : BackgroundService
{
    private sealed class ListenerState
    {
        public required NetworkPrinterMapping Mapping { get; init; }
        public required TcpListener Listener { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Task { get; init; }
    }

    private readonly FileLog _log = new(AppPaths.DeviceLog);
    private readonly ConcurrentDictionary<Guid, DevicePresence> _peers = new();
    private readonly ConcurrentDictionary<Guid, PrintJobRecord> _jobs = new();
    private readonly Dictionary<string, ListenerState> _listeners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _jobSaveLock = new(1, 1);
    private readonly SemaphoreSlim _diagnosticsLock = new(1, 1);
    private readonly object _configLock = new();
    private readonly object _listenerLock = new();

    private SimplePrintConfig _config = new();
    private DateTime _configWriteUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadConfig(true);
        LoadKnownJobs();

        await ResolveConfiguredWsdRoutesAsync();
        await EnsurePrinterSharesAsync();
        await SyncListenersAsync(stoppingToken);

        var cfg = SnapshotConfig();
        _log.Info(
            $"SimplePrint-Dienst gestartet. DeviceId={cfg.DeviceId}, " +
            $"Discovery={cfg.DiscoveryPort}, Gateway={cfg.GatewayPort}");

        await Task.WhenAll(
            RunDiscoveryResponderAsync(stoppingToken),
            RunPeerDiscoveryLoopAsync(stoppingToken),
            RunGatewayAsync(stoppingToken),
            RunDiagnosticsAsync(stoppingToken),
            RunReloadLoopAsync(stoppingToken));
    }

    private SimplePrintConfig SnapshotConfig()
    {
        lock (_configLock)
        {
            return new SimplePrintConfig
            {
                DeviceId = _config.DeviceId,
                DeviceName = _config.DeviceName,
                DiscoveryPort = _config.DiscoveryPort,
                GatewayPort = _config.GatewayPort,
                DiagnosticsPort = _config.DiagnosticsPort,
                LocalPortStart = _config.LocalPortStart,
                LocalPortEnd = _config.LocalPortEnd,
                ManualPeers = _config.ManualPeers.ToList(),
                SharedPrinters = _config.SharedPrinters
                    .Select(CloneSharedPrinter)
                    .ToList(),
                NetworkPrinters = _config.NetworkPrinters
                    .Select(CloneNetworkPrinter)
                    .ToList()
            };
        }
    }

    private void LoadConfig(bool force = false)
    {
        try
        {
            var write = File.Exists(AppPaths.DeviceConfig)
                ? File.GetLastWriteTimeUtc(AppPaths.DeviceConfig)
                : DateTime.MinValue;

            if (!force && write == _configWriteUtc)
                return;

            var config = UnifiedConfigStore.LoadOrMigrate();

            lock (_configLock)
                _config = config;

            _configWriteUtc = File.Exists(AppPaths.DeviceConfig)
                ? File.GetLastWriteTimeUtc(AppPaths.DeviceConfig)
                : DateTime.MinValue;

            _log.Info(
                $"Konfiguration geladen: {config.SharedPrinters.Count} eigene Freigaben, " +
                $"{config.NetworkPrinters.Count} Netzwerkdrucker.");
        }
        catch (Exception ex)
        {
            _log.Error("Konfiguration konnte nicht geladen werden", ex);
        }
    }

    private void LoadKnownJobs()
    {
        try
        {
            foreach (var job in JsonStore.LoadOrCreate(
                         AppPaths.DeviceJobs,
                         () => new List<PrintJobRecord>()))
            {
                _jobs[job.JobId] = job;
            }
        }
        catch (Exception ex)
        {
            _log.Error("Druckaufträge konnten nicht geladen werden", ex);
        }
    }

    private async Task SaveJobsAsync()
    {
        await _jobSaveLock.WaitAsync();
        try
        {
            var keep = _jobs.Values
                .OrderByDescending(x => x.UpdatedAt)
                .Take(300)
                .ToList();

            var keepIds = keep.Select(x => x.JobId).ToHashSet();
            foreach (var old in _jobs.Keys.Where(x => !keepIds.Contains(x)).ToList())
                _jobs.TryRemove(old, out _);

            JsonStore.Save(AppPaths.DeviceJobs, keep);
        }
        catch (Exception ex)
        {
            _log.Error("Druckaufträge konnten nicht gespeichert werden", ex);
        }
        finally
        {
            _jobSaveLock.Release();
        }
    }

    private void SavePeers()
    {
        try
        {
            var active = _peers.Values
                .Where(x => DateTimeOffset.Now - x.LastSeen <= TimeSpan.FromSeconds(30))
                .OrderBy(x => x.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            JsonStore.Save(AppPaths.DevicePeers, active);
        }
        catch (Exception ex)
        {
            _log.Error("Geräteliste konnte nicht gespeichert werden", ex);
        }
    }

    private async Task RunReloadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var before = _configWriteUtc;
            LoadConfig();

            if (before != _configWriteUtc)
            {
                await ResolveConfiguredWsdRoutesAsync();
                await EnsurePrinterSharesAsync();
                await SyncListenersAsync(ct);
            }

            await Task.Delay(1200, ct);
        }
    }

    private async Task ResolveConfiguredWsdRoutesAsync()
    {
        var candidates = SnapshotConfig().SharedPrinters
            .Where(p =>
                p.Enabled &&
                string.Equals(
                    p.TransportMode,
                    PrinterTransport.Wsd,
                    StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(p.DirectAddress) &&
                !string.IsNullOrWhiteSpace(p.DeviceUuid))
            .ToList();

        foreach (var candidate in candidates)
        {
            try
            {
                var address = await WsdAddressResolver.ResolveAsync(candidate.DeviceUuid);
                if (string.IsNullOrWhiteSpace(address))
                    continue;

                lock (_configLock)
                {
                    var live = _config.SharedPrinters.FirstOrDefault(x => x.Id == candidate.Id);
                    if (live is null)
                        continue;

                    live.TransportMode = PrinterTransport.Ipp;
                    live.DirectAddress = address;
                }

                _log.Info(
                    $"WSD-Drucker '{candidate.QueueName}' wird gerichtet per IPP über {address} angeboten.");
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"WSD-Adresse für '{candidate.QueueName}' konnte nicht aufgelöst werden",
                    ex);
            }
        }

    }

    private async Task EnsurePrinterSharesAsync()
    {
        try
        {
            var config = SnapshotConfig();
            var directPrinters = config.SharedPrinters
                .Where(x => x.Enabled && PrinterTransport.IsDeviceDirect(x.TransportMode))
                .ToList();

            var shareNames = directPrinters
                .Select(x => PrinterTransport.GetWindowsShareName(x.Id))
                .ToArray();

            var wantedArray = shareNames.Length == 0
                ? "@()"
                : "@(" + string.Join(
                    ",",
                    shareNames.Select(PowerShellRunner.Quote)) + ")";

            var script = $@"
$ErrorActionPreference='Stop'
$wanted={wantedArray}

foreach($printer in @(Get-Printer -ErrorAction SilentlyContinue |
  Where-Object {{ $_.Shared -and ([string]$_.ShareName) -like 'SimplePrint-*' }})) {{
  if($wanted -notcontains [string]$printer.ShareName) {{
    Set-Printer -Name $printer.Name -Shared $false -ErrorAction SilentlyContinue
  }}
}}
";

            foreach (var printer in directPrinters)
            {
                var shareName = PrinterTransport.GetWindowsShareName(printer.Id);
                script += $@"
$p = Get-Printer -Name {PowerShellRunner.Quote(printer.QueueName)} -ErrorAction Stop
Set-Printer -Name $p.Name -Shared $true -ShareName {PowerShellRunner.Quote(shareName)} -ErrorAction Stop
";
            }

            script += @"
Get-NetFirewallRule -Name 'SimplePrint-PrintShare-SMB' -ErrorAction SilentlyContinue |
  Remove-NetFirewallRule

if($wanted.Count -gt 0) {
  New-NetFirewallRule -Name 'SimplePrint-PrintShare-SMB' -DisplayName 'SimplePrint Printer Sharing SMB' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort 445 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
}
";

            var result = await PowerShellRunner.RunAsync(script);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(result.StdErr)
                        ? "Windows-Druckerfreigaben konnten nicht vorbereitet werden."
                        : result.StdErr.Trim());
            }
        }
        catch (Exception ex)
        {
            _log.Error("Windows-Fallbackfreigaben konnten nicht vorbereitet werden", ex);
        }
    }

    private async Task RunDiscoveryResponderAsync(CancellationToken ct)
    {
        var cfg = SnapshotConfig();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, cfg.DiscoveryPort));

        _log.Info($"Geräteerkennung lauscht auf UDP {cfg.DiscoveryPort}.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);

                if (result.Buffer.AsSpan().SequenceEqual(Protocol.DeviceDiscoveryRequestBytes))
                {
                    var response = BuildDeviceAnnouncement();
                    await udp.SendAsync(
                        Protocol.SerializeDeviceAnnouncement(response),
                        result.RemoteEndPoint,
                        ct);
                    continue;
                }

                // Übergangskompatibilität, bis die alte getrennte GUI vollständig entfernt ist.
                if (result.Buffer.AsSpan().SequenceEqual(Protocol.DiscoveryRequestBytes))
                {
                    var current = SnapshotConfig();
                    var legacy = new DiscoveryAnnouncement
                    {
                        AppVersion = GetAppVersion(),
                        ServerId = current.DeviceId,
                        ServerName = current.DeviceName,
                        GatewayPort = current.GatewayPort,
                        Printers = BuildDiscoveredPrinters(current.SharedPrinters)
                    };

                    await udp.SendAsync(
                        Protocol.SerializeAnnouncement(legacy),
                        result.RemoteEndPoint,
                        ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("Geräteerkennung fehlgeschlagen", ex);
                await Task.Delay(500, ct);
            }
        }
    }

    private DeviceAnnouncement BuildDeviceAnnouncement()
    {
        var current = SnapshotConfig();

        return new DeviceAnnouncement
        {
            AppVersion = GetAppVersion(),
            DeviceId = current.DeviceId,
            DeviceName = current.DeviceName,
            GatewayPort = current.GatewayPort,
            DiagnosticsPort = current.DiagnosticsPort,
            Printers = BuildDiscoveredPrinters(current.SharedPrinters),
            Subscriptions = DeviceRelationshipHelper
                .GetLocalSubscriptions(current)
                .ToList()
        };
    }

    private static List<DiscoveredPrinter> BuildDiscoveredPrinters(
        IEnumerable<SharedPrinterConfig> printers) =>
        printers
            .Where(p => p.Enabled)
            .Select(p => new DiscoveredPrinter
            {
                Id = p.Id,
                DisplayName = string.IsNullOrWhiteSpace(p.DisplayName)
                    ? p.QueueName
                    : p.DisplayName,
                DriverName = p.DriverName,
                PortName = p.PortName,
                TransportMode = p.TransportMode,
                DirectAddress = p.DirectAddress,
                DeviceUuid = p.DeviceUuid,
                Status = RawPrinter.CanOpen(p.QueueName)
                    ? "Bereit"
                    : "Nicht verfügbar"
            })
            .ToList();

    private async Task RunPeerDiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await DiscoverNowAsync(ct);
            await RefreshPendingJobStatusesAsync(ct);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(7), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DiscoverNowAsync(CancellationToken ct)
    {
        try
        {
            var cfg = SnapshotConfig();
            var devices = await Discovery.DiscoverDevicesAsync(
                cfg.DiscoveryPort,
                1200,
                ct,
                cfg.ManualPeers);

            foreach (var device in devices)
            {
                if (device.Announcement.DeviceId == cfg.DeviceId)
                    continue;

                _peers[device.Announcement.DeviceId] =
                    ToPresence(device);
            }

            var cutoff = DateTimeOffset.Now - TimeSpan.FromSeconds(30);
            foreach (var stale in _peers
                         .Where(x => x.Value.LastSeen < cutoff)
                         .Select(x => x.Key)
                         .ToList())
            {
                _peers.TryRemove(stale, out _);
            }

            SavePeers();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Gerätesuche fehlgeschlagen", ex);
        }
    }

    private static DevicePresence ToPresence(DiscoveredDevice device) =>
        new()
        {
            DeviceId = device.Announcement.DeviceId,
            DeviceName = device.Announcement.DeviceName,
            Address = device.Address.ToString(),
            AppVersion = device.Announcement.AppVersion,
            ProtocolVersion = device.Announcement.Version,
            GatewayPort = device.Announcement.GatewayPort,
            DiagnosticsPort = device.Announcement.DiagnosticsPort,
            Printers = device.Announcement.Printers
                .Select(CloneDiscoveredPrinter)
                .ToList(),
            Subscriptions = device.Announcement.Subscriptions
                .Select(x => new PrinterSubscriptionAnnouncement
                {
                    SourceDeviceId = x.SourceDeviceId,
                    PrinterId = x.PrinterId
                })
                .ToList(),
            LastSeen = device.SeenAt
        };

    private async Task RunDiagnosticsAsync(CancellationToken ct)
    {
        var cfg = SnapshotConfig();
        var listener = new TcpListener(IPAddress.Any, cfg.DiagnosticsPort);
        listener.Start(8);

        _log.Info($"Diagnose-Endpunkt lauscht auf TCP {cfg.DiagnosticsPort}.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(
                    () => HandleDiagnosticsRequestAsync(client, ct),
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleDiagnosticsRequestAsync(
        TcpClient client,
        CancellationToken serviceCt)
    {
        using (client)
        {
            await _diagnosticsLock.WaitAsync(serviceCt);
            try
            {
                client.NoDelay = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
                timeout.CancelAfter(TimeSpan.FromSeconds(75));
                using var stream = client.GetStream();

                var request = new byte[Protocol.DiagnosticsRequestLength];
                if (!await Protocol.ReadExactAsync(stream, request, timeout.Token) ||
                    !Protocol.TryParseDiagnosticsRequest(request, out var requesterId))
                {
                    return;
                }

                var remoteIp =
                    (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString()
                    ?? "";

                var authorized =
                    requesterId != Guid.Empty &&
                    _peers.TryGetValue(requesterId, out var requester) &&
                    DateTimeOffset.Now - requester.LastSeen <= TimeSpan.FromSeconds(35) &&
                    string.Equals(
                        requester.Address,
                        remoteIp,
                        StringComparison.OrdinalIgnoreCase);

                if (!authorized)
                {
                    await Protocol.WriteDiagnosticsErrorAsync(
                        stream,
                        "Diagnoseabruf abgelehnt: Das anfragende SimplePrint-Gerät ist aktuell nicht als aktives Netzwerkgerät bekannt.",
                        timeout.Token);
                    return;
                }

                var config = SnapshotConfig();
                var peers = _peers.Values
                    .Where(x => DateTimeOffset.Now - x.LastSeen <= TimeSpan.FromSeconds(35))
                    .OrderBy(x => x.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                _log.Info(
                    $"Diagnosepaket für '{requester!.DeviceName}' ({requesterId}) wird erstellt.");

                var archive = await UnifiedDiagnosticsBuilder.CreateArchiveAsync(
                    config,
                    peers,
                    timeout.Token);

                await Protocol.WriteDiagnosticsArchiveAsync(
                    stream,
                    archive,
                    timeout.Token);

                _log.Info(
                    $"Diagnosepaket mit {archive.Length:N0} Byte an '{requester.DeviceName}' übertragen.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error("Remote-Diagnose fehlgeschlagen", ex);

                try
                {
                    if (client.Connected)
                    {
                        using var stream = client.GetStream();
                        await Protocol.WriteDiagnosticsErrorAsync(
                            stream,
                            ex.Message,
                            CancellationToken.None);
                    }
                }
                catch
                {
                }
            }
            finally
            {
                _diagnosticsLock.Release();
            }
        }
    }

    private async Task RunGatewayAsync(CancellationToken ct)
    {
        var cfg = SnapshotConfig();
        var listener = new TcpListener(IPAddress.Any, cfg.GatewayPort);
        listener.Start(64);

        _log.Info($"Print-Gateway lauscht auf TCP {cfg.GatewayPort}.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(
                    () => HandleIncomingPrintAsync(client, ct),
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleIncomingPrintAsync(TcpClient client, CancellationToken ct)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        var remoteIp = remoteEndPoint?.Address.ToString() ?? "unbekannt";
        var remote = remoteEndPoint?.ToString() ?? "unbekannt";

        using (client)
        {
            Guid jobId = Guid.Empty;

            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                var header = new byte[Protocol.GatewayHeaderLength];
                if (!await Protocol.ReadExactAsync(stream, header, ct))
                    return;

                if (!Protocol.TryParseGatewayHeader(header, out var printerId, out jobId))
                    return;

                if (printerId == Guid.Empty && jobId == Guid.Empty)
                {
                    await stream.WriteAsync("SPROK2"u8.ToArray(), ct);
                    return;
                }

                if (printerId == Guid.Empty)
                {
                    await SendExistingJobStatusAsync(stream, jobId, ct);
                    return;
                }

                var cfg = SnapshotConfig();
                var printer = cfg.SharedPrinters
                    .FirstOrDefault(p => p.Id == printerId && p.Enabled);

                if (jobId == Guid.Empty)
                {
                    var health = printer is null
                        ? new PrinterHealthStatus
                        {
                            PrinterId = printerId,
                            Level = "Red",
                            Summary = "Der Drucker ist auf diesem SimplePrint-Gerät nicht freigegeben.",
                            CheckedAt = DateTimeOffset.Now
                        }
                        : await PrinterHealthProbe.ProbeAsync(
                            printer.QueueName,
                            printer.Id,
                            ct);

                    if (printer is not null)
                    {
                        health.PrinterName = string.IsNullOrWhiteSpace(printer.DisplayName)
                            ? printer.QueueName
                            : printer.DisplayName;
                    }

                    await Protocol.WritePrinterHealthAsync(stream, health, ct);
                    return;
                }

                if (printer is null)
                {
                    await Protocol.WriteJobAckAsync(
                        stream,
                        new PrintJobAck
                        {
                            JobId = jobId,
                            Success = false,
                            Status = "Fehler",
                            Message = "Der angeforderte Drucker ist auf diesem SimplePrint-Gerät nicht freigegeben."
                        },
                        ct);
                    return;
                }

                var routeGuard = await PrinterRouteGuard.CheckAsync(printer.QueueName);
                if (!routeGuard.Safe)
                {
                    await Protocol.WriteJobAckAsync(
                        stream,
                        new PrintJobAck
                        {
                            JobId = jobId,
                            Success = false,
                            Status = "Fehler",
                            Message = string.IsNullOrWhiteSpace(routeGuard.Reason)
                                ? "Druckauftrag aus Sicherheitsgründen blockiert."
                                : routeGuard.Reason,
                            UpdatedAt = DateTimeOffset.Now
                        },
                        ct);
                    return;
                }

                var peer = _peers.Values
                    .Where(x => x.Address == remoteIp)
                    .OrderByDescending(x => x.LastSeen)
                    .FirstOrDefault();

                var record = new PrintJobRecord
                {
                    JobId = jobId,
                    ClientId = peer?.DeviceId ?? Guid.Empty,
                    ClientName = peer?.DeviceName ?? remoteIp,
                    ServerId = cfg.DeviceId,
                    ServerName = cfg.DeviceName,
                    PrinterId = printer.Id,
                    PrinterName = string.IsNullOrWhiteSpace(printer.DisplayName)
                        ? printer.QueueName
                        : printer.DisplayName,
                    LocalPrinterName = printer.QueueName,
                    Status = "Empfangen",
                    Message = "Druckdaten werden von einem SimplePrint-Gerät empfangen.",
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now
                };

                _jobs[jobId] = record;
                await SaveJobsAsync();

                _log.Info($"Druckjob {jobId} von {remote} -> {record.PrinterName} begonnen.");

                var result = await RawPrinter.SendStreamAsync(
                    printer.QueueName,
                    stream,
                    $"SimplePrint {record.ClientName} {jobId:N}",
                    ct);

                if (result.Bytes == 0 || result.SpoolerJobId == 0)
                {
                    _jobs.TryRemove(jobId, out _);
                    await SaveJobsAsync();

                    await Protocol.WriteJobAckAsync(
                        stream,
                        new PrintJobAck
                        {
                            JobId = jobId,
                            Success = true,
                            Status = "Ignoriert",
                            Message = "Leere Portmonitor-Verbindung ignoriert.",
                            Bytes = 0
                        },
                        ct);
                    return;
                }

                record.Bytes = result.Bytes;
                record.SpoolerJobId = result.SpoolerJobId;
                record.Status = "Spooler";
                record.Message = $"An Windows-Spooler übergeben (Job {result.SpoolerJobId}).";
                record.UpdatedAt = DateTimeOffset.Now;
                await SaveJobsAsync();

                await Protocol.WriteJobAckAsync(stream, ToAck(record, true), ct);

                _ = Task.Run(
                    () => MonitorSpoolerJobAsync(
                        jobId,
                        printer.QueueName,
                        result.SpoolerJobId,
                        CancellationToken.None),
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error($"Druckjob {jobId} von {remote} fehlgeschlagen", ex);

                if (jobId == Guid.Empty)
                    return;

                var record = _jobs.GetOrAdd(
                    jobId,
                    id => new PrintJobRecord
                    {
                        JobId = id,
                        ClientName = remoteIp,
                        Status = "Fehler",
                        CreatedAt = DateTimeOffset.Now
                    });

                record.Status = "Fehler";
                record.Message = ex.Message;
                record.UpdatedAt = DateTimeOffset.Now;
                await SaveJobsAsync();

                try
                {
                    if (client.Connected)
                    {
                        using var stream = client.GetStream();
                        await Protocol.WriteJobAckAsync(
                            stream,
                            ToAck(record, false),
                            CancellationToken.None);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private async Task SendExistingJobStatusAsync(
        NetworkStream stream,
        Guid jobId,
        CancellationToken ct)
    {
        if (!_jobs.TryGetValue(jobId, out var record))
        {
            await Protocol.WriteJobAckAsync(
                stream,
                new PrintJobAck
                {
                    JobId = jobId,
                    Success = false,
                    Status = "Unbekannt",
                    Message = "Dieses SimplePrint-Gerät kennt den Druckauftrag nicht."
                },
                ct);
            return;
        }

        await Protocol.WriteJobAckAsync(
            stream,
            ToAck(record, !record.Status.Equals(
                "Fehler",
                StringComparison.OrdinalIgnoreCase)),
            ct);
    }

    private async Task MonitorSpoolerJobAsync(
        Guid jobId,
        string queueName,
        uint spoolerJobId,
        CancellationToken ct)
    {
        await Task.Delay(500, ct);

        string? lastStatus = null;
        string? lastMessage = null;

        for (var i = 0; i < 60 && !ct.IsCancellationRequested; i++)
        {
            try
            {
                var snapshot = RawPrinter.GetJobSnapshot(queueName, spoolerJobId);

                if (!snapshot.Exists)
                {
                    if (_jobs.TryGetValue(jobId, out var gone))
                    {
                        gone.Status = "Abgeschlossen";
                        gone.Message = "Auftrag an Drucker gesendet";
                        gone.UpdatedAt = DateTimeOffset.Now;
                        await SaveJobsAsync();
                    }
                    return;
                }

                var status = RawPrinter.IsConfirmedPrinted(snapshot.Status)
                    ? "Gedruckt"
                    : RawPrinter.IsFailure(snapshot.Status)
                        ? "Fehler"
                        : RawPrinter.IsPrinting(snapshot.Status)
                            ? "Druckt"
                            : RawPrinter.IsSpooling(snapshot.Status)
                                ? "Spoolt"
                                : "Spooler";

                var message = snapshot.StatusText;

                if (_jobs.TryGetValue(jobId, out var record) &&
                    (!string.Equals(status, lastStatus, StringComparison.Ordinal) ||
                     !string.Equals(message, lastMessage, StringComparison.Ordinal)))
                {
                    record.Status = status;
                    record.Message = message;
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();

                    lastStatus = status;
                    lastMessage = message;
                }

                if (status is "Gedruckt" or "Fehler")
                    return;
            }
            catch (Exception ex)
            {
                _log.Error($"Spoolerstatus für Job {jobId} konnte nicht gelesen werden", ex);
            }

            await Task.Delay(1000, ct);
        }
    }

    private Task SyncListenersAsync(CancellationToken serviceCt)
    {
        lock (_listenerLock)
        {
            var wanted = SnapshotConfig().NetworkPrinters
                .Where(m =>
                    m.Enabled &&
                    !PrinterTransport.IsDirect(m.TransportMode) &&
                    !string.IsNullOrWhiteSpace(m.PortName) &&
                    m.LocalProxyPort > 0)
                .GroupBy(m => m.PortName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToDictionary(m => m.PortName, StringComparer.OrdinalIgnoreCase);

            foreach (var old in _listeners.Keys
                         .Where(k => !wanted.ContainsKey(k))
                         .ToList())
            {
                var state = _listeners[old];
                state.Cts.Cancel();
                state.Listener.Stop();
                _listeners.Remove(old);
                _log.Info($"Lokaler Proxy gestoppt: {old}");
            }

            foreach (var mapping in wanted.Values)
            {
                if (_listeners.TryGetValue(mapping.PortName, out var existing))
                {
                    if (existing.Mapping.LocalProxyPort == mapping.LocalProxyPort &&
                        existing.Mapping.SourceDeviceId == mapping.SourceDeviceId &&
                        existing.Mapping.PrinterId == mapping.PrinterId)
                    {
                        continue;
                    }

                    existing.Cts.Cancel();
                    existing.Listener.Stop();
                    _listeners.Remove(mapping.PortName);
                }

                var linked =
                    CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
                var listener = new TcpListener(
                    IPAddress.Loopback,
                    mapping.LocalProxyPort);
                listener.Start(16);

                var task = Task.Run(
                    () => AcceptLoopAsync(mapping, listener, linked.Token),
                    CancellationToken.None);

                _listeners[mapping.PortName] = new ListenerState
                {
                    Mapping = CloneNetworkPrinter(mapping),
                    Listener = listener,
                    Cts = linked,
                    Task = task
                };

                _log.Info(
                    $"Lokaler Proxy {mapping.LocalProxyPort} -> " +
                    $"{mapping.SourceDeviceName}/{mapping.PrinterDisplayName} aktiv.");
            }
        }

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(
        NetworkPrinterMapping mapping,
        TcpListener listener,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var local = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(
                    () => ForwardJobAsync(mapping, local, ct),
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
            _log.Error($"Listener {mapping.LocalProxyPort} ausgefallen", ex);
        }
    }

    private async Task ForwardJobAsync(
        NetworkPrinterMapping mapping,
        TcpClient local,
        CancellationToken ct)
    {
        using (local)
        {
            try
            {
                using var source = local.GetStream();
                var buffer = new byte[64 * 1024];
                int firstRead;

                using (var firstByteTimeout =
                       CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    firstByteTimeout.CancelAfter(TimeSpan.FromSeconds(5));

                    try
                    {
                        firstRead = await source.ReadAsync(
                            buffer,
                            firstByteTimeout.Token);
                    }
                    catch (OperationCanceledException)
                        when (!ct.IsCancellationRequested)
                    {
                        return;
                    }
                }

                if (firstRead == 0)
                    return;

                var cfg = SnapshotConfig();
                var jobId = Guid.NewGuid();

                var record = new PrintJobRecord
                {
                    JobId = jobId,
                    ClientId = cfg.DeviceId,
                    ClientName = cfg.DeviceName,
                    ServerId = mapping.SourceDeviceId,
                    ServerName = mapping.SourceDeviceName,
                    PrinterId = mapping.PrinterId,
                    PrinterName = mapping.PrinterDisplayName,
                    LocalPrinterName = mapping.LocalPrinterName,
                    Status = "Warteschlange",
                    Message = "Windows hat Druckdaten an SimplePrint übergeben.",
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now
                };

                _jobs[jobId] = record;
                await SaveJobsAsync();

                try
                {
                    var endpoint = await GetPeerForMappingAsync(mapping, ct);
                    if (endpoint is null)
                    {
                        throw new InvalidOperationException(
                            $"SimplePrint-Gerät '{mapping.SourceDeviceName}' wurde im Netzwerk nicht gefunden.");
                    }

                    record.ServerName = endpoint.DeviceName;
                    record.Status = "Verbinde";
                    record.Message = $"Verbindung zu {endpoint.DeviceName} wird aufgebaut.";
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();

                    using var remote = new TcpClient { NoDelay = true };
                    using var timeout =
                        CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));

                    await remote.ConnectAsync(
                        IPAddress.Parse(endpoint.Address),
                        endpoint.GatewayPort,
                        timeout.Token);

                    using var target = remote.GetStream();
                    await target.WriteAsync(
                        Protocol.CreateGatewayHeader(mapping.PrinterId, jobId),
                        ct);

                    record.Status = "Überträgt";
                    record.Message =
                        $"Druckdaten werden an {endpoint.DeviceName} übertragen.";
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();

                    long total = firstRead;
                    await target.WriteAsync(buffer.AsMemory(0, firstRead), ct);

                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, ct);
                        if (read == 0)
                            break;

                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        total += read;
                    }

                    await target.FlushAsync(ct);

                    try
                    {
                        remote.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch
                    {
                    }

                    record.Bytes = total;
                    record.Status = "Übertragen";
                    record.Message =
                        $"{total:N0} Byte wurden vollständig übertragen.";
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();

                    using var ackTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(ct);
                    ackTimeout.CancelAfter(TimeSpan.FromSeconds(20));

                    var ack = await Protocol.ReadJobAckAsync(
                        target,
                        ackTimeout.Token);

                    if (ack is null)
                        throw new InvalidOperationException(
                            "Das Zielgerät hat den Druckauftrag nicht bestätigt.");

                    ApplyAck(record, ack);
                    await SaveJobsAsync();
                }
                catch (OperationCanceledException)
                    when (!ct.IsCancellationRequested)
                {
                    record.Status = "Fehler";
                    record.Message =
                        "Zeitüberschreitung bei der Druckübertragung oder Bestätigung.";
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    record.Status = "Fehler";
                    record.Message = ex.Message;
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();

                    _log.Error(
                        $"Weiterleitung für '{mapping.LocalPrinterName}' fehlgeschlagen",
                        ex);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"Lokale Verbindung für '{mapping.LocalPrinterName}' fehlgeschlagen",
                    ex);
            }
        }
    }

    private async Task<DevicePresence?> GetPeerForMappingAsync(
        NetworkPrinterMapping mapping,
        CancellationToken ct)
    {
        if (_peers.TryGetValue(mapping.SourceDeviceId, out var peer) &&
            DateTimeOffset.Now - peer.LastSeen <= TimeSpan.FromSeconds(30))
        {
            return peer;
        }

        await DiscoverNowAsync(ct);
        return _peers.TryGetValue(mapping.SourceDeviceId, out peer)
            ? peer
            : null;
    }

    private async Task RefreshPendingJobStatusesAsync(CancellationToken ct)
    {
        var pending = _jobs.Values
            .Where(x =>
                x.ServerId != SnapshotConfig().DeviceId &&
                x.CreatedAt > DateTimeOffset.Now.AddDays(-1) &&
                x.Status is not "Gedruckt" and
                not "Abgeschlossen" and
                not "Fehler")
            .OrderByDescending(x => x.UpdatedAt)
            .Take(20)
            .ToList();

        foreach (var job in pending)
        {
            if (!_peers.TryGetValue(job.ServerId, out var endpoint))
                continue;

            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                await tcp.ConnectAsync(
                    IPAddress.Parse(endpoint.Address),
                    endpoint.GatewayPort,
                    timeout.Token);

                using var stream = tcp.GetStream();
                await stream.WriteAsync(
                    Protocol.CreateGatewayHeader(Guid.Empty, job.JobId),
                    timeout.Token);

                var ack = await Protocol.ReadJobAckAsync(
                    stream,
                    timeout.Token);

                if (ack is null)
                    continue;

                ApplyAck(job, ack);
                await SaveJobsAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Die Statusabfrage darf den normalen Druckbetrieb nicht stören.
            }
        }
    }

    private static string GetAppVersion()
    {
        var version = typeof(DeviceWorker).Assembly.GetName().Version;
        return version is null
            ? "unbekannt"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static SharedPrinterConfig CloneSharedPrinter(
        SharedPrinterConfig source) =>
        new()
        {
            Id = source.Id,
            QueueName = source.QueueName,
            DisplayName = source.DisplayName,
            DriverName = source.DriverName,
            PortName = source.PortName,
            TransportMode = source.TransportMode,
            DirectAddress = source.DirectAddress,
            DeviceUuid = source.DeviceUuid,
            Enabled = source.Enabled
        };

    private static NetworkPrinterMapping CloneNetworkPrinter(
        NetworkPrinterMapping source) =>
        new()
        {
            SourceDeviceId = source.SourceDeviceId,
            PrinterId = source.PrinterId,
            SourceDeviceName = source.SourceDeviceName,
            PrinterDisplayName = source.PrinterDisplayName,
            LocalPrinterName = source.LocalPrinterName,
            DriverName = source.DriverName,
            PortName = source.PortName,
            LocalProxyPort = source.LocalProxyPort,
            TransportMode = source.TransportMode,
            DirectAddress = source.DirectAddress,
            DeviceUuid = source.DeviceUuid,
            UseExistingQueue = source.UseExistingQueue,
            Enabled = source.Enabled
        };

    private static DiscoveredPrinter CloneDiscoveredPrinter(
        DiscoveredPrinter source) =>
        new()
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            DriverName = source.DriverName,
            PortName = source.PortName,
            TransportMode = source.TransportMode,
            DirectAddress = source.DirectAddress,
            DeviceUuid = source.DeviceUuid,
            Status = source.Status
        };

    private static void ApplyAck(PrintJobRecord record, PrintJobAck ack)
    {
        record.Status = ack.Status;
        record.Message = ack.Message;
        record.Bytes = Math.Max(record.Bytes, ack.Bytes);
        record.SpoolerJobId = ack.SpoolerJobId;
        record.UpdatedAt = ack.UpdatedAt;
    }

    private static PrintJobAck ToAck(PrintJobRecord record, bool success) =>
        new()
        {
            JobId = record.JobId,
            Success = success,
            Status = record.Status,
            Message = record.Message,
            Bytes = record.Bytes,
            SpoolerJobId = record.SpoolerJobId,
            UpdatedAt = record.UpdatedAt
        };
}
