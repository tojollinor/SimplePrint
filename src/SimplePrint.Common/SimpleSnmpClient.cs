using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimplePrint.Common;

public sealed class SnmpValue
{
    public string Oid { get; init; } = "";
    public byte Type { get; init; }
    public byte[] Raw { get; init; } = [];

    public long? AsInteger()
    {
        if (Raw.Length == 0) return null;

        if (Type is 0x02)
        {
            long value = (Raw[0] & 0x80) != 0 ? -1 : 0;
            foreach (var b in Raw)
                value = (value << 8) | b;
            return value;
        }

        if (Type is 0x41 or 0x42 or 0x43 or 0x46)
        {
            long value = 0;
            foreach (var b in Raw)
                value = (value << 8) | b;
            return value;
        }

        return null;
    }

    public string AsText()
    {
        if (Type == 0x04)
        {
            try { return Encoding.UTF8.GetString(Raw).Trim('\0', ' ', '\r', '\n'); }
            catch { return Encoding.ASCII.GetString(Raw).Trim('\0', ' ', '\r', '\n'); }
        }

        return AsInteger()?.ToString() ?? "";
    }
}

public static class SimpleSnmpClient
{
    private const int SnmpPort = 161;

    public static async Task<SnmpValue?> GetAsync(
        string host,
        string community,
        string oid,
        int timeoutMs = 800,
        CancellationToken ct = default)
        => await RequestAsync(host, community, oid, getNext: false, timeoutMs, ct);

    public static async Task<IReadOnlyList<SnmpValue>> WalkAsync(
        string host,
        string community,
        string subtree,
        int maxItems = 48,
        int timeoutMs = 800,
        CancellationToken ct = default)
    {
        var result = new List<SnmpValue>();
        var current = subtree.TrimEnd('.');

        for (var i = 0; i < maxItems; i++)
        {
            var next = await RequestAsync(host, community, current, getNext: true, timeoutMs, ct);
            if (next is null) break;

            if (!(next.Oid.Equals(subtree, StringComparison.Ordinal) ||
                  next.Oid.StartsWith(subtree.TrimEnd('.') + ".", StringComparison.Ordinal)))
                break;

            if (next.Oid.Equals(current, StringComparison.Ordinal))
                break;

            result.Add(next);
            current = next.Oid;
        }

        return result;
    }

    private static async Task<SnmpValue?> RequestAsync(
        string host,
        string community,
        string oid,
        bool getNext,
        int timeoutMs,
        CancellationToken ct)
    {
        IPAddress address;
        if (!IPAddress.TryParse(host, out address!))
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new InvalidOperationException($"Für '{host}' wurde keine IPv4-Adresse gefunden.");
        }

        var packet = BuildRequest(community, oid, getNext);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await udp.SendAsync(packet, new IPEndPoint(address, SnmpPort), timeout.Token);
            var response = await udp.ReceiveAsync(timeout.Token);
            return ParseResponse(response.Buffer);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static byte[] BuildRequest(string community, string oid, bool getNext)
    {
        var requestId = Random.Shared.Next(1, int.MaxValue);

        var variableBinding = Tlv(
            0x30,
            Concat(
                Tlv(0x06, EncodeOid(oid)),
                Tlv(0x05, [])));

        var variableList = Tlv(0x30, variableBinding);

        var pdu = Tlv(
            getNext ? (byte)0xA1 : (byte)0xA0,
            Concat(
                Tlv(0x02, EncodeInteger(requestId)),
                Tlv(0x02, [0]),
                Tlv(0x02, [0]),
                variableList));

        return Tlv(
            0x30,
            Concat(
                Tlv(0x02, [0]), // SNMP v1
                Tlv(0x04, Encoding.ASCII.GetBytes(string.IsNullOrWhiteSpace(community) ? "public" : community)),
                pdu));
    }

