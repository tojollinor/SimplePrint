using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SimplePrint.Common;

public static class Discovery
{
    // Windows meldet bei UDP ein ICMP "Port unreachable" als SocketError.ConnectionReset
    // (10054) beim nächsten Receive. Das würde sonst die ganze Empfangsschleife beenden.
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromMilliseconds(1500);

    public static void DisableUdpConnectionReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            udp.Client.IOControl(SioUdpConnReset, new byte[4], null);
        }
        catch
        {
        }
    }

    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverDevicesAsync(
        int port = Protocol.DefaultDiscoveryPort,
        int waitMs = 900,
        CancellationToken ct = default,
        IEnumerable<string>? manualPeers = null)
    {
        var found = new Dictionary<Guid, DiscoveredDevice>();
        var targets = new HashSet<IPAddress>(await GetTargetsAsync(null, ct));

        if (manualPeers is not null)
        {
            var resolved = await Task.WhenAll(
                manualPeers
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => ResolveHostAsync(x, ct)));

            foreach (var list in resolved)
            {
                foreach (var target in list)
                    targets.Add(target);
            }
        }

        using var udp = CreateClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(waitMs);

        var request = Protocol.DeviceDiscoveryRequestBytes;
        await SendToAllAsync(udp, request, targets, port, deadline.Token);
        var repeat = RepeatSendAsync(udp, request, targets, port, deadline.Token);

        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(deadline.Token);
                var announcement = Protocol.DeserializeDeviceAnnouncement(result.Buffer);
                if (announcement is null || announcement.DeviceId == Guid.Empty)
                    continue;

                found[announcement.DeviceId] = new DiscoveredDevice(
                    announcement,
                    result.RemoteEndPoint.Address,
                    DateTimeOffset.Now);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
            catch (SocketException) { break; }
        }

        await repeat;

        return found.Values
            .OrderBy(x => x.Announcement.DeviceName)
            .ToList();
    }

    public static async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        int port = Protocol.DefaultDiscoveryPort,
        int waitMs = 900,
        CancellationToken ct = default,
        string? manualHost = null)
    {
        var found = new Dictionary<Guid, DiscoveredServer>();
        var targets = await GetTargetsAsync(manualHost, ct);

        using var udp = CreateClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(waitMs);

        var request = Protocol.DiscoveryRequestBytes;
        await SendToAllAsync(udp, request, targets, port, deadline.Token);
        var repeat = RepeatSendAsync(udp, request, targets, port, deadline.Token);

        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(deadline.Token);
                var announcement = Protocol.DeserializeAnnouncement(result.Buffer);
                if (announcement is null) continue;

                found[announcement.ServerId] = new DiscoveredServer(
                    announcement,
                    result.RemoteEndPoint.Address,
                    DateTimeOffset.Now);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
            catch (SocketException) { break; }
        }

        await repeat;

        return found.Values.OrderBy(x => x.Announcement.ServerName).ToList();
    }

    public static async Task SendClientHeartbeatAsync(
        ClientHeartbeat heartbeat,
        int port,
        string? manualHost,
        CancellationToken ct = default)
    {
        var targets = await GetTargetsAsync(manualHost, ct);
        var bytes = Protocol.SerializeClientHeartbeat(heartbeat);

        using var udp = CreateClient();
        await SendToAllAsync(udp, bytes, targets, port, ct);
    }

    private static UdpClient CreateClient()
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        DisableUdpConnectionReset(udp);
        return udp;
    }

    private static async Task SendToAllAsync(
        UdpClient udp,
        ReadOnlyMemory<byte> bytes,
        IEnumerable<IPAddress> targets,
        int port,
        CancellationToken ct)
    {
        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(bytes, new IPEndPoint(target, port), ct);
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Ein verlorenes UDP-Paket soll kein Gerät "verschwinden" lassen.
    private static async Task RepeatSendAsync(
        UdpClient udp,
        ReadOnlyMemory<byte> bytes,
        IEnumerable<IPAddress> targets,
        int port,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(RepeatDelay, ct);
            await SendToAllAsync(udp, bytes, targets, port, ct);
        }
        catch
        {
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveHostAsync(
        string manualHost,
        CancellationToken ct)
    {
        var host = manualHost.Trim();

        if (IPAddress.TryParse(host, out var direct))
        {
            return direct.AddressFamily == AddressFamily.InterNetwork
                ? new List<IPAddress> { direct }
                : new List<IPAddress>();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DnsTimeout);

            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            return addresses
                .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
        }
        catch
        {
            return new List<IPAddress>();
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> GetTargetsAsync(
        string? manualHost,
        CancellationToken ct)
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            try
            {
                foreach (var item in nic.GetIPProperties().UnicastAddresses)
                {
                    if (item.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (item.IPv4Mask is null) continue;
                    targets.Add(GetBroadcastAddress(item.Address, item.IPv4Mask));
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(manualHost))
        {
            foreach (var address in await ResolveHostAsync(manualHost, ct))
                targets.Add(address);
        }

        return targets.ToList();
    }

    private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var subnet = mask.GetAddressBytes();
        var broadcast = new byte[4];

        for (var i = 0; i < 4; i++)
            broadcast[i] = (byte)(ip[i] | (subnet[i] ^ 255));

        return new IPAddress(broadcast);
    }
}
