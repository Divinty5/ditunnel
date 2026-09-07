using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Tests;

public sealed class SubscriptionUsageTests
{
    [Fact] public void ParsesLimitsAndClampsOverageAndExpiry()
    {
        var usage = SubscriptionUsage.Parse("upload=10; download=30; total=100; expire=86401")!;
        Assert.Equal(60m, usage.RemainingBytes);
        Assert.Equal(2, usage.RemainingDays(DateTimeOffset.UnixEpoch));
        Assert.Equal(0, usage.RemainingDays(DateTimeOffset.UnixEpoch.AddDays(5)));
        Assert.Equal(0m, SubscriptionUsage.Parse("upload=100; download=30; total=10")!.RemainingBytes);
    }
    [Fact] public void UnlimitedMissingAndMalformedValuesAreNotInvented()
    {
        Assert.Null(SubscriptionUsage.Parse(null));
        var unlimited = SubscriptionUsage.Parse("upload=0; download=0; total=0; expire=0")!;
        Assert.Null(unlimited.RemainingBytes); Assert.Null(unlimited.RemainingDays(DateTimeOffset.UtcNow));
        Assert.Null(SubscriptionUsage.Parse("total=100; download=bad; upload=-1")!.RemainingBytes);
        Assert.Null(SubscriptionUsage.Parse("total=99999999999999999999999999999999")!.Total);
    }
}
