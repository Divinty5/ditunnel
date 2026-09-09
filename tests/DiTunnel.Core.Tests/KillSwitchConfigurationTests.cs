using System.Net;
using DiTunnel.Core.Connection;

namespace DiTunnel.Core.Tests;

public sealed class KillSwitchConfigurationTests
{
    [Fact]
    public void RequiresANonLoopbackResolvedServerAddress()
    {
        Assert.Throws<ArgumentException>(() => KillSwitchConfiguration.Create([IPAddress.Loopback], false));
    }

    [Fact]
    public void KeepsBothIpFamiliesAndLanChoice()
    {
        var result = KillSwitchConfiguration.Create([IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::20"), IPAddress.Parse("198.51.100.20")], true);
        Assert.Equal(2, result.ServerAddresses.Count);
        Assert.True(result.AllowLocalNetwork);
    }

    [Fact]
    public void KeepsTheExactVpnTransportEndpoint()
    {
        var result = KillSwitchConfiguration.Create([IPAddress.Parse("198.51.100.20")], 443, KillSwitchTransportProtocol.Tcp, false);
        Assert.Equal((ushort)443, result.ServerPort);
        Assert.Equal(KillSwitchTransportProtocol.Tcp, result.ServerTransport);
    }

    [Fact]
    public void KeepsDistinctExplicitDirectDestinations()
    {
        var direct = IPAddress.Parse("203.0.113.9");
        var result = KillSwitchConfiguration.Create([IPAddress.Parse("198.51.100.20")], 443, KillSwitchTransportProtocol.Tcp, false, [direct, direct]);
        Assert.Equal([direct], result.DirectAddresses);
    }
}
