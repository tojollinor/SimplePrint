using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SimplePrint.Common;

namespace SimplePrint.Client.Service;

public sealed class ClientWorker : BackgroundService
{
    private sealed record Endpoint(
        IPAddress Address,
        int GatewayPort,
        DateTimeOffset SeenAt,
        string ServerName,
        int ProtocolVersion,
        string AppVersion);

    private sealed class ListenerState
    {
        public required ClientPrinterMapping Mapping { get; init; }
        public required TcpListener Listener { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Task { get; init; }
    }

    private readonly FileLog _log = new(AppPaths.ClientLog);
    private readonly ConcurrentDictionary<Guid, Endpoint> _servers = new();
    private readonly ConcurrentDictionary<Guid, PrintJobRecord> _jobs = new();
    private readonly Dictionary<string, ListenerState> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _jobSaveLock = new(1, 1);
    private readonly SemaphoreSlim _diagnosticsLock = new(1, 1);
    private readonly object _sync = new();

    private ClientConfig _config = new();
    private DateTime _writeUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadConfig(true);
        LoadKnownJobs();

        _log.Info($"Client-Agent gestartet. ClientId={_config.ClientId}");

        await SyncListenersAsync(stoppingToken);

        await Task.WhenAll(
            DiscoveryLoopAsync(stoppingToken),
            ReloadLoopAsync(stoppingToken),
            DiagnosticsLoopAsync(stoppingToken));
    }

    private void LoadConfig(bool force = false)
    {
        var write = File.Exists(AppPaths.ClientConfig)
            ? File.GetLastWriteTimeUtc(AppPaths.ClientConfig)
            : DateTime.MinValue;

        if (!force && write == _writeUtc) return;

        _config = JsonStore.LoadOrCreate(AppPaths.ClientConfig, () => new ClientConfig());

        if (_config.ClientId == Guid.Empty)
        {
            _config.ClientId = Guid.NewGuid();
            JsonStore.Save(AppPaths.ClientConfig, _config);
        }

        _writeUtc = File.GetLastWriteTimeUtc(AppPaths.ClientConfig);
    }

    private void LoadKnownJobs()
    {
        try
        {
            foreach (var job in JsonStore.LoadOrCreate(AppPaths.ClientJobs, () => new List<PrintJobRecord>()))
                _jobs[job.JobId] = job;
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
                .Take(200)
                .ToList();

            var keepIds = keep.Select(x => x.JobId).ToHashSet();
            foreach (var old in _jobs.Keys.Where(x => !keepIds.Contains(x)).ToList())
                _jobs.TryRemove(old, out _);

            JsonStore.Save(AppPaths.ClientJobs, keep);
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

    private async Task ReloadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var before = _writeUtc;
            LoadConfig();

            if (before != _writeUtc)
                await SyncListenersAsync(ct);

            await Task.Delay(1200, ct);
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SendHeartbeatAsync(ct);
            await DiscoverNowAsync(ct);
            await RefreshPendingJobStatusesAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            var version = typeof(ClientWorker).Assembly.GetName().Version;

            var heartbeat = new ClientHeartbeat
            {
                ClientId = _config.ClientId,
                ClientName = Environment.MachineName,
                AgentVersion = version is null
                    ? "unbekannt"
                    : $"{version.Major}.{version.Minor}.{version.Build}",
                PreferredServerId = _config.PreferredServerId,
                InstalledPrinterCount = _config.Mappings.Count(x => x.Enabled),
                DiagnosticsPort = Protocol.DefaultClientDiagnosticsPort
            };

            await Discovery.SendClientHeartbeatAsync(
                heartbeat,
                _config.DiscoveryPort,
                _config.ManualServer,
                ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Client-Lebenszeichen fehlgeschlagen", ex);
        }
    }

    private async Task DiscoverNowAsync(CancellationToken ct)
    {
        try
        {
            var servers = await Discovery.DiscoverAsync(
                _config.DiscoveryPort,
                1000,
                ct,
                _config.ManualServer);

            foreach (var server in servers)
            {
                _servers[server.Announcement.ServerId] = new Endpoint(
                    server.Address,
                    server.Announcement.GatewayPort,
                    server.SeenAt,
                    server.Announcement.ServerName,
                    server.Announcement.Version,
                    server.Announcement.AppVersion);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Discovery fehlgeschlagen", ex);
        }
    }

    private async Task DiagnosticsLoopAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, Protocol.DefaultClientDiagnosticsPort);
        listener.Start(8);

        _log.Info($"Client-Diagnose lauscht auf TCP {Protocol.DefaultClientDiagnosticsPort}.");

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
                    !Protocol.TryParseDiagnosticsRequest(request, out var serverId))
                    return;

