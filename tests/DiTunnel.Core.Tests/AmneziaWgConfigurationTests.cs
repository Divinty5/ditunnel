using System.Text;
using System.Text.Json;

using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Tests;

public sealed class AmneziaWgConfigurationTests
{
    // Synthetic keys, documentation addresses and reserved example domains only.
    private static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static string Configuration => $"""
        [Interface]
        PrivateKey = {Key}
        Address = 192.0.2.2/32, 2001:db8::2/128
        DNS = 192.0.2.53
        MTU = 1280
        Jc = 4
        Jmin = 40
        Jmax = 70
        S1 = 15
        S2 = 20
        H1 = 12345
        H2 = 23456
        H3 = 34567
        H4 = 45678
        [Peer]
        PublicKey = {Key}
        PresharedKey = {Key}
        Endpoint = vpn.example.com:51820
        AllowedIPs = 0.0.0.0/0, ::/0
        PersistentKeepalive = 25
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportsRawAndBase64ConfigurationWithoutLosingParameters(bool base64)
    {
        var content = Configuration;
        var input = base64 ? Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) : content;
        var profile = Assert.Single(ProfileParser.Parse(input, out var skipped));
        Assert.Equal("AmneziaWG", profile.Kind);
        Assert.Equal("AmneziaWG 1.0 · конфигурация сервера", profile.Summary);
        Assert.Equal("AmneziaWG · vpn.example.com", profile.Name);
        Assert.Equal(content, profile.Content);
        Assert.Equal(0, skipped);
        var config = AmneziaWgConfiguration.Parse(profile.Content);
        Assert.Equal("vpn.example.com", config.EndpointHost);
        Assert.Equal(51820, config.EndpointPort);
        Assert.Equal("40", config.Interface["Jmin"]);
        Assert.Equal("0.0.0.0/0, ::/0", config.Peer["AllowedIPs"]);
    }

    [Fact]
    public void AcceptsBomCommentsCaseInsensitiveFieldsAndIpv6Endpoint()
    {
        var content = "\uFEFF# comment\r\n; comment\r\n" + Configuration.Replace("[Interface]", "[interface]")
            .Replace("PrivateKey", "privatekey").Replace("vpn.example.com:51820", "[2001:db8::1]:443 # comment");
        var config = AmneziaWgConfiguration.Parse(Assert.Single(ProfileParser.Parse(content)).Content);
        Assert.Equal("2001:db8::1", config.EndpointHost);
        Assert.Equal(443, config.EndpointPort);
    }

    [Theory]
    [InlineData("", "1.0")]
    [InlineData("I1 = <r 10>", "1.5")]
    [InlineData("S3 = 12\nS4 = 12", "2.0")]
    [InlineData("ContentPaddingAddition = 10-20", "3.0")]
    [InlineData("RandomTrailers = true", "3.1")]
    [InlineData("DisableCookies = false", "3.1")]
    public void DisplaysProtocolVersionWithoutChangingStoredKind(string extra, string version)
    {
        var profile = Assert.Single(ProfileParser.Parse(Configuration.Replace("[Peer]", extra + "\n[Peer]")));
        Assert.Equal("AmneziaWG", profile.Kind);
        Assert.Equal("AmneziaWG " + version, profile.ProtocolName);
        Assert.StartsWith("AmneziaWG " + version, profile.Summary);
    }

    [Fact]
    public void PreservesModernObfuscationParameters()
    {
        var content = Configuration.Replace("H1 = 12345", $"""
            H1 = 12345-12355
            S3 = 12
            S4 = 12
            I1 = <b 0x01020304><r 10>
            HeaderProtectionKey = {Key}
            ContentPaddingAddition = 10-20
            RekeyAfterTime = 120-180
            RandomTrailers = true
            """) .Replace("PersistentKeepalive = 25", "PersistentKeepalive = 22-30");
        var config = AmneziaWgConfiguration.Parse(content);
        Assert.Equal("12345-12355", config.Interface["H1"]);
        Assert.Equal("<b 0x01020304><r 10>", config.Interface["I1"]);
        Assert.Equal(Key, config.Interface["HeaderProtectionKey"]);
        Assert.Equal("22-30", config.Peer["PersistentKeepalive"]);
    }

    [Theory]
    [InlineData("PrivateKey", "bad")]
    [InlineData("PublicKey", "bad")]
    [InlineData("Address", "192.0.2.2/33")]
    [InlineData("Address", "2001:db8::2/129")]
    [InlineData("Address", "192.0.2.2/32,")]
    [InlineData("DNS", "https://example.com")]
    [InlineData("MTU", "0")]
    [InlineData("Jc", "-1")]
    [InlineData("Jmin", "100")]
    [InlineData("S1", "65536")]
    [InlineData("H1", "4294967296")]
    [InlineData("H1", "20-10")]
    [InlineData("PersistentKeepalive", "-1")]
    [InlineData("Endpoint", "vpn.example.com")]
    [InlineData("Endpoint", "vpn.example.com:0")]
    [InlineData("Endpoint", "vpn.example.com:65536")]
    [InlineData("Endpoint", "2001:db8::1:443")]
    [InlineData("Endpoint", "[vpn.example.com]:443")]
    [InlineData("AllowedIPs", "example.com/32")]
    public void RejectsMalformedFieldsWithoutEchoingTheirValues(string key, string value)
    {
        var content = string.Join('\n', Configuration.Split('\n').Select(line => line.StartsWith(key + " = ") ? key + " = " + value : line));
        var error = Assert.Throws<FormatException>(() => ProfileParser.Parse(content));
        Assert.DoesNotContain(Key, error.Message);
    }

    [Theory]
    [InlineData("PostUp = echo secret")]
    [InlineData("PreDown = echo secret")]
    [InlineData("Unknown = secret")]
    [InlineData("Jc = 5")]
    [InlineData("privatekey = secret")]
    [InlineData("[Interface]")]
    public void RejectsHooksUnknownFieldsAndDuplicates(string line)
        => Assert.Throws<FormatException>(() => ProfileParser.Parse(Configuration.Replace("[Peer]", line + "\n[Peer]")));

    [Fact]
    public void RejectsMultiplePeersAndMissingRequiredFields()
    {
        Assert.Throws<FormatException>(() => ProfileParser.Parse(Configuration + "\n[Peer]\nPublicKey = " + Key));
        Assert.Throws<FormatException>(() => ProfileParser.Parse(Configuration.Replace("PublicKey = " + Key, "")));
        Assert.Throws<FormatException>(() => ProfileParser.Parse(Configuration.Replace("[Peer]", "[Other]")));
    }

    [Fact]
    public void DoesNotMislabelPlainWireGuardAsAmneziaWg()
    {
        var content = string.Join('\n', Configuration.Split('\n').Where(line => !line.StartsWith('J') && !line.StartsWith('S') && !line.StartsWith('H')));
        Assert.Throws<FormatException>(() => ProfileParser.Parse(content));
    }

    [Fact]
    public void ProfileRoundTripsThroughExistingStorageJson()
    {
        var profile = Assert.Single(ProfileParser.Parse(Configuration));
        var json = JsonSerializer.Serialize(profile, DiTunnelJsonContext.Default.ImportedProfile);
        var restored = JsonSerializer.Deserialize(json, DiTunnelJsonContext.Default.ImportedProfile);
        Assert.Equal(profile, restored);
        Assert.DoesNotContain(Key, AmneziaWgConfiguration.Parse(Configuration).ToString());
    }
}
