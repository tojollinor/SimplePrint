using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SimplePrint.Common;

public static class Protocol
{
    public const int Version = 4;
    public const int DefaultDiscoveryPort = 45880;
    public const int DefaultGatewayPort = 45881;
    public const int DefaultClientDiagnosticsPort = 45882;
    public const string DiscoveryRequestMagic = "SPRDISC1";
    public const string DiscoveryResponseMagic = "SPRANN1";
    public const string ClientHeartbeatMagic = "SPRCLT1";
    public const int GatewayHeaderLength = 40;

    private static readonly byte[] GatewayMagic = Encoding.ASCII.GetBytes("SPR4");
    private static readonly byte[] DiagnosticsRequestMagic = Encoding.ASCII.GetBytes("SPD1");
    private static readonly byte[] DiagnosticsArchiveMagic = Encoding.ASCII.GetBytes("SPDZ");
    private static readonly byte[] DiagnosticsErrorMagic = Encoding.ASCII.GetBytes("SPDE");
    public const int DiagnosticsRequestLength = 24;
    private const int DiagnosticsResponseHeaderLength = 8;
    private const int DiagnosticsMaxPayload = 128 * 1024 * 1024;

    public static byte[] DiscoveryRequestBytes => Encoding.ASCII.GetBytes(DiscoveryRequestMagic);

    public static byte[] SerializeAnnouncement(DiscoveryAnnouncement announcement) =>
        JsonSerializer.SerializeToUtf8Bytes(announcement, JsonStore.Options);

    public static DiscoveryAnnouncement? DeserializeAnnouncement(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var item = JsonSerializer.Deserialize<DiscoveryAnnouncement>(bytes, JsonStore.Options);
            return item?.Magic == DiscoveryResponseMagic ? item : null;
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
            return item?.Magic == ClientHeartbeatMagic ? item : null;
        }
        catch { return null; }
    }

    public static byte[] CreateGatewayHeader(Guid printerId, Guid jobId = default)
    {
        var data = new byte[GatewayHeaderLength];
        GatewayMagic.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), Version);
        printerId.TryWriteBytes(data.AsSpan(8, 16));
        jobId.TryWriteBytes(data.AsSpan(24, 16));
        return data;
    }

    public static bool TryParseGatewayHeader(ReadOnlySpan<byte> data, out Guid printerId, out Guid jobId)
    {
        printerId = Guid.Empty;
        jobId = Guid.Empty;
        if (data.Length != GatewayHeaderLength) return false;
        if (!data[..4].SequenceEqual(GatewayMagic)) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4)) != Version) return false;
        printerId = new Guid(data.Slice(8, 16));
        jobId = new Guid(data.Slice(24, 16));
        return true;
    }

    public static byte[] CreateDiagnosticsRequest(Guid serverId)
    {
        var data = new byte[DiagnosticsRequestLength];
        DiagnosticsRequestMagic.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), Version);
        serverId.TryWriteBytes(data.AsSpan(8, 16));
        return data;
    }

    public static bool TryParseDiagnosticsRequest(ReadOnlySpan<byte> data, out Guid serverId)
    {
        serverId = Guid.Empty;
        if (data.Length != DiagnosticsRequestLength) return false;
        if (!data[..4].SequenceEqual(DiagnosticsRequestMagic)) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4)) != Version) return false;
        serverId = new Guid(data.Slice(8, 16));
        return serverId != Guid.Empty;
    }

    public static async Task WriteDiagnosticsArchiveAsync(
        Stream stream,
        byte[] archive,
        CancellationToken ct)
    {
        if (archive.Length <= 0 || archive.Length > DiagnosticsMaxPayload)
            throw new InvalidDataException("Ungültige Größe des Diagnosepakets.");

        var header = new byte[DiagnosticsResponseHeaderLength];
        DiagnosticsArchiveMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), archive.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(archive, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteDiagnosticsErrorAsync(
        Stream stream,
        string error,
        CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(error) ? "Diagnose konnte nicht erstellt werden." : error);

        if (payload.Length > 1024 * 1024)
            payload = payload[..(1024 * 1024)];

        var header = new byte[DiagnosticsResponseHeaderLength];
        DiagnosticsErrorMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<(byte[]? Archive, string? Error)> ReadDiagnosticsResponseAsync(
        Stream stream,
        CancellationToken ct)
    {
        var header = new byte[DiagnosticsResponseHeaderLength];
        if (!await ReadExactAsync(stream, header, ct))
            return (null, "Client hat keine Diagnoseantwort geliefert.");

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length <= 0 || length > DiagnosticsMaxPayload)
            return (null, "Client meldet eine ungültige Diagnosepaketgröße.");

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct))
            return (null, "Diagnosepaket wurde unvollständig übertragen.");

        if (header.AsSpan(0, 4).SequenceEqual(DiagnosticsArchiveMagic))
            return (payload, null);

        if (header.AsSpan(0, 4).SequenceEqual(DiagnosticsErrorMagic))
            return (null, Encoding.UTF8.GetString(payload));

        return (null, "Client hat ein unbekanntes Diagnoseformat geliefert.");
    }

    public static async Task WriteJobAckAsync(Stream stream, PrintJobAck ack, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(ack, JsonStore.Options);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<PrintJobAck?> ReadJobAckAsync(Stream stream, CancellationToken ct)
    {
        var length = new byte[4];
        if (!await ReadExactAsync(stream, length, ct)) return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size <= 0 || size > 1024 * 1024) return null;

        var payload = new byte[size];
        if (!await ReadExactAsync(stream, payload, ct)) return null;

        try
        {
            return JsonSerializer.Deserialize<PrintJobAck>(payload, JsonStore.Options);
        }
        catch
        {
            return null;
        }
    }

    public static async Task WritePrinterHealthAsync(Stream stream, PrinterHealthStatus status, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(status, JsonStore.Options);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<PrinterHealthStatus?> ReadPrinterHealthAsync(Stream stream, CancellationToken ct)
    {
        var length = new byte[4];
        if (!await ReadExactAsync(stream, length, ct)) return null;

        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size <= 0 || size > 1024 * 1024) return null;

        var payload = new byte[size];
        if (!await ReadExactAsync(stream, payload, ct)) return null;

        try
        {
            return JsonSerializer.Deserialize<PrinterHealthStatus>(payload, JsonStore.Options);
        }
        catch
        {
            return null;
        }
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