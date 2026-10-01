using System.Net;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class EndpointRouteLeaseTests
{
    [Fact]
    public async Task ProbeAndTunnelKeepRouteUntilLastOwnerDisconnects()
    {
        var removed = new List<NetworkRoute>();
        var leases = new EndpointRouteLeases(removed.Add);
        var ip = IPAddress.Parse("192.0.2.12");
        var route = new NetworkRoute(ip + "/32", 2, "192.0.2.1");
        await using var probe = await leases.AcquireAsync(ip, () => new(route, "192.0.2.5"), default);
        await using var tunnel = await leases.AcquireAsync(ip, () => throw new Exception("Route already exists"), default, true);
        Assert.Equal(probe.SourceAddress, tunnel.SourceAddress);
        await probe.DisposeAsync();
        Assert.Empty(removed);
        await tunnel.DisposeAsync();
        Assert.Equal([route], removed);
    }
    [Fact]
    public async Task ExistingRouteIsBorrowedAndNeverRemoved()
    {
        var leases = new EndpointRouteLeases(_ => Assert.Fail("Borrowed route removed"));
        await using var lease = await leases.AcquireAsync(IPAddress.Parse("192.0.2.13"), () => new(null, "192.0.2.5"), default, true);
    }
    [Fact]
    public async Task TunnelUpgradesPhysicalProbeToAnExactRouteWithoutLosingItsLease()
    {
        var removed = new List<NetworkRoute>();
        var leases = new EndpointRouteLeases(removed.Add);
        var ip = IPAddress.Parse("192.0.2.14");
        var route = new NetworkRoute(ip + "/32", 2, "192.0.2.1");
        await using var probe = await leases.AcquireAsync(ip, () => new(null, null), default);
        await using var tunnel = await leases.AcquireAsync(ip, () => new(route, "192.0.2.5"), default, true);
        await tunnel.DisposeAsync();
        Assert.Empty(removed);
        await probe.DisposeAsync();
        Assert.Equal([route], removed);
    }
    [Fact]
    public async Task FailedRemovalRemainsOwnedAndCanBeRetried()
    {
        var fail = true;
        var removed = new List<NetworkRoute>();
        var leases = new EndpointRouteLeases(r => { if (fail) throw new IOException(); removed.Add(r); });
        var ip = IPAddress.Parse("192.0.2.15");
        var route = new NetworkRoute(ip + "/32", 2, "192.0.2.1");
        var first = await leases.AcquireAsync(ip, () => new(route, "192.0.2.5"), default);
        await Assert.ThrowsAsync<IOException>(() => first.DisposeAsync().AsTask());
        fail = false;
        await using var retry = await leases.AcquireAsync(ip, () => throw new Exception("Ownership lost"), default);
        await retry.DisposeAsync();
        Assert.Equal([route], removed);
    }
}
