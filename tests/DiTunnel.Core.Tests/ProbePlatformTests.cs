using DiTunnel.Core.Connection;
using System.Xml.Linq;

namespace DiTunnel.Core.Tests;

public sealed class ProbePlatformTests
{
    [Theory]
    [InlineData("DNS сервера", "Dns", "DNS сервера · Ошибка DNS")]
    [InlineData("HTTPS", "Timeout", "HTTPS · Тайм-аут")]
    [InlineData("TLS", "Tls", "Ошибка TLS")]
    [InlineData("HTTPS", "ControlService", "HTTPS · Ошибка контрольного сайта")]
    [InlineData("AmneziaWG", "Runtime", "AmneziaWG · Ошибка запуска")]
    [InlineData("Xray", "Runtime", "Xray · Ошибка запуска")]
    [InlineData("Служба проверки", "Connection", "Служба проверки · Ошибка соединения")]
    public void SharedFailuresUseTheSameShortTextAndReportFields(string stage, string kind, string message)
    {
        foreach (var platform in new[] { "Windows", "Android", "Linux" })
        {
            var error = kind == "Runtime" ? new InvalidOperationException() : new ProbeFailureException(stage, kind);
            var result = ProbePolicy.Failure(error, stage, "HTTPS", 75, platform);
            Assert.Equal(message, result.Message);
            Assert.Null(result.Milliseconds);
            Assert.Equal(stage, result.Diagnostics!.Stage);
            Assert.Equal(kind, result.Diagnostics.ErrorType);
            Assert.Equal(platform, result.Diagnostics.Platform);
            Assert.False(result.Diagnostics.ProfileConfirmed);
        }
    }

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