    private static SnmpValue? ParseResponse(byte[] data)
    {
        try
        {
            var index = 0;

            var message = ReadTlv(data, ref index);
            if (message.Tag != 0x30) return null;

            var inner = message.Start;
            _ = ReadTlv(data, ref inner); // version
            _ = ReadTlv(data, ref inner); // community

            var pdu = ReadTlv(data, ref inner);
            if (pdu.Tag != 0xA2) return null;

            var p = pdu.Start;
            _ = ReadTlv(data, ref p); // request id
            var errorStatus = ReadTlv(data, ref p);
            var errorIndex = ReadTlv(data, ref p);

            if ((DecodeInteger(data.AsSpan(errorStatus.Start, errorStatus.Length)) ?? 0) != 0 ||
                (DecodeInteger(data.AsSpan(errorIndex.Start, errorIndex.Length)) ?? 0) < 0)
                return null;

            var list = ReadTlv(data, ref p);
            if (list.Tag != 0x30) return null;

            var l = list.Start;
            var binding = ReadTlv(data, ref l);
            if (binding.Tag != 0x30) return null;

            var b = binding.Start;
            var oidTlv = ReadTlv(data, ref b);
            var valueTlv = ReadTlv(data, ref b);

            if (oidTlv.Tag != 0x06) return null;
            if (valueTlv.Tag is 0x80 or 0x81 or 0x82) return null;

            return new SnmpValue
            {
                Oid = DecodeOid(data.AsSpan(oidTlv.Start, oidTlv.Length)),
                Type = valueTlv.Tag,
                Raw = data.AsSpan(valueTlv.Start, valueTlv.Length).ToArray()
            };
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct TlvInfo(byte Tag, int Start, int Length);

    private static TlvInfo ReadTlv(byte[] data, ref int index)
    {
        if (index >= data.Length) throw new InvalidDataException("BER: Tag fehlt.");

        var tag = data[index++];
        var length = ReadLength(data, ref index);

        if (length < 0 || index + length > data.Length)
            throw new InvalidDataException("BER: ungültige Länge.");

        var result = new TlvInfo(tag, index, length);
        index += length;
        return result;
    }

    private static int ReadLength(byte[] data, ref int index)
    {
        var first = data[index++];
        if ((first & 0x80) == 0) return first;

        var count = first & 0x7F;
        if (count is 0 or > 4) throw new InvalidDataException("BER: Länge nicht unterstützt.");

        var length = 0;
        for (var i = 0; i < count; i++)
            length = (length << 8) | data[index++];

        return length;
    }

    private static byte[] Tlv(byte tag, byte[] value)
        => Concat([tag], EncodeLength(value.Length), value);

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80) return [(byte)length];

        Span<byte> tmp = stackalloc byte[4];
        var count = 0;
        var value = length;

        while (value > 0)
        {
            tmp[3 - count] = (byte)(value & 0xFF);
            value >>= 8;
            count++;
        }

        var result = new byte[count + 1];
        result[0] = (byte)(0x80 | count);
        tmp[(4 - count)..].CopyTo(result.AsSpan(1));
        return result;
    }

    private static byte[] EncodeInteger(int value)
    {
        Span<byte> raw = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(raw, value);

        var start = 0;
        while (start < 3 &&
               raw[start] == 0 &&
               (raw[start + 1] & 0x80) == 0)
            start++;

        return raw[start..].ToArray();
    }

    private static long? DecodeInteger(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0 || raw.Length > 8) return null;

        long value = (raw[0] & 0x80) != 0 ? -1 : 0;
        foreach (var b in raw)
            value = (value << 8) | b;

        return value;
    }

    private static byte[] EncodeOid(string oid)
    {
        var parts = oid.Trim('.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(uint.Parse)
            .ToArray();

        if (parts.Length < 2) throw new ArgumentException("Ungültige OID.", nameof(oid));

        var bytes = new List<byte>();
        bytes.AddRange(EncodeOidPart(parts[0] * 40 + parts[1]));

        for (var i = 2; i < parts.Length; i++)
            bytes.AddRange(EncodeOidPart(parts[i]));

        return bytes.ToArray();
    }

    private static IEnumerable<byte> EncodeOidPart(uint value)
    {
        Span<byte> tmp = stackalloc byte[5];
        var pos = 5;

        tmp[--pos] = (byte)(value & 0x7F);
        value >>= 7;

        while (value > 0)
        {
            tmp[--pos] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        return tmp[pos..].ToArray();
    }

    private static string DecodeOid(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "";

        var values = new List<uint>();
        uint current = 0;

        foreach (var b in data)
        {
            current = (current << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                values.Add(current);
                current = 0;
            }
        }

        if (values.Count == 0) return "";

        var firstCombined = values[0];
        uint first;
        uint second;

        if (firstCombined < 40)
        {
            first = 0;
            second = firstCombined;
        }
        else if (firstCombined < 80)
        {
            first = 1;
            second = firstCombined - 40;
        }
        else
        {
            first = 2;
            second = firstCombined - 80;
        }

        var parts = new List<uint> { first, second };
        parts.AddRange(values.Skip(1));

        return "." + string.Join(".", parts);
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = arrays.Sum(x => x.Length);
        var result = new byte[total];
        var offset = 0;

        foreach (var array in arrays)
        {
            Buffer.BlockCopy(array, 0, result, offset, array.Length);
            offset += array.Length;
        }

        return result;
    }
}
