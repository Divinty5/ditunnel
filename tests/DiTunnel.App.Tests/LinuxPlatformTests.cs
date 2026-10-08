using System.Text.Json;
using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class LinuxPlatformTests
{
    private sealed class EmptyStore : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => [];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }

    [Fact]
    public void LinuxShellDoesNotOfferVpnOrClaimWindowsProtection()
    {
        var vm = new MainViewModel(null, new EmptyStore(), platform: AppPlatform.Linux with { SupportsKillSwitch = false, SupportsAdvancedNetworkSettings = false });
        Assert.True(vm.CanImport);
        Assert.False(vm.CanConnect);
        Assert.False(vm.IsKillSwitchEnabled);
        Assert.False(vm.CanRestoreNetwork);
        Assert.Contains("Linux", vm.ConnectionHint);
        Assert.DoesNotContain("Windows", vm.ImportStorageDescription);
        Assert.False(vm.Platform.SupportsHideToTray);
        Assert.False(vm.Platform.SupportsAdvancedNetworkSettings);
    }

    [Fact]
    public async Task UnavailableStorageDisablesImportAndSubscriptionWrites()
    {
        const string message = "Storage locked";
        var vm = new MainViewModel(null, new UnavailableProfileStore(message), platform: AppPlatform.Linux);
        Assert.Equal(message, vm.Notice);
        Assert.False(vm.CanImport); Assert.False(vm.CanManageProfiles); Assert.False(vm.CanRefreshSubscriptions);
        await vm.RefreshSubscriptionsAsync((_, _) => throw new Exception("Must not download"));
        Assert.Equal(message, vm.Notice);
    }

    [Fact]
    public void LinuxPathCaseAffectsFingerprintWhileDomainCaseDoesNot()
    {
        var settings = new UserSettings { SplitTunnelMode = SplitTunnelMode.BypassSelected };
        settings.SetSplitTunnelRules(SplitTunnelMode.BypassSelected, ["EXAMPLE.COM"], ["/opt/A/app"]);
        var first = settings.NetworkSettingsFingerprint(AppPlatform.Linux);
        settings.SetSplitTunnelRules(SplitTunnelMode.BypassSelected, ["example.com"], ["/opt/A/app"]);
        Assert.Equal(first, settings.NetworkSettingsFingerprint(AppPlatform.Linux));
        var windows = settings.NetworkSettingsFingerprint(AppPlatform.Windows);
        settings.SetSplitTunnelRules(SplitTunnelMode.BypassSelected, ["example.com"], ["/opt/a/app"]);
        Assert.NotEqual(first, settings.NetworkSettingsFingerprint(AppPlatform.Linux));
        Assert.Equal(windows, settings.NetworkSettingsFingerprint(AppPlatform.Windows));
    }

    [Theory]
    [InlineData("Di-Tunnel-0.5.20-linux-amd64.deb", true)]
    [InlineData("Di-Tunnel-0.5.20-Setup-x64.exe", false)]
    [InlineData("Di-Tunnel-0.5.20-arm64.apk", false)]
    public void LinuxReleaseRequiresItsOwnPackageAndChecksum(string asset, bool available)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            tag_name = "v0.5.20",
            assets = new[] { asset, asset + ".sha256" }.Select(name => new { name, browser_download_url = "https://github.com/Divinty5/ditunnel/releases/download/v0.5.20/" + name })
        }));
        Assert.Equal(available, ReleaseChecker.Parse(json.RootElement, new(0, 5, 19), ReleaseTarget.LinuxX64).Release is not null);
    }
}
