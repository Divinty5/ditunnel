using System.Text.Json;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class XrayProfileConverterTests
{
    [Fact] public void HysteriaPreservesAuthAndSniWhilePinningUpstream()
    {
        var profile = new ImportedProfile("Test", "Hysteria 2", "hy2://a%3Ab@example.com:443?sni=cert.example.com#Test");
        var converted = XrayProfileConverter.Convert(profile);
        using var config = JsonDocument.Parse(converted.Build("192.0.2.1", true));
        var outbound = config.RootElement.GetProperty("outbounds")[0];
        Assert.Equal("example.com", converted.ServerHost);
        Assert.Equal("192.0.2.1", outbound.GetProperty("settings").GetProperty("address").GetString());
        Assert.Equal("a:b", outbound.GetProperty("streamSettings").GetProperty("hysteriaSettings").GetProperty("auth").GetString());
        Assert.Equal("cert.example.com", outbound.GetProperty("streamSettings").GetProperty("tlsSettings").GetProperty("serverName").GetString());
        Assert.Equal("tun", config.RootElement.GetProperty("inbounds")[0].GetProperty("protocol").GetString());
    }
    [Fact] public void SocksProbeOnlyListensOnLoopback()
    {
        var converted = XrayProfileConverter.Convert(new("Test", "HY2", "hy2://pass@example.com:443"));
        using var config = JsonDocument.Parse(converted.Build("192.0.2.1", false));
        Assert.Equal("127.0.0.1", config.RootElement.GetProperty("inbounds")[0].GetProperty("listen").GetString());
        Assert.Single(config.RootElement.GetProperty("outbounds").EnumerateArray());
    }
    [Fact] public void HysteriaPreservesTlsFingerprint()
    {
        var converted = XrayProfileConverter.Convert(new("Test", "Hysteria 2", "hy2://pass@example.com:443?alpn=h3&fp=chrome"));
        var tls = converted.Outbound["streamSettings"]!["tlsSettings"]!;
        Assert.Equal("chrome", tls["fingerprint"]!.GetValue<string>());
        Assert.Equal("h3", tls["alpn"]![0]!.GetValue<string>());
    }
    [Theory]
    [InlineData("hy2://pass@example.com:443?insecure=1")]
    [InlineData("hy2://pass@example.com:443?obfs=salamander")]
    [InlineData("vmess://e30=")]
    public void UnsupportedSettingsAreNotSilentlyDropped(string link) => Assert.Throws<NotSupportedException>(() => XrayProfileConverter.Convert(new("Test", "Test", link)));
    [Fact]
    public void VlessPreservesRealitySettings()
    {
        var link = "vless://00000000-0000-0000-0000-000000000001@example.com:443?security=reality&sni=cdn.example.com&fp=chrome&pbk=public-key&sid=0123456789abcdef&spx=%2F";
        var converted = XrayProfileConverter.Convert(new("Reality", "VLESS", link));
        var reality = converted.Outbound["streamSettings"]!["realitySettings"]!;
        Assert.Equal("cdn.example.com", reality["serverName"]!.GetValue<string>());
        Assert.Equal("public-key", reality["publicKey"]!.GetValue<string>());
        Assert.Equal("0123456789abcdef", reality["shortId"]!.GetValue<string>());
        Assert.Equal("/", reality["spiderX"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("ws", "wsSettings")]
    [InlineData("httpupgrade", "httpupgradeSettings")]
    public void VlessPreservesTransport(string transport, string property)
    {
        var converted = XrayProfileConverter.Convert(new("Test", "VLESS", $"vless://00000000-0000-0000-0000-000000000001@example.com:443?type={transport}&path=%2Fvpn&host=cdn.example.com"));
        using var config = JsonDocument.Parse(converted.Build("192.0.2.1", true));
        var stream = config.RootElement.GetProperty("outbounds")[0].GetProperty("streamSettings");
        Assert.Equal("/vpn", stream.GetProperty(property).GetProperty("path").GetString());
    }
    [Theory]
    [InlineData("hysteria", "192.0.2.10")]
    [InlineData("", "192.0.2.10")]
    [InlineData("cert.example.com", "cert.example.com")]
    public void Hy2UsesIpSanForPlaceholderButPreservesExplicitDnsName(string sni, string expected)
    {
        var converted = XrayProfileConverter.Convert(new("HY2", "Hysteria 2", $"hy2://pass@192.0.2.10:443?sni={sni}"));
        var tls = converted.Outbound["streamSettings"]!["tlsSettings"]!;
        Assert.Equal(expected, tls["serverName"]!.GetValue<string>());
        Assert.False(tls["allowInsecure"]!.GetValue<bool>());
        Assert.Equal(2, converted.Outbound["settings"]!["version"]!.GetValue<int>());
    }

    [Fact] public void SplitTunnelSeparatesSuffixDomainsFromLiteralAddresses()
    {
        var converted = XrayProfileConverter.Convert(new("Test", "Hysteria 2", "hy2://pass@example.com:443"));
        var policy = new SplitTunnelPolicy(SplitTunnelMode.BypassSelected, ["github.com", "192.0.2.40"], []);
        using var config = JsonDocument.Parse(converted.Build("192.0.2.1", true, splitTunnel: policy));
        var rules = config.RootElement.GetProperty("routing").GetProperty("rules");

        Assert.Contains(rules.EnumerateArray(), rule => rule.TryGetProperty("domain", out var domains) && domains[0].GetString() == "domain:github.com");
        Assert.Contains(rules.EnumerateArray(), rule => rule.TryGetProperty("ip", out var addresses) && addresses[0].GetString() == "192.0.2.40");
    }

    [Fact] public void ProxySelectedUsesDirectAsDefaultAndProxiesSelectedDomains()
    {
        var converted = XrayProfileConverter.Convert(new("Test", "VLESS", "vless://00000000-0000-0000-0000-000000000001@example.com:443"));
        var policy = new SplitTunnelPolicy(SplitTunnelMode.ProxySelected, ["ifconfig.me"], []);
        using var config = JsonDocument.Parse(converted.Build("192.0.2.1", true, splitTunnel: policy));
        var outbounds = config.RootElement.GetProperty("outbounds");
        var rules = config.RootElement.GetProperty("routing").GetProperty("rules");

        Assert.Equal("direct", outbounds[0].GetProperty("tag").GetString());
        Assert.Equal("proxy", outbounds[1].GetProperty("tag").GetString());
        Assert.Contains(rules.EnumerateArray(), rule =>
            rule.TryGetProperty("domain", out var domains)
            && domains[0].GetString() == "domain:ifconfig.me"
            && rule.GetProperty("outboundTag").GetString() == "proxy");
    }
}
