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
    public const int GatewayHeaderLength = 24;

    private static readonly byte[] GatewayMagic = Encoding.ASCII.GetBytes("SPR1");

    public static byte[] DiscoveryRequestBytes => Encoding.ASCII.GetBytes(DiscoveryRequestMagic);

    public static byte[] SerializeAnnouncement(DiscoveryAnnouncement announcement) =>
        JsonSerializer.SerializeToUtf8Bytes(announcement, JsonStore.Options);

    public static DiscoveryAnnouncement? DeserializeAnnouncement(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var a = JsonSerializer.Deserialize<DiscoveryAnnouncement>(bytes, JsonStore.Options);
            return a?.Magic == DiscoveryResponseMagic && a.Version == Version ? a : null;
        }
        catch { return null; }
    }

    // Header: 4 bytes magic, 4 bytes version, 16 bytes printer Guid.
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
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
