using System.Net;
using System.Net.Sockets;

namespace SimplePrint.Common;

public static class Discovery
{
    public static async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(int port = Protocol.DefaultDiscoveryPort, int waitMs = 900, CancellationToken ct = default)
    {
        var found = new Dictionary<Guid, DiscoveredServer>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        await udp.SendAsync(Protocol.DiscoveryRequestBytes, new IPEndPoint(IPAddress.Broadcast, port), ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(waitMs);
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(deadline.Token);
                var announcement = Protocol.DeserializeAnnouncement(result.Buffer);
                if (announcement is null) continue;
                found[announcement.ServerId] = new DiscoveredServer(announcement, result.RemoteEndPoint.Address, DateTimeOffset.Now);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
        }
        return found.Values.OrderBy(x => x.Announcement.ServerName).ToList();
    }
}
