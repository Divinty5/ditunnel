using DiTunnel.Core.Connection;
using System.Xml.Linq;

namespace DiTunnel.Core.Tests;

public sealed class ProbePlatformTests
{
    [Theory]
    [InlineData("Windows")]
    [InlineData("Android")]
    [InlineData("Linux")]
    public async Task SharedPolicyPreservesPlatformAndVersionInCopiedReport(string platform)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        Assert.NotNull(root);
        var version = XDocument.Load(Path.Combine(root.FullName, "Directory.Build.props"))
            .Descendants(platform + "Version").Single().Value;
        var result = await ProbePolicy.CheckHttpsAsync((uri, token) => Task.CompletedTask,
            CancellationToken.None, platform: platform);
        Assert.Equal(platform, result.Diagnostics!.Platform);
        Assert.Equal(version, result.Diagnostics.ClientVersion);
        Assert.Contains($"Di-Tunnel {platform} {version}", result.Diagnostics.ToDisplayString());
        Assert.True(result.Diagnostics.ProfileConfirmed);
    }
}
