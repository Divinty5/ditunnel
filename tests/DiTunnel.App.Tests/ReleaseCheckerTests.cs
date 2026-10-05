using System.Text.Json;
using DiTunnel.App;

namespace DiTunnel.App.Tests;

public sealed class ReleaseCheckerTests
{
    private static object CombinedRelease513(string? missingChecksum = null)
    {
        const string version = "0.5.13";
        string[] names = ["Di-Tunnel-0.5.13-Setup-x64.exe", "Di-Tunnel-0.5.13-arm64.apk"];
        var assets = names.SelectMany(name => name == missingChecksum ? new[] { name } : new[] { name, name + ".sha256" })
            .Select(name => new { name, browser_download_url = $"https://github.com/Divinty5/ditunnel/releases/download/v{version}/{name}" });
        return new { tag_name = "v" + version, draft = false, prerelease = false, assets };
    }

    [Theory]
    [InlineData(false, "Di-Tunnel-0.5.13-Setup-x64.exe")]
    [InlineData(true, "Di-Tunnel-0.5.13-arm64.apk")]
    public void CombinedStable513ReleaseOffersCorrectPlatformFiles(bool android, string installerName)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(CombinedRelease513()));
        var release = ReleaseChecker.Parse(json.RootElement, new(0, 5, 12), android).Release;
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 5, 13), release.Version);
        Assert.Equal($"https://github.com/Divinty5/ditunnel/releases/download/v0.5.13/{installerName}", release.InstallerUrl);
        Assert.Equal(release.InstallerUrl + ".sha256", release.ChecksumUrl);
        Assert.Null(ReleaseChecker.Parse(json.RootElement, new(0, 5, 13), android).Release);
    }

    [Theory]
    [InlineData(false, "Di-Tunnel-0.5.13-Setup-x64.exe")]
    [InlineData(true, "Di-Tunnel-0.5.13-arm64.apk")]
    public void Missing513ChecksumHidesOnlyAffectedPlatform(bool android, string installerName)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(CombinedRelease513(installerName)));
        Assert.Null(ReleaseChecker.Parse(json.RootElement, new(0, 5, 12), android).Release);
        Assert.NotNull(ReleaseChecker.Parse(json.RootElement, new(0, 5, 12), !android).Release);
    }

    private static object Release(string version, bool android, bool checksum = true, bool draft = false, bool prerelease = false)
    {
        var name = android ? $"Di-Tunnel-{version}-arm64.apk" : $"Di-Tunnel-{version}-Setup-x64.exe";
        var assets = new List<object> { new { name, browser_download_url = $"https://github.com/Divinty5/ditunnel/releases/download/v{version}/{name}" } };
        if (checksum) assets.Add(new { name = name + ".sha256", browser_download_url = $"https://github.com/Divinty5/ditunnel/releases/download/v{version}/{name}.sha256" });
        return new { tag_name = "v" + version, assets, draft, prerelease };
    }

    [Fact]
    public void WindowsOnlyReleaseDoesNotOfferAndroidAnUpdate()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Release("0.5.7", false)));
        Assert.Equal(new Version(0, 5, 7), ReleaseChecker.Parse(json.RootElement, new(0, 5, 6)).Release?.Version);
        var android = ReleaseChecker.Parse(json.RootElement, new(0, 4, 33), android: true);
        Assert.Null(android.Release);
        Assert.Equal("Установлена актуальная версия.", android.Message);
    }

    [Fact]
    public void FindsLatestReleaseForEachPlatformRegardlessOfPublicationOrder()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new[] {
            Release("0.5.8", true), Release("0.5.7", false), Release("0.4.33", true), Release("0.5.6", false)
        }));
        Assert.Equal(new Version(0, 5, 7), ReleaseChecker.ParseReleases(json.RootElement, new(0, 4, 33)).Release?.Version);
        Assert.Equal(new Version(0, 5, 8), ReleaseChecker.ParseReleases(json.RootElement, new(0, 4, 33), android: true).Release?.Version);
    }

    [Fact]
    public void WindowsOnlyReleasesDoNotHideThePreviousAndroidRelease()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new[] {
            Release("0.5.7", false), Release("0.4.33", true)
        }));
        Assert.Null(ReleaseChecker.ParseReleases(json.RootElement, new(0, 4, 33), android: true).Release);
        Assert.Equal(new Version(0, 4, 33), ReleaseChecker.ParseReleases(json.RootElement, new(0, 4, 29), android: true).Release?.Version);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void IncompleteDraftAndPrereleaseBuildsAreNotOffered(bool checksum, bool draft, bool prerelease)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Release("0.5.7", false, checksum, draft, prerelease)));
        Assert.Null(ReleaseChecker.Parse(json.RootElement, new(0, 5, 6)).Release);
    }

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
    public void ParseFindsMatchingAndroidApkAndChecksum()
    {
        using var json = JsonDocument.Parse("""
        {
          "tag_name": "v0.4.27",
          "assets": [
            { "name": "Di-Tunnel-0.4.27-arm64.apk", "browser_download_url": "https://github.com/Divinty5/ditunnel/releases/download/v0.4.27/Di-Tunnel-0.4.27-arm64.apk" },
            { "name": "Di-Tunnel-0.4.27-arm64.apk.sha256", "browser_download_url": "https://github.com/Divinty5/ditunnel/releases/download/v0.4.27/Di-Tunnel-0.4.27-arm64.apk.sha256" }
          ]
        }
        """);

        var result = ReleaseChecker.Parse(json.RootElement, new Version(0, 4, 26), android: true);

        Assert.EndsWith(".apk", result.Release?.InstallerUrl);
        Assert.EndsWith(".apk.sha256", result.Release?.ChecksumUrl);
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
