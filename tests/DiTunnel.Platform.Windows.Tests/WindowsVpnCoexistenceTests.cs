using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class WindowsVpnCoexistenceTests
{
    [Fact]
    public void DetectsOpenVpnButDoesNotMistakeOwnTunOrHyperVForCorporateVpn()
    {
        Assert.True(WindowsVpnCoexistence.IsOtherVpn("Corporate", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, true, "DiTunnel"));
        Assert.False(WindowsVpnCoexistence.IsOtherVpn("DiTunnel-123", "Wintun", NetworkInterfaceType.Tunnel, true, "DiTunnel"));
        Assert.False(WindowsVpnCoexistence.IsOtherVpn("vEthernet", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, true, "DiTunnel"));
        Assert.False(WindowsVpnCoexistence.IsOtherVpn("Corporate", "TAP-Windows", NetworkInterfaceType.Ethernet, false, "DiTunnel"));
    }
    [Theory]
    [InlineData("10.0.0.0/8", "10.2.3.4", true)] [InlineData("10.0.0.0/8", "203.0.113.1", false)]
    [InlineData("0.0.0.0/0", "203.0.113.1", false)] [InlineData("128.0.0.0/1", "203.0.113.1", false)]
    [InlineData("fd00::/64", "fd00::123", true)]
    public void PreservesSpecificCorporatePrefixesWithoutBypassingInternet(string prefix, string address, bool expected) =>
        Assert.Equal(expected, WindowsVpnCoexistence.CoversSpecificRoute(prefix, IPAddress.Parse(address)));
    [Fact]
    public void OpenVpnTransportBypassIsScopedToProcessAndDisabledUnderKillSwitch()
    {
        var root = JsonNode.Parse("""{"outbounds":[],"routing":{"rules":[]}}""")!.AsObject();
        WindowsVpnCoexistence.ConfigureOpenVpnBypass(root, "192.0.2.4", false);
        Assert.Empty(root["outbounds"]!.AsArray());
        WindowsVpnCoexistence.ConfigureOpenVpnBypass(root, "192.0.2.4", true, "Synthetic Ethernet");
        var rule = Assert.Single(root["routing"]!["rules"]!.AsArray())!;
        Assert.NotNull(rule["process"]);
        Assert.Null(rule["port"]);
        Assert.Null(rule["ip"]);
        Assert.Equal("192.0.2.4", root["outbounds"]![0]!["sendThrough"]!.GetValue<string>());
        Assert.Equal("Synthetic Ethernet", root["outbounds"]![0]!["streamSettings"]!["sockopt"]!["interface"]!.GetValue<string>());
    }
}
