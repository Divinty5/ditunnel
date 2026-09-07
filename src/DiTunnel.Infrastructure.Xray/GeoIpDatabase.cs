using System.Net;
using System.Text;

namespace DiTunnel.Infrastructure.Xray;

/// <summary>Reads country CIDRs from Xray's local GeoIPList protobuf database.</summary>
public sealed class GeoIpDatabase
{
    private readonly Dictionary<UInt128, string>?[] ipv4 = new Dictionary<UInt128, string>?[33];
    private readonly Dictionary<UInt128, string>?[] ipv6 = new Dictionary<UInt128, string>?[129];

    public static GeoIpDatabase Load(byte[] bytes)
    {
        if (bytes.Length > 64 * 1024 * 1024) throw new FormatException("GeoIP database is too large.");
        var database = new GeoIpDatabase();
        var list = new Reader(bytes);
        while (list.More)
        {
            var tag = list.Varint();
            if (tag == 10) database.ReadCountry(list.Bytes()); else list.Skip(tag);
        }
        return database;
    }

    private void ReadCountry(ReadOnlySpan<byte> message)
    {
        var reader = new Reader(message);
        string? code = null;
        var reverse = false;
        // Read metadata before CIDRs: protobuf field order is not significant.
        while (reader.More)
        {
            var tag = reader.Varint();
            if (tag == 10) code = Encoding.ASCII.GetString(reader.Bytes()).ToUpperInvariant();
            else if (tag == 24) reverse = reader.Varint() != 0;
            else reader.Skip(tag);
        }
        if (reverse || code is not { Length: 2 } || !code.All(c => c is >= 'A' and <= 'Z') || code is "EU" or "AP" or "ZZ") return;
        reader = new Reader(message);
        while (reader.More)
        {
            var tag = reader.Varint();
            if (tag != 18) { reader.Skip(tag); continue; }
            var cidr = new Reader(reader.Bytes());
            ReadOnlySpan<byte> address = default;
            uint prefix = 0;
            while (cidr.More)
            {
                var field = cidr.Varint();
                if (field == 10) address = cidr.Bytes();
                else if (field == 16) prefix = cidr.Varint();
                else cidr.Skip(field);
            }
            if (address.Length is not (4 or 16) || prefix > address.Length * 8) throw new FormatException("Invalid GeoIP CIDR.");
            var maps = address.Length == 4 ? ipv4 : ipv6;
            (maps[prefix] ??= new())[Network(Number(address), (int)prefix, address.Length * 8)] = code;
        }
    }

    public string? FindCountry(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        var maps = bytes.Length == 4 ? ipv4 : ipv6;
        var number = Number(bytes);
        for (var prefix = maps.Length - 1; prefix >= 0; prefix--)
            if (maps[prefix]?.TryGetValue(Network(number, prefix, bytes.Length * 8), out var code) == true) return code;
        return null;
    }

    private static UInt128 Number(ReadOnlySpan<byte> bytes)
    {
        UInt128 number = 0;
        foreach (var b in bytes) number = (number << 8) | b;
        return number;
    }
    private static UInt128 Network(UInt128 number, int prefix, int bits) => prefix == 0 ? 0 : number >> (bits - prefix) << (bits - prefix);

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> remaining = data;
        public bool More => !remaining.IsEmpty;
        public uint Varint()
        {
            uint value = 0;
            for (var shift = 0; shift <= 28; shift += 7)
            {
                if (remaining.IsEmpty) throw new FormatException("Truncated GeoIP data.");
                var b = remaining[0]; remaining = remaining[1..];
                if (shift == 28 && b > 15) throw new FormatException("Invalid GeoIP integer.");
                value |= (uint)(b & 127) << shift;
                if (b < 128) return value;
            }
            throw new FormatException("Invalid GeoIP integer.");
        }
        public ReadOnlySpan<byte> Bytes()
        {
            var length = Varint();
            if (length > remaining.Length) throw new FormatException("Truncated GeoIP field.");
            var result = remaining[..(int)length]; remaining = remaining[(int)length..]; return result;
        }
        public void Skip(uint tag)
        {
            switch (tag & 7)
            {
                case 0: Varint(); break;
                case 2: Bytes(); break;
                case 1: Fixed(8); break;
                case 5: Fixed(4); break;
                default: throw new FormatException("Unsupported GeoIP field.");
            }
        }
        private void Fixed(int length)
        {
            if (remaining.Length < length) throw new FormatException("Truncated GeoIP field.");
            remaining = remaining[length..];
        }
    }
}
