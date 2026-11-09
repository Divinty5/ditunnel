using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Tests;

public sealed class SubscriptionAccessTests
{
    [Fact]
    public void UnknownAndUnlimitedUsageNeverInventExpiry()
    {
        var profile = new ImportedProfile("Synthetic", "VLESS", "synthetic", Usage: new(null, null, 0, 0));
        SubscriptionAccess.EnsureAvailable(profile);
        Assert.False(profile.Usage!.IsExhausted(DateTimeOffset.UtcNow));
    }
    [Fact]
    public void TrafficExhaustionDoesNotDependOnExpiryDate()
    {
        var profile = new ImportedProfile("Synthetic", "VLESS", "synthetic", Usage: new(90, 20, 100, null));
        Assert.Throws<SubscriptionExhaustedException>(() => SubscriptionAccess.EnsureAvailable(profile));
    }
}
