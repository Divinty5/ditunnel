using System.Text.Json;
using DiTunnel.App;

namespace DiTunnel.App.Tests;

public sealed class ReleaseCheckerTests
{
    [Fact]
    public void ParseFindsMatchingWindowsInstallerAndChecksum()
    {
        using var json = JsonDocument.Parse("""
        {
          "tag_name": "v0.5.0",
          "html_url": "https://github.com/Divinty5/ditunnel/releases/tag/v0.5.0",
          "assets": [
            { "name": "Di-Tunnel-0.5.0-Setup-x64.exe", "browser_download_url": "https://github.com/Divinty5/ditunnel/releases/download/v0.5.0/Di-Tunnel-0.5.0-Setup-x64.exe" },
            { "name": "Di-Tunnel-0.5.0-Setup-x64.exe.sha256", "browser_download_url": "https://github.com/Divinty5/ditunnel/releases/download/v0.5.0/Di-Tunnel-0.5.0-Setup-x64.exe.sha256" }
          ]
        }
        """);

        var result = ReleaseChecker.Parse(json.RootElement, new Version(0, 4, 24));

        Assert.Equal(new Version(0, 5, 0), result.Release?.Version);
        Assert.EndsWith(".exe", result.Release?.InstallerUrl);
        Assert.EndsWith(".sha256", result.Release?.ChecksumUrl);
    }

    [Fact]
    public void ParseRejectsAssetFromUntrustedHost()
    {
        using var json = JsonDocument.Parse("""
        {
          "tag_name": "0.5.0",
          "assets": [
            { "name": "Di-Tunnel-0.5.0-Setup-x64.exe", "browser_download_url": "https://example.com/installer.exe" }
          ]
        }
        """);

        var result = ReleaseChecker.Parse(json.RootElement, new Version(0, 4, 24));

        Assert.Null(result.Release?.InstallerUrl);
    }

    [Fact]
    public void ParseReportsCurrentVersion()
    {
        using var json = JsonDocument.Parse("""{ "tag_name": "v0.4.24" }""");

        var result = ReleaseChecker.Parse(json.RootElement, new Version(0, 4, 24));

        Assert.Null(result.Release);
        Assert.Equal("Установлена актуальная версия.", result.Message);
    }
}