                var authorized =
                    _servers.TryGetValue(serverId, out var endpoint) &&
                    DateTimeOffset.Now - endpoint.SeenAt <= TimeSpan.FromMinutes(2) &&
                    (_config.PreferredServerId is null ||
                     _config.PreferredServerId == serverId);

                if (!authorized)
                {
                    await Protocol.WriteDiagnosticsErrorAsync(
                        stream,
                        "Diagnoseabruf abgelehnt: Der anfragende Server ist diesem Client nicht aktuell zugeordnet.",
                        timeout.Token);
                    return;
                }

                var discovered = _servers
                    .Select(x => new DiscoveredServer(
                        new DiscoveryAnnouncement
                        {
                            ServerId = x.Key,
                            ServerName = x.Value.ServerName,
                            GatewayPort = x.Value.GatewayPort,
                            Version = x.Value.ProtocolVersion,
                            AppVersion = x.Value.AppVersion
                        },
                        x.Value.Address,
                        x.Value.SeenAt))
                    .ToList();

                _log.Info($"Vollständiges Diagnosepaket für Server {serverId} wird erstellt.");

                var archive = await ClientDiagnosticsBuilder.CreateArchiveAsync(
                    _config,
                    discovered,
                    timeout.Token);

                await Protocol.WriteDiagnosticsArchiveAsync(
                    stream,
                    archive,
                    timeout.Token);

                _log.Info($"Diagnosepaket mit {archive.Length:N0} Byte an Server übertragen.");
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

