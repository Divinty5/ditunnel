using DiTunnel.Core.Connection;

namespace DiTunnel.Core.Tests;

public sealed class ConnectionPolicyTests
{
    [Fact]
    public void AutoConnectAlsoWorksAfterManualLaunch() =>
        Assert.True(new ConnectionPolicy(false, false, false, true).Normalize().AutoConnect);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 30)]
    [InlineData(12, 30)]
    public void ReconnectUsesBoundedExponentialBackoff(int failures, int seconds)
    {
        var schedule = ReconnectSchedule.Create(new ConnectionPolicy(false, false, true, true), failures);
        Assert.NotNull(schedule);
        Assert.Equal(failures + 1, schedule.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(seconds), schedule.Delay);
    }

    [Fact]
    public void ReconnectIsOffWhenAutomaticConnectionIsOff() =>
        Assert.Null(ReconnectSchedule.Create(ConnectionPolicy.Default, 0));

    [Fact]
    public void ActiveTunnelAlwaysUsesRecoveryBackoff()
    {
        var schedule = ReconnectSchedule.CreateForActiveTunnel(2);
        Assert.NotNull(schedule);
        Assert.Equal(3, schedule.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(4), schedule.Delay);
    }

    [Fact]
    public void ActiveTunnelRecoveryStopsAfterTenFailures()
    {
        Assert.NotNull(ReconnectSchedule.CreateForActiveTunnel(9));
        Assert.Null(ReconnectSchedule.CreateForActiveTunnel(10));
    }
}
