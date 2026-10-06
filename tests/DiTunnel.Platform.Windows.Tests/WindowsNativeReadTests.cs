using System.Net;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class WindowsNativeReadTests
{
    [Theory]
    [InlineData("192.0.2.19")]
    [InlineData("2001:db8:abcd::19")]
    public void SocketAddressRoundTripsBothFamilies(string text)
    {
        var address = IPAddress.Parse(text);
        Assert.Equal(address, WindowsNetworkApi.Address(WindowsNetworkApi.SocketAddress(address)));
    }

    [Fact]
    public void NativeRouteTableCanBeReadWithoutChangingRoutes()
    {
        var routes = WindowsNetworkApi.Routes();
        Assert.NotEmpty(routes);
        Assert.All(routes, r => Assert.True(r.InterfaceIndex > 0));
        Assert.True(WindowsNetworkApi.BestInterface(IPAddress.Loopback) > 0);
    }

    [Fact]
    public void DnsProviderCanBeReadWithoutChangingPolicies()
    {
        // Explicit opt-in for read-only WMI access outside the restricted test sandbox.
        if (Environment.GetEnvironmentVariable("DITUNNEL_TEST_DNS_PROVIDER") != "1") return;
        var output = WindowsDnsPolicy.Invoke("PS_DnsClientNrptRule", "Get");
        Assert.NotNull(output.Items);
        var effective = WindowsDnsPolicy.Invoke("PS_DnsClientNrptPolicy", "Get",
            new Dictionary<string, object> { ["Effective"] = true });
        Assert.NotNull(effective.Items);
        _ = WindowsDnsPolicy.HasConflictingPolicy();
        _ = WindowsDnsPolicy.CachedNames();
    }

    [Fact]
    public void PhysicalUplinkCanBeReadWithoutChangingRoutes()
    {
        if (Environment.GetEnvironmentVariable("DITUNNEL_TEST_DNS_PROVIDER") != "1") return;
        var uplink = WindowsNetworkApi.FindUplink();
        Assert.True(uplink.InterfaceIndex > 0);
        Assert.True(IPAddress.TryParse(uplink.SourceAddress, out _));
    }
}