    private async Task RefreshPendingJobStatusesAsync(CancellationToken ct)
    {
        var pending = _jobs.Values
            .Where(x =>
                x.CreatedAt > DateTimeOffset.Now.AddDays(-1) &&
                x.Status is not "Gedruckt" and not "Abgeschlossen" and not "Fehler")
            .OrderByDescending(x => x.UpdatedAt)
            .Take(20)
            .ToList();

        foreach (var job in pending)
        {
            if (!_servers.TryGetValue(job.ServerId, out var endpoint))
                continue;

            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                await tcp.ConnectAsync(endpoint.Address, endpoint.GatewayPort, timeout.Token);

                using var stream = tcp.GetStream();
                await stream.WriteAsync(
                    Protocol.CreateGatewayHeader(Guid.Empty, job.JobId),
                    timeout.Token);

                var ack = await Protocol.ReadJobAckAsync(stream, timeout.Token);
                if (ack is null) continue;

                ApplyAck(job, ack);
                await SaveJobsAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // Statusabfrage darf den normalen Clientbetrieb nicht stören.
            }
        }
    }

    private Task SyncListenersAsync(CancellationToken serviceCt)
    {
        lock (_sync)
        {
            var wanted = _config.Mappings
                .Where(m => m.Enabled && !PrinterTransport.IsDirect(m.TransportMode))
                .ToDictionary(m => m.PortName, StringComparer.OrdinalIgnoreCase);

            foreach (var old in _listeners.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
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
                        existing.Mapping.ServerId == mapping.ServerId &&
                        existing.Mapping.PrinterId == mapping.PrinterId)
                        continue;

                    existing.Cts.Cancel();
                    existing.Listener.Stop();
                    _listeners.Remove(mapping.PortName);
                }

                var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
                var listener = new TcpListener(IPAddress.Loopback, mapping.LocalProxyPort);
                listener.Start(16);

                var task = Task.Run(
                    () => AcceptLoopAsync(mapping, listener, linked.Token),
                    CancellationToken.None);

                _listeners[mapping.PortName] = new ListenerState
                {
                    Mapping = mapping,
                    Listener = listener,
                    Cts = linked,
                    Task = task
                };

                _log.Info($"Lokaler Proxy {mapping.LocalProxyPort} -> {mapping.ServerName}/{mapping.PrinterDisplayName} aktiv.");
            }
        }

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(
        ClientPrinterMapping mapping,
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
        ClientPrinterMapping mapping,
        TcpClient local,
        CancellationToken ct)
    {
        using (local)
        {
            try
            {
                using var source = local.GetStream();

                // Windows' Standard-TCP/IP-Portmonitor öffnet regelmäßig reine
                // Prüfverbindungen ohne Druckdaten. Diese dürfen niemals als
                // Druckauftrag an den Server weitergereicht werden.
                var buffer = new byte[64 * 1024];
                int firstRead;

                using (var firstByteTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    firstByteTimeout.CancelAfter(TimeSpan.FromSeconds(5));

                    try
                    {
                        firstRead = await source.ReadAsync(buffer, firstByteTimeout.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _log.Info($"{mapping.LocalPrinterName}: leere/zeitüberschrittene Portmonitor-Verbindung ignoriert.");
                        return;
                    }
                }

                if (firstRead == 0)
                {
                    _log.Info($"{mapping.LocalPrinterName}: leere Portmonitor-Verbindung ignoriert.");
                    return;
                }

                var jobId = Guid.NewGuid();

                var record = new PrintJobRecord
                {
                    JobId = jobId,
                    ClientId = _config.ClientId,
                    ClientName = Environment.MachineName,
                    ServerId = mapping.ServerId,
                    ServerName = mapping.ServerName,
                    PrinterId = mapping.PrinterId,
                    PrinterName = mapping.PrinterDisplayName,
                    LocalPrinterName = mapping.LocalPrinterName,
                    Status = "Warteschlange",
                    Message = "Windows hat Druckdaten an den lokalen SimplePrint-Proxy übergeben.",
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now
                };

                _jobs[jobId] = record;
                await SaveJobsAsync();

                try
                {
                    if (!_servers.TryGetValue(mapping.ServerId, out var endpoint) ||
                        DateTimeOffset.Now - endpoint.SeenAt > TimeSpan.FromMinutes(2))
                    {
                        await DiscoverNowAsync(ct);
                        _servers.TryGetValue(mapping.ServerId, out endpoint);
                    }

                    if (endpoint is null)
                        throw new InvalidOperationException($"Server '{mapping.ServerName}' wurde im Netzwerk nicht gefunden.");

                    record.Status = "Verbinde";
                    record.Message = $"Verbindung zu {endpoint.ServerName} wird aufgebaut.";
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();

                    using var remote = new TcpClient { NoDelay = true };
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));

                    await remote.ConnectAsync(
                        endpoint.Address,
                        endpoint.GatewayPort,
                        timeout.Token);

                    using var target = remote.GetStream();

                    await target.WriteAsync(
                        Protocol.CreateGatewayHeader(mapping.PrinterId, jobId),
                        ct);

                    record.Status = "Überträgt";
                    record.Message = $"Druckdaten werden an {endpoint.ServerName} übertragen.";
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();

                    long total = firstRead;
                    await target.WriteAsync(buffer.AsMemory(0, firstRead), ct);

                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, ct);
                        if (read == 0) break;

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
                    record.Message = $"{total:N0} Byte wurden vollständig an den Server übertragen.";
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();

                    using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    ackTimeout.CancelAfter(TimeSpan.FromSeconds(20));

                    var ack = await Protocol.ReadJobAckAsync(target, ackTimeout.Token);

                    if (ack is null)
                        throw new InvalidOperationException("Der Server hat den Druckauftrag nicht bestätigt.");

                    ApplyAck(record, ack);
                    await SaveJobsAsync();

                    _log.Info(
                        $"{mapping.LocalPrinterName}: Job {jobId} mit {total:N0} Byte -> {endpoint.ServerName}/{mapping.PrinterDisplayName}, Serverstatus={record.Status}.");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    record.Status = "Fehler";
                    record.Message = "Zeitüberschreitung bei der Druckübertragung oder Serverbestätigung.";
                    record.UpdatedAt = DateTimeOffset.Now;
                    await SaveJobsAsync();

                    _log.Error(
                        $"Weiterleitung für '{mapping.LocalPrinterName}' fehlgeschlagen",
                        new TimeoutException(record.Message));
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

                    _log.Error($"Weiterleitung für '{mapping.LocalPrinterName}' fehlgeschlagen", ex);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error($"Lokale Verbindung für '{mapping.LocalPrinterName}' fehlgeschlagen", ex);
            }
        }
    }

    private static void ApplyAck(PrintJobRecord record, PrintJobAck ack)
    {
        record.Status = ack.Status;
        record.Message = ack.Message;
        record.Bytes = Math.Max(record.Bytes, ack.Bytes);
        record.SpoolerJobId = ack.SpoolerJobId;
        record.UpdatedAt = ack.UpdatedAt;
    }
}