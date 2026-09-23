using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SimplePrint.Common;

namespace SimplePrint.Server.Service;

public sealed class ServerWorker : BackgroundService
{
    private readonly FileLog _log = new(AppPaths.ServerLog);
    private readonly object _configLock = new();
    private readonly ConcurrentDictionary<Guid, ClientPresence> _clients = new();
    private readonly ConcurrentDictionary<Guid, PrintJobRecord> _jobs = new();
    private readonly SemaphoreSlim _clientSaveLock = new(1, 1);
    private readonly SemaphoreSlim _jobSaveLock = new(1, 1);

    private ServerConfig _config = new();
    private DateTime _configWriteUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadConfig(true);
        LoadKnownClients();
        LoadKnownJobs();

        _log.Info($"Serverdienst gestartet. ServerId={_config.ServerId}, Discovery={_config.DiscoveryPort}, Gateway={_config.GatewayPort}");

        await Task.WhenAll(
            RunDiscoveryAsync(stoppingToken),
            RunGatewayAsync(stoppingToken),
            RunReloadLoopAsync(stoppingToken));
    }

    private void LoadKnownClients()
    {
        try
        {
            foreach (var client in JsonStore.LoadOrCreate(AppPaths.ServerClients, () => new List<ClientPresence>()))
                _clients[client.ClientId] = client;
        }
        catch (Exception ex)
        {
            _log.Error("Client-Liste konnte nicht geladen werden", ex);
        }
    }

    private void LoadKnownJobs()
    {
        try
        {
            foreach (var job in JsonStore.LoadOrCreate(AppPaths.ServerJobs, () => new List<PrintJobRecord>()))
                _jobs[job.JobId] = job;
        }
        catch (Exception ex)
        {
            _log.Error("Druckaufträge konnten nicht geladen werden", ex);
        }
    }

    private async Task SaveClientsAsync()
    {
        ApplyOfflineClientDeletions();
        await _clientSaveLock.WaitAsync();
        try
        {
            JsonStore.Save(
                AppPaths.ServerClients,
                _clients.Values
                    .OrderBy(x => x.ClientName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList());
        }
        catch (Exception ex)
        {
            _log.Error("Client-Liste konnte nicht gespeichert werden", ex);
        }
        finally
        {
            _clientSaveLock.Release();
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

            JsonStore.Save(AppPaths.ServerJobs, keep);
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

    private void ApplyOfflineClientDeletions()
    {
        try
        {
            if (!File.Exists(AppPaths.ServerClients)) return;

            var persisted = JsonStore.LoadOrCreate(AppPaths.ServerClients, () => new List<ClientPresence>());
            var persistedIds = persisted.Select(x => x.ClientId).ToHashSet();
            var now = DateTimeOffset.Now;

            foreach (var item in _clients.ToArray())
            {
                if (persistedIds.Contains(item.Key)) continue;
                if (now - item.Value.LastSeen <= TimeSpan.FromSeconds(35)) continue;
                _clients.TryRemove(item.Key, out _);
            }
        }
        catch
        {
            // Nur eine Komfortfunktion für manuell gelöschte Offline-Clients.
        }
    }

    private ServerConfig SnapshotConfig()
    {
        lock (_configLock)
        {
            return new ServerConfig
            {
                ServerId = _config.ServerId,
                ServerName = _config.ServerName,
                DiscoveryPort = _config.DiscoveryPort,
                GatewayPort = _config.GatewayPort,
                Printers = _config.Printers.Select(p => new SharedPrinterConfig
                {
                    Id = p.Id,
                    QueueName = p.QueueName,
                    DisplayName = p.DisplayName,
                    DriverName = p.DriverName,
                    Enabled = p.Enabled
                }).ToList()
            };
        }
    }

    private void LoadConfig(bool force = false)
    {
        try
        {
            var write = File.Exists(AppPaths.ServerConfig)
                ? File.GetLastWriteTimeUtc(AppPaths.ServerConfig)
                : DateTime.MinValue;

            if (!force && write == _configWriteUtc) return;

            var config = JsonStore.LoadOrCreate(AppPaths.ServerConfig, () => new ServerConfig());
            lock (_configLock) _config = config;
            _configWriteUtc = File.GetLastWriteTimeUtc(AppPaths.ServerConfig);

            _log.Info($"Konfiguration geladen: {config.Printers.Count} Drucker.");
        }
        catch (Exception ex)
        {
            _log.Error("Konfiguration konnte nicht geladen werden", ex);
        }
    }

    private async Task RunReloadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            LoadConfig();
            ApplyOfflineClientDeletions();
            await Task.Delay(1500, ct);
        }
    }

    private async Task RunDiscoveryAsync(CancellationToken ct)
    {
        var cfg = SnapshotConfig();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, cfg.DiscoveryPort));

        _log.Info($"Discovery und Client-Präsenz lauschen auf UDP {cfg.DiscoveryPort}.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct);

                if (result.Buffer.AsSpan().SequenceEqual(Protocol.DiscoveryRequestBytes))
                {
                    var current = SnapshotConfig();
                    var response = new DiscoveryAnnouncement
                    {
                        ServerId = current.ServerId,
                        ServerName = string.IsNullOrWhiteSpace(current.ServerName)
                            ? Environment.MachineName
                            : current.ServerName,
                        GatewayPort = current.GatewayPort,
                        Printers = current.Printers
                            .Where(p => p.Enabled)
                            .Select(p => new DiscoveredPrinter
                            {
                                Id = p.Id,
                                DisplayName = string.IsNullOrWhiteSpace(p.DisplayName)
                                    ? p.QueueName
                                    : p.DisplayName,
                                DriverName = p.DriverName,
                                Status = RawPrinter.CanOpen(p.QueueName)
                                    ? "Bereit"
                                    : "Nicht verfügbar"
                            })
                            .ToList()
                    };

                    await udp.SendAsync(
                        Protocol.SerializeAnnouncement(response),
                        result.RemoteEndPoint,
                        ct);

                    continue;
                }

                var heartbeat = Protocol.DeserializeClientHeartbeat(result.Buffer);
                if (heartbeat is null || heartbeat.ClientId == Guid.Empty)
                    continue;

                _clients[heartbeat.ClientId] = new ClientPresence
                {
                    ClientId = heartbeat.ClientId,
                    ClientName = string.IsNullOrWhiteSpace(heartbeat.ClientName)
                        ? result.RemoteEndPoint.Address.ToString()
                        : heartbeat.ClientName,
                    Address = result.RemoteEndPoint.Address.ToString(),
                    AgentVersion = heartbeat.AgentVersion,
                    PreferredServerId = heartbeat.PreferredServerId,
                    InstalledPrinterCount = heartbeat.InstalledPrinterCount,
                    LastSeen = DateTimeOffset.Now
                };

                await SaveClientsAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error("Discovery-/Client-Präsenz-Fehler", ex);
                await Task.Delay(1000, ct);
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
                    () => HandleClientAsync(client, ct),
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

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
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
                    await stream.WriteAsync("SPROK1"u8.ToArray(), ct);
                    return;
                }

                if (printerId == Guid.Empty)
                {
                    await SendExistingJobStatusAsync(stream, jobId, ct);
                    return;
                }

                if (jobId == Guid.Empty)
                    jobId = Guid.NewGuid();

                var cfg = SnapshotConfig();
                var printer = cfg.Printers.FirstOrDefault(p => p.Id == printerId && p.Enabled);

                if (printer is null)
                {
                    await Protocol.WriteJobAckAsync(
                        stream,
                        new PrintJobAck
                        {
                            JobId = jobId,
                            Success = false,
                            Status = "Fehler",
                            Message = "Der angeforderte Drucker ist auf dem Server nicht freigegeben."
                        },
                        ct);
                    return;
                }

                var clientPresence = _clients.Values
                    .Where(x => x.Address == remoteIp)
                    .OrderByDescending(x => x.LastSeen)
                    .FirstOrDefault();

                var record = new PrintJobRecord
                {
                    JobId = jobId,
                    ClientId = clientPresence?.ClientId ?? Guid.Empty,
                    ClientName = clientPresence?.ClientName ?? remoteIp,
                    ServerId = cfg.ServerId,
                    ServerName = cfg.ServerName,
                    PrinterId = printer.Id,
                    PrinterName = printer.DisplayName,
                    LocalPrinterName = printer.QueueName,
                    Status = "Empfangen",
                    Message = "Druckdaten werden vom Client empfangen.",
                    CreatedAt = DateTimeOffset.Now,
                    UpdatedAt = DateTimeOffset.Now
                };

                _jobs[jobId] = record;
                await SaveJobsAsync();

                _log.Info($"Druckjob {jobId} von {remote} -> {printer.DisplayName} begonnen.");

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

                    _log.Info($"Leere Druckverbindung {jobId} von {remote} ignoriert.");
                    return;
                }

                record.Bytes = result.Bytes;
                record.SpoolerJobId = result.SpoolerJobId;
                record.Status = "Spooler";
                record.Message = $"An Windows-Spooler übergeben (Job {result.SpoolerJobId}).";
                record.UpdatedAt = DateTimeOffset.Now;

                await SaveJobsAsync();

                await Protocol.WriteJobAckAsync(
                    stream,
                    ToAck(record, true),
                    ct);

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

                if (jobId != Guid.Empty)
                {
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
                    Message = "Der Server kennt diesen Druckauftrag nicht."
                },
                ct);
            return;
        }

        await Protocol.WriteJobAckAsync(
            stream,
            ToAck(record, !record.Status.Equals("Fehler", StringComparison.OrdinalIgnoreCase)),
            ct);
    }

    private async Task MonitorSpoolerJobAsync(
        Guid jobId,
        string queueName,
        uint spoolerJobId,
        CancellationToken ct)
    {
        await Task.Delay(500, ct);

        var seenInQueue = false;
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
                        gone.Message = seenInQueue
                            ? "Auftrag an Drucker gesendet"
                            : "Auftrag an Drucker gesendet";
                        gone.UpdatedAt = DateTimeOffset.Now;
                        await SaveJobsAsync();
                    }
                    return;
                }

                seenInQueue = true;

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

        if (_jobs.TryGetValue(jobId, out var timeout))
        {
            timeout.Status = "Status offen";
            timeout.Message = "Der Windows-Spooler hat innerhalb von 60 Sekunden keinen eindeutigen Endstatus gemeldet.";
            timeout.UpdatedAt = DateTimeOffset.Now;
            await SaveJobsAsync();
        }
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
