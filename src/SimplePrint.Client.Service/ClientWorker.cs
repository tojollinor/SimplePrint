using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SimplePrint.Common;

namespace SimplePrint.Client.Service;

public sealed class ClientWorker : BackgroundService
{
    private sealed record Endpoint(IPAddress Address, int GatewayPort, DateTimeOffset SeenAt, string ServerName);
    private sealed class ListenerState
    {
        public required ClientPrinterMapping Mapping { get; init; }
        public required TcpListener Listener { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Task { get; init; }
    }

    private readonly FileLog _log = new(AppPaths.ClientLog);
    private readonly ConcurrentDictionary<Guid, Endpoint> _servers = new();
    private readonly Dictionary<string, ListenerState> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private ClientConfig _config = new();
    private DateTime _writeUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadConfig(true);
        _log.Info($"Client-Agent gestartet. ClientId={_config.ClientId}");
        await SyncListenersAsync(stoppingToken);

        await Task.WhenAll(
            DiscoveryLoopAsync(stoppingToken),
            ReloadLoopAsync(stoppingToken));
    }

    private void LoadConfig(bool force = false)
    {
        var write = File.Exists(AppPaths.ClientConfig) ? File.GetLastWriteTimeUtc(AppPaths.ClientConfig) : DateTime.MinValue;
        if (!force && write == _writeUtc) return;

        _config = JsonStore.LoadOrCreate(AppPaths.ClientConfig, () => new ClientConfig());
        if (_config.ClientId == Guid.Empty)
        {
            _config.ClientId = Guid.NewGuid();
            JsonStore.Save(AppPaths.ClientConfig, _config);
        }
        _writeUtc = File.GetLastWriteTimeUtc(AppPaths.ClientConfig);
    }

    private async Task ReloadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var before = _writeUtc;
            LoadConfig();
            if (before != _writeUtc) await SyncListenersAsync(ct);
            await Task.Delay(1200, ct);
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SendHeartbeatAsync(ct);
            await DiscoverNowAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            var v = typeof(ClientWorker).Assembly.GetName().Version;
            var heartbeat = new ClientHeartbeat
            {
                ClientId = _config.ClientId,
                ClientName = Environment.MachineName,
                AgentVersion = v is null ? "unbekannt" : $"{v.Major}.{v.Minor}.{v.Build}",
                PreferredServerId = _config.PreferredServerId,
                InstalledPrinterCount = _config.Mappings.Count(x => x.Enabled)
            };
            await Discovery.SendClientHeartbeatAsync(heartbeat, _config.DiscoveryPort, _config.ManualServer, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("Client-Lebenszeichen fehlgeschlagen", ex); }
    }

    private async Task DiscoverNowAsync(CancellationToken ct)
    {
        try
        {
            var servers = await Discovery.DiscoverAsync(_config.DiscoveryPort, 1000, ct, _config.ManualServer);
            foreach (var s in servers)
                _servers[s.Announcement.ServerId] = new Endpoint(s.Address, s.Announcement.GatewayPort, s.SeenAt, s.Announcement.ServerName);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("Discovery fehlgeschlagen", ex); }
    }

    private Task SyncListenersAsync(CancellationToken serviceCt)
    {
        lock (_sync)
        {
            var wanted = _config.Mappings.Where(m => m.Enabled).ToDictionary(m => m.PortName, StringComparer.OrdinalIgnoreCase);

            foreach (var old in _listeners.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                var state = _listeners[old];
                state.Cts.Cancel();
                state.Listener.Stop();
                _listeners.Remove(old);
                _log.Info($"Lokaler Proxy gestoppt: {old}");
            }

            foreach (var m in wanted.Values)
            {
                if (_listeners.TryGetValue(m.PortName, out var existing))
                {
                    if (existing.Mapping.LocalProxyPort == m.LocalProxyPort &&
                        existing.Mapping.ServerId == m.ServerId &&
                        existing.Mapping.PrinterId == m.PrinterId)
                        continue;

                    existing.Cts.Cancel();
                    existing.Listener.Stop();
                    _listeners.Remove(m.PortName);
                }

                var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
                var listener = new TcpListener(IPAddress.Loopback, m.LocalProxyPort);
                listener.Start(16);
                var task = Task.Run(() => AcceptLoopAsync(m, listener, linked.Token), CancellationToken.None);

                _listeners[m.PortName] = new ListenerState
                {
                    Mapping = m,
                    Listener = listener,
                    Cts = linked,
                    Task = task
                };

                _log.Info($"Lokaler Proxy {m.LocalProxyPort} -> {m.ServerName}/{m.PrinterDisplayName} aktiv.");
            }
        }

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(ClientPrinterMapping mapping, TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var local = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => ForwardJobAsync(mapping, local, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _log.Error($"Listener {mapping.LocalProxyPort} ausgefallen", ex); }
    }

    private async Task ForwardJobAsync(ClientPrinterMapping mapping, TcpClient local, CancellationToken ct)
    {
        using (local)
        {
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

                using var remote = new TcpClient { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await remote.ConnectAsync(endpoint.Address, endpoint.GatewayPort, timeout.Token);

                using var source = local.GetStream();
                using var target = remote.GetStream();
                await target.WriteAsync(Protocol.CreateGatewayHeader(mapping.PrinterId), ct);

                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                }

                await target.FlushAsync(ct);
                try { remote.Client.Shutdown(SocketShutdown.Send); } catch { }
                _log.Info($"{mapping.LocalPrinterName}: {total:N0} Byte an {endpoint.ServerName}/{mapping.PrinterDisplayName} übertragen.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.Error($"Weiterleitung für '{mapping.LocalPrinterName}' fehlgeschlagen", ex); }
        }
    }
}
