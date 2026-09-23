using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SimplePrint.Common;

public static class Discovery
{
    public static async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        int port = Protocol.DefaultDiscoveryPort,
        int waitMs = 900,
        CancellationToken ct = default,
        string? manualHost = null)
    {
        var found = new Dictionary<Guid, DiscoveredServer>();
        var targets = await GetTargetsAsync(manualHost);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };

        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(Protocol.DiscoveryRequestBytes, new IPEndPoint(target, port), ct);
            }
            catch (SocketException) { }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(waitMs);

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
            catch (SocketException) { break; }
        }

        return found.Values.OrderBy(x => x.Announcement.ServerName).ToList();
    }

    public static async Task SendClientHeartbeatAsync(
        ClientHeartbeat heartbeat,
        int port,
        string? manualHost,
        CancellationToken ct = default)
    {
        var targets = await GetTargetsAsync(manualHost);
        var bytes = Protocol.SerializeClientHeartbeat(heartbeat);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        foreach (var target in targets)
        {
            try
            {
                await udp.SendAsync(bytes, new IPEndPoint(target, port), ct);
            }
            catch (SocketException) { }
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> GetTargetsAsync(string? manualHost)
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
            var host = manualHost.Trim();
            if (IPAddress.TryParse(host, out var direct) && direct.AddressFamily == AddressFamily.InterNetwork)
            {
                targets.Add(direct);
            }
            else
            {
                try
                {
                    foreach (var address in await Dns.GetHostAddressesAsync(host))
                    {
                        if (address.AddressFamily == AddressFamily.InterNetwork)
                            targets.Add(address);
                    }
                }
                catch { }
            }
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
