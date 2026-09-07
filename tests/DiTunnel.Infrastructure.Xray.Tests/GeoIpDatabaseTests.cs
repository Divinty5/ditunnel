using System.Net;
using System.Text;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class GeoIpDatabaseTests
{
    private static byte[] Field(byte tag, byte[] payload) => [tag, .. Varint((uint)payload.Length), .. payload];
    private static byte[] Varint(uint value)
    {
        var bytes = new List<byte>();
        do { var next = (byte)(value & 127); value >>= 7; bytes.Add((byte)(next | (value > 0 ? 128 : 0))); } while (value > 0);
        return bytes.ToArray();
    }
    private static byte[] Country(string code, string ip, byte prefix, bool reverse = false)
        => Field(10, [.. Field(18, [.. Field(10, IPAddress.Parse(ip).GetAddressBytes()), 16, prefix]), .. Field(10, Encoding.ASCII.GetBytes(code)), 24, (byte)(reverse ? 1 : 0)]);

    [Fact] public void MatchesBoundariesLongestPrefixAndBothAddressFamilies()
    {
        var db = GeoIpDatabase.Load([.. Country("SE", "192.0.2.0", 24), .. Country("US", "192.0.2.128", 25), .. Country("DE", "2001:db8::", 32)]);
        Assert.Equal("SE", db.FindCountry(IPAddress.Parse("192.0.2.0")));
        Assert.Equal("SE", db.FindCountry(IPAddress.Parse("192.0.2.127")));
        Assert.Equal("US", db.FindCountry(IPAddress.Parse("192.0.2.255")));
        Assert.Equal("US", db.FindCountry(IPAddress.Parse("::ffff:192.0.2.255")));
        Assert.Null(db.FindCountry(IPAddress.Parse("192.0.3.0")));
        Assert.Equal("DE", db.FindCountry(IPAddress.Parse("2001:db8:ffff::1")));
        Assert.Null(db.FindCountry(IPAddress.Parse("2001:db9::1")));
    }
    [Fact] public void SkipsNonCountryAndReverseRules()
    {
        var db = GeoIpDatabase.Load([.. Country("PRIVATE", "10.0.0.0", 8), .. Country("US", "192.0.2.0", 24, true)]);
        Assert.Null(db.FindCountry(IPAddress.Parse("10.1.2.3")));
        Assert.Null(db.FindCountry(IPAddress.Parse("192.0.2.1")));
    }
    [Fact] public void RejectsTruncatedAndInvalidFields()
    {
        Assert.Throws<FormatException>(() => GeoIpDatabase.Load([10, 100, 1]));
        Assert.Throws<FormatException>(() => GeoIpDatabase.Load([0xff, 0xff, 0xff, 0xff, 0xff]));
        Assert.Throws<FormatException>(() => GeoIpDatabase.Load(Country("SE", "192.0.2.0", 33)));
    }
}
