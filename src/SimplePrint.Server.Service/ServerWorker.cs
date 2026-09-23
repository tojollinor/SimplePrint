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
    private readonly SemaphoreSlim _clientSaveLock = new(1, 1);
    private ServerConfig _config = new();
    private DateTime _configWriteUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadConfig(true);
        LoadKnownClients();
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

    private async Task SaveClientsAsync()
    {
        await _clientSaveLock.WaitAsync();
        try
        {
            JsonStore.Save(
                AppPaths.ServerClients,
                _clients.Values.OrderBy(x => x.ClientName, StringComparer.CurrentCultureIgnoreCase).ToList());
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
                        ServerName = string.IsNullOrWhiteSpace(current.ServerName) ? Environment.MachineName : current.ServerName,
                        GatewayPort = current.GatewayPort,
                        Printers = current.Printers.Where(p => p.Enabled).Select(p => new DiscoveredPrinter
                        {
                            Id = p.Id,
                            DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.QueueName : p.DisplayName,
                            DriverName = p.DriverName,
                            Status = RawPrinter.CanOpen(p.QueueName) ? "Bereit" : "Nicht verfügbar"
                        }).ToList()
                    };

                    await udp.SendAsync(Protocol.SerializeAnnouncement(response), result.RemoteEndPoint, ct);
                    continue;
                }

                var heartbeat = Protocol.DeserializeClientHeartbeat(result.Buffer);
                if (heartbeat is null || heartbeat.ClientId == Guid.Empty) continue;

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
            catch (OperationCanceledException) { break; }
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
                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unbekannt";
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                var header = new byte[Protocol.GatewayHeaderLength];
                if (!await Protocol.ReadExactAsync(stream, header, ct)) return;
                if (!Protocol.TryParseGatewayHeader(header, out var printerId)) return;

                if (printerId == Guid.Empty)
                {
                    await stream.WriteAsync("SPROK1"u8.ToArray(), ct);
                    return;
                }

                var cfg = SnapshotConfig();
                var printer = cfg.Printers.FirstOrDefault(p => p.Id == printerId && p.Enabled);
                if (printer is null) return;

                _log.Info($"Druckjob von {remote} -> {printer.DisplayName} begonnen.");
                var bytes = await RawPrinter.SendStreamAsync(
                    printer.QueueName,
                    stream,
                    $"SimplePrint {remote}",
                    ct);
                _log.Info($"Druckjob -> {printer.DisplayName} abgeschlossen, {bytes:N0} Byte RAW.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Error($"Druckjob von {remote} fehlgeschlagen", ex);
            }
        }
    }
}
