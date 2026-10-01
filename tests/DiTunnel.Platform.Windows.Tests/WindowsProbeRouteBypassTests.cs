using System.Diagnostics;
using System.Net;
using System.Reflection;
using DiTunnel.Platform.Windows;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class WindowsProbeRouteBypassTests
{
    [Fact]
    public async Task SameEndpointWaitsButOtherEndpointsRemainParallel()
    {
        var address = IPAddress.Parse("192.0.2.10");
        using var first = await WindowsProbeRouteBypass.AcquireEndpointAsync(address, default);
        var pending = WindowsProbeRouteBypass.AcquireEndpointAsync(address, default);
        Assert.False(pending.IsCompleted);
        using var other = await WindowsProbeRouteBypass.AcquireEndpointAsync(IPAddress.Parse("192.0.2.11"), default);
        first.Dispose();
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelledWaitDoesNotReleaseAnotherProbesRoute()
    {
        var address = IPAddress.Parse("192.0.2.12");
        using var first = await WindowsProbeRouteBypass.AcquireEndpointAsync(address, default);
        using var cancellation = new CancellationTokenSource();
        var cancelled = WindowsProbeRouteBypass.AcquireEndpointAsync(address, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var pending = WindowsProbeRouteBypass.AcquireEndpointAsync(address, default);
        Assert.False(pending.IsCompleted);
        first.Dispose();
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
    }

}