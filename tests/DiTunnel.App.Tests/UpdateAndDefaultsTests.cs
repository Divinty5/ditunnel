using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class UpdateAndDefaultsTests
{
    [Fact]
    public void MigrationAndAutomaticInstallPreferenceSurviveSettingsReload()
    {
        var path = Path.Combine(Path.GetTempPath(), "DiTunnel-defaults-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"CheckForUpdatesAutomatically\":true}");
            var settings = UserSettings.Load(path);
            Assert.False(settings.InstallUpdatesAutomatically);
            Assert.False(settings.SplitTunnelDefaultsApplied);
            Assert.True(settings.ApplySplitTunnelDefaults([new("outlook.exe", "Outlook")]));
            settings.InstallUpdatesAutomatically = true;
            settings.Save(path);
            var reloaded = UserSettings.Load(path);
            Assert.True(reloaded.InstallUpdatesAutomatically);
            Assert.True(reloaded.SplitTunnelDefaultsApplied);
            Assert.Equal(SplitTunnelMode.BypassSelected, reloaded.SplitTunnelMode);
            Assert.Equal(new[] { "outlook.exe" }, reloaded.BypassSplitTunnelProcesses);
            Assert.False(reloaded.ApplySplitTunnelDefaults([new("saby.exe", "Saby")]));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void DisabledSplitModeIsMigratedOnceWithoutLosingSelections()
    {
        var settings = new UserSettings { BypassSplitTunnelProcesses = ["custom.exe"], BypassSplitTunnelDomains = ["example.com"],
            ProxySelectedSplitTunnelProcesses = ["proxy.exe"] };
        Assert.True(settings.ApplySplitTunnelDefaults([new("outlook.exe", "Outlook"), new("custom.exe", "Custom")]));
        Assert.Equal(SplitTunnelMode.BypassSelected, settings.SplitTunnelMode);
        Assert.Equal(new[] { "custom.exe", "outlook.exe" }, settings.BypassSplitTunnelProcesses);
        Assert.Equal(new[] { "example.com" }, settings.BypassSplitTunnelDomains);
        Assert.Equal(new[] { "proxy.exe" }, settings.ProxySelectedSplitTunnelProcesses);
        settings.SplitTunnelMode = SplitTunnelMode.ProxyAll;
        settings.BypassSplitTunnelProcesses.Clear();
        Assert.False(settings.ApplySplitTunnelDefaults([new("outlook.exe", "Outlook")]));
        Assert.Equal(SplitTunnelMode.ProxyAll, settings.SplitTunnelMode);
        Assert.Empty(settings.BypassSplitTunnelProcesses);
    }

    [Theory]
    [InlineData(SplitTunnelMode.BypassSelected)]
    [InlineData(SplitTunnelMode.ProxySelected)]
    public void ExistingSplitModeAndItsSelectionsArePreserved(SplitTunnelMode mode)
    {
        var settings = new UserSettings { SplitTunnelMode = mode, BypassSplitTunnelProcesses = ["custom.exe"],
            ProxySelectedSplitTunnelProcesses = ["telegram.exe"] };
        Assert.False(settings.ApplySplitTunnelDefaults([new("outlook.exe", "Outlook")]));
        Assert.Equal(mode, settings.SplitTunnelMode);
        Assert.Equal(new[] { "custom.exe" }, settings.BypassSplitTunnelProcesses);
        Assert.Equal(new[] { "telegram.exe" }, settings.ProxySelectedSplitTunnelProcesses);
        Assert.True(settings.SplitTunnelDefaultsApplied);
    }

    private static readonly AppRelease Release = new(new Version(99, 0, 0), "https://github.com/Divinty5/ditunnel/releases",
        "https://example.com/setup.exe", "https://example.com/setup.exe.sha256");
    [Fact]
    public async Task LaterPromptsAgainOnEachLaunchEvenForPreviouslySkippedVersion()
    {
        var settings = new UserSettings { SkippedUpdateVersion = "99.0.0", CheckForUpdatesAutomatically = false };
        var installer = new Installer();
        int prompts = 0;
        for (int i = 0; i < 2; i++)
            await UpdateFlow.CheckCoreAsync(new MainViewModel(null, new EmptyStore()), false, settings, installer,
                () => Task.FromResult(new ReleaseCheckResult("Update", Release)), (_, _) => { prompts++; return Task.FromResult<string?>("later"); });
        Assert.Equal(2, prompts);
        Assert.Equal(0, installer.Downloads);
        Assert.Equal("99.0.0", settings.SkippedUpdateVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticInstallDownloadsAndLaunchesWithoutPrompt(bool manual)
    {
        var installer = new Installer();
        await UpdateFlow.CheckCoreAsync(new MainViewModel(null, new EmptyStore()), manual,
            new UserSettings { InstallUpdatesAutomatically = true }, installer,
            () => Task.FromResult(new ReleaseCheckResult("Update", Release)), (_, _) => throw new Exception("Unexpected prompt"));
        Assert.Equal(1, installer.Downloads);
        Assert.Equal(1, installer.Launches);
    }

    [Fact]
    public async Task MissingChecksumNeverStartsAutomaticOrManualInstall()
    {
        var installer = new Installer();
        bool offeredInstall = true;
        await UpdateFlow.CheckCoreAsync(new MainViewModel(null, new EmptyStore()), false,
            new UserSettings { InstallUpdatesAutomatically = true }, installer,
            () => Task.FromResult(new ReleaseCheckResult("Update", Release with { ChecksumUrl = null })),
            (_, canInstall) => { offeredInstall = canInstall; return Task.FromResult<string?>("install"); });
        Assert.False(offeredInstall);
        Assert.Equal(0, installer.Downloads);
        Assert.Equal(0, installer.Launches);
    }

    [Fact]
    public async Task FailedDownloadDoesNotLaunchOrRunAfterLaunchAction()
    {
        var installer = new Installer { Fail = true };
        bool callback = false;
        await UpdateFlow.CheckCoreAsync(new MainViewModel(null, new EmptyStore()), false,
            new UserSettings { InstallUpdatesAutomatically = true }, installer,
            () => Task.FromResult(new ReleaseCheckResult("Update", Release)), (_, _) => Task.FromResult<string?>(null), () => callback = true);
        Assert.Equal(0, installer.Launches);
        Assert.False(callback);
    }

    private sealed class Installer : IUpdateInstaller
    {
        public int Downloads, Launches;
        public bool Fail;
        public Task<string> DownloadAsync(AppRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        { Downloads++; return Fail ? Task.FromException<string>(new IOException()) : Task.FromResult("checked-installer.exe"); }
        public void Launch(string path) { Assert.Equal("checked-installer.exe", path); Launches++; }
    }
    private sealed class EmptyStore : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => [];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }
}
