using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SimplePrint.Common;

public static class Protocol
{
    public const int Version = 1;
    public const int DefaultDiscoveryPort = 45880;
    public const int DefaultGatewayPort = 45881;
    public const string DiscoveryRequestMagic = "SPRDISC1";
    public const string DiscoveryResponseMagic = "SPRANN1";
    public const string ClientHeartbeatMagic = "SPRCLT1";
    public const int GatewayHeaderLength = 24;

    private static readonly byte[] GatewayMagic = Encoding.ASCII.GetBytes("SPR1");

    public static byte[] DiscoveryRequestBytes => Encoding.ASCII.GetBytes(DiscoveryRequestMagic);

    public static byte[] SerializeAnnouncement(DiscoveryAnnouncement announcement) =>
        JsonSerializer.SerializeToUtf8Bytes(announcement, JsonStore.Options);

    public static DiscoveryAnnouncement? DeserializeAnnouncement(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var item = JsonSerializer.Deserialize<DiscoveryAnnouncement>(bytes, JsonStore.Options);
            return item?.Magic == DiscoveryResponseMagic && item.Version == Version ? item : null;
        }
        catch { return null; }
    }

    public static byte[] SerializeClientHeartbeat(ClientHeartbeat heartbeat) =>
        JsonSerializer.SerializeToUtf8Bytes(heartbeat, JsonStore.Options);

    public static ClientHeartbeat? DeserializeClientHeartbeat(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var item = JsonSerializer.Deserialize<ClientHeartbeat>(bytes, JsonStore.Options);
            return item?.Magic == ClientHeartbeatMagic && item.Version == Version ? item : null;
        }
        catch { return null; }
    }

    public static byte[] CreateGatewayHeader(Guid printerId)
    {
        var data = new byte[GatewayHeaderLength];
        GatewayMagic.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), Version);
        printerId.TryWriteBytes(data.AsSpan(8, 16));
        return data;
    }

    public static bool TryParseGatewayHeader(ReadOnlySpan<byte> data, out Guid printerId)
    {
        printerId = Guid.Empty;
        if (data.Length != GatewayHeaderLength) return false;
        if (!data[..4].SequenceEqual(GatewayMagic)) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4)) != Version) return false;
        printerId = new Guid(data.Slice(8, 16));
        return true;
    }

    public static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], ct);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }
}
