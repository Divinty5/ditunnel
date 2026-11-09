using DiTunnel.Core.Connection;

namespace DiTunnel.Core.Tests;

public sealed class ProbePlatformTests
{
    [Theory]
    [InlineData("Windows", "0.6.8")]
    [InlineData("Android", "0.6.7")]
    [InlineData("Linux", "0.6.7")]
    public async Task SharedPolicyPreservesPlatformAndVersionInCopiedReport(string platform, string version)
    {
        var result = await ProbePolicy.CheckHttpsAsync((uri, token) => Task.CompletedTask,
            CancellationToken.None, platform: platform);
        Assert.Equal(platform, result.Diagnostics!.Platform);
        Assert.Equal(version, result.Diagnostics.ClientVersion);
        Assert.Contains($"Di-Tunnel {platform} {version}", result.Diagnostics.ToDisplayString());
        Assert.True(result.Diagnostics.ProfileConfirmed);
    }
}
