using System.Net;
using System.Net.Sockets;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class SplitAddressResolutionTests
{
    [Theory]
    [InlineData(SplitTunnelMode.BypassSelected)]
    [InlineData(SplitTunnelMode.ProxySelected)]
    public async Task ZonesDoNotRequireAddressRecords(SplitTunnelMode mode)
    {
        var addresses = await WindowsVpnEngine.ResolveSplitAddressesAsync(new(mode, ["ru", "рф", "org"], []), default,
            (_, _) => throw new InvalidOperationException("A suffix must not be resolved as a host"));
        Assert.Empty(addresses);
    }

    [Fact]
    public async Task MixedRulesResolveOnlyHostsAndKeepUniqueIpv4Addresses()
    {
        var names = new List<string>();
        var ip = IPAddress.Parse("203.0.113.20");
        var addresses = await WindowsVpnEngine.ResolveSplitAddressesAsync(new(SplitTunnelMode.BypassSelected,
            ["ru", "рф", "пример.рф", "203.0.113.20"], []), default, (name, _) =>
            {
                names.Add(name);
                return Task.FromResult(new[] { ip, IPAddress.IPv6Loopback });
            });
        Assert.Equal(["xn--e1afmkfd.xn--p1ai", "203.0.113.20"], names);
        Assert.Equal([ip], addresses);
    }

    [Fact]
    public async Task UnresolvableHostsStillReportAnError()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => WindowsVpnEngine.ResolveSplitAddressesAsync(
            new(SplitTunnelMode.BypassSelected, ["missing.invalid"], []), default,
            (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound))));
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsVpnEngine.ResolveSplitAddressesAsync(
            new(SplitTunnelMode.BypassSelected, ["example.org"], []), cancellation.Token,
            (_, token) => Task.FromCanceled<IPAddress[]>(token)));
    }
}
