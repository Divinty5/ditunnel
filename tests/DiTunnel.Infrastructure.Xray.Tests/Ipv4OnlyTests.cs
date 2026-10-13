using System.Text.Json.Nodes;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class Ipv4OnlyTests
{
    private static XrayProfileConfiguration Profile() => XrayProfileConverter.Convert(
        new ImportedProfile("Test", "VLESS", "vless://11111111-1111-1111-1111-111111111111@192.0.2.1:443?security=tls&sni=example.test"));

    [Theory]
    [InlineData(SplitTunnelMode.ProxyAll)]
    [InlineData(SplitTunnelMode.ProxySelected)]
    [InlineData(SplitTunnelMode.BypassSelected)]
    public void Ipv4OnlyFiltersDnsAndBlocksIpv6BeforeSplitRules(SplitTunnelMode mode)
    {
        var profile = Profile().WithIpv4Only(true);
        Assert.False(profile.SupportsIpv6);
        var config = JsonNode.Parse(profile.Build("192.0.2.1", true, splitTunnel: new(mode, ["ru", "::1"], [])))!;
        Assert.Equal("UseIPv4", (string?)config["dns"]!["queryStrategy"]);
        var rules = config["routing"]!["rules"]!.AsArray();
        Assert.Equal("dns-upstream", (string?)rules[0]!["inboundTag"]![0]);
        Assert.Equal("proxy", (string?)rules[0]!["outboundTag"]);
        Assert.Equal("dns-ipv4", (string?)rules[1]!["outboundTag"]);
        Assert.Equal("::/0", (string?)rules[2]!["ip"]![0]);
        Assert.Equal("block", (string?)rules[2]!["outboundTag"]);
        Assert.Contains(config["outbounds"]!.AsArray(), value => (string?)value?["protocol"] == "dns");
    }

    [Fact]
    public void DisabledOptionKeepsDualStackAndLeavesDnsUnmodified()
    {
        var profile = Profile().WithIpv4Only(false);
        Assert.True(profile.SupportsIpv6);
        var config = JsonNode.Parse(profile.Build("192.0.2.1", true))!;
        Assert.Null(config["dns"]);
        Assert.Single(config["outbounds"]!.AsArray());
    }

    [Fact]
    public void DnsSelectionKeepsIpv4AndDoesNotAdvertiseIpv6Servers()
    {
        Assert.Equal(["1.1.1.1"], XrayProfileConfiguration.DnsServersFor(true, ["1.1.1.1", "2606:4700:4700::1111"]));
        Assert.Equal(["1.1.1.1", "1.0.0.1"], XrayProfileConfiguration.DnsServersFor(true, ["2001:db8::53"]));
        Assert.Equal(["2001:db8::53"], XrayProfileConfiguration.DnsServersFor(false, ["2001:db8::53"]));
    }

    [Fact]
    public void Ipv6OnlyProfileReportsHowToEnableIt()
    {
        var profile = Profile() with { SupportsIpv4 = false };
        Assert.Throws<NotSupportedException>(() => profile.WithIpv4Only(true));
        Assert.False(profile.WithIpv4Only(false).SupportsIpv4);
    }
}
