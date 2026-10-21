using System.Net;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Network;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class LinuxEndpointResolverTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Protected_retry_reuses_previously_resolved_endpoint_after_dns_cache_expiry()
    {
        var clock = new Clock();
        var calls = 0;
        var resolver = new LinuxEndpointResolver(clock, (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2." + ++calls) }));
        var profile = ProfileParser.Parse("vless://11111111-1111-1111-1111-111111111111@synthetic.invalid:443?security=none").Single();
        Assert.Equal(IPAddress.Parse("192.0.2.1"), await resolver.ResolveAsync(profile, true, CancellationToken.None));
        clock.Now = clock.Now.AddHours(1);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), await resolver.ResolveAsync(profile, true, CancellationToken.None, protectedRetry: true));
        Assert.Equal(1, calls);
        Assert.Equal(IPAddress.Parse("192.0.2.2"), await resolver.ResolveAsync(profile, true, CancellationToken.None));
        Assert.Equal(2, calls);
    }
}
