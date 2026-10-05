using System.IO.Compression;
using Avalonia;
using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class SettingsAndRefreshTests
{
    private sealed class Store : IProfileStore
    {
        public List<ImportedProfile> Items = [new("Old", "VLESS", "old", "sub", "Subscription", "https://example.com/sub"), new("Manual", "SS", "manual", "manual", "Manual")];
        public bool Fail;
        public IReadOnlyList<ImportedProfile> Load() => Items;
        public void Save(IEnumerable<ImportedProfile> profiles) { if (Fail) throw new IOException(); Items = profiles.ToList(); }
    }
    [Fact] public async Task RefreshReplacesSnapshotButKeepsManualProfiles()
    {
        var store = new Store(); var vm = new MainViewModel(null, store);
        await vm.RefreshSubscriptionsAsync((_, _) => Task.FromResult<IReadOnlyList<ImportedProfile>>([new("New", "VLESS", "new")]));
        Assert.Equal(2, store.Items.Count);
        Assert.DoesNotContain(store.Items, p => p.Content == "old");
        Assert.Contains(store.Items, p => p.Content == "manual");
        Assert.Equal("sub", vm.SelectedProfile!.Profile.SourceId);
        Assert.Equal("https://example.com/sub", vm.SelectedProfile.Profile.SourceUrl);
    }
    [Fact] public async Task FailedDownloadOrSaveNeverDestroysSnapshot()
    {
        var store = new Store(); var vm = new MainViewModel(null, store);
        await vm.RefreshSubscriptionsAsync((_, _) => throw new HttpRequestException());
        Assert.Equal("old", vm.SelectedProfile!.Profile.Content);
        Assert.Equal("#d9c8324a", vm.RefreshToastBackground.ToString());
        store.Fail = true;
        await vm.RefreshSubscriptionsAsync((_, _) => Task.FromResult<IReadOnlyList<ImportedProfile>>([new("New", "VLESS", "new")]));
        Assert.Equal("old", vm.SelectedProfile!.Profile.Content);
        Assert.Contains("Не удалось обновить: 1", vm.SubscriptionStatus);
        Assert.False(vm.IsBusy);
    }
    [Fact] public async Task AutomaticStartupRefreshKeepsFailureSilent()
    {
        var store = new Store(); var vm = new MainViewModel(null, store);

        await vm.RefreshSubscriptionsAsync((_, _) => throw new HttpRequestException(), showToast: false);

        Assert.False(vm.IsRefreshToastVisible);
        Assert.Equal("old", vm.SelectedProfile!.Profile.Content);
        Assert.Contains("Не удалось обновить: 1", vm.SubscriptionStatus);
    }
    [Fact] public void WindowPlacementRespectsSideTaskbarAndDpi()
    {
        var work = new PixelRect(80, 0, 1840, 1080);
        var initial = WindowLayout.Fit(null, work, 1.25);
        Assert.Equal(1000, initial.X);
        Assert.Equal(736, initial.Width);
        var moved = WindowLayout.Fit(new(-4000, -500, 4000, 3000, true), work, 1.25);
        Assert.Equal(80, moved.X); Assert.Equal(0, moved.Y);
        Assert.True(moved.Height * 1.25 + 50 <= work.Height);
        Assert.True(moved.Maximized);
        var compact = WindowLayout.Fit(new(100, 100, 100, 100, false), new PixelRect(0, 0, 1920, 1080), 1);
        Assert.Equal(360, compact.Width);
        Assert.Equal(500, compact.Height);
    }
    [Fact] public void SettingsRoundTripAndInvalidValuesFallBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            new UserSettings { Theme = "light", Language = "en", CloseAction = "hide", KillSwitchEnabled = true, BlockAdsEnabled = true, StrictAdBlockingEnabled = true, AllowLocalNetwork = true, StartWithWindows = true, AutoConnect = true, Window = new(10, 20, 600, 700, true) }.Save(path);
            var loaded = UserSettings.Load(path); Assert.Equal("en", loaded.Language); Assert.Equal("light", loaded.Theme); Assert.True(loaded.Window!.Maximized); Assert.True(loaded.GetConnectionPolicy().KillSwitchEnabled); Assert.True(loaded.BlockAdsEnabled); Assert.True(loaded.StrictAdBlockingEnabled); Assert.True(loaded.GetConnectionPolicy().AutoConnect);
            File.WriteAllText(path, "{\"Language\":\"bad\",\"Theme\":\"bad\"}");
            Assert.Equal("ru", UserSettings.Load(path).Language); Assert.Equal("system", UserSettings.Load(path).Theme);
            File.WriteAllText(path, "broken"); Assert.Equal("ask", UserSettings.Load(path).CloseAction);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact] public void SplitTunnelRulesAreStoredSeparatelyForBothModes()
    {
        var settings = new UserSettings();
        settings.SetSplitTunnelRules(SplitTunnelMode.BypassSelected, ["direct.example"], ["direct.app"]);
        settings.SetSplitTunnelRules(SplitTunnelMode.ProxySelected, ["proxy.example"], ["proxy.app"]);

        Assert.Equal(["direct.example"], settings.GetSplitTunnelRules(SplitTunnelMode.BypassSelected).Domains);
        Assert.Equal(["direct.app"], settings.GetSplitTunnelRules(SplitTunnelMode.BypassSelected).Processes);
        Assert.Equal(["proxy.example"], settings.GetSplitTunnelRules(SplitTunnelMode.ProxySelected).Domains);
        Assert.Equal(["proxy.app"], settings.GetSplitTunnelRules(SplitTunnelMode.ProxySelected).Processes);
    }

    [Fact] public void LegacySplitTunnelRulesAreMigratedToTheirSelectedMode()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"SplitTunnelMode\":2,\"SplitTunnelDomains\":[\"proxy.example\"],\"SplitTunnelProcesses\":[\"proxy.app\"]}");

            var loaded = UserSettings.Load(path);

            Assert.Equal(["proxy.example"], loaded.ProxySelectedSplitTunnelDomains);
            Assert.Equal(["proxy.app"], loaded.ProxySelectedSplitTunnelProcesses);
            Assert.Empty(loaded.BypassSplitTunnelDomains);
            Assert.Empty(loaded.BypassSplitTunnelProcesses);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(SplitTunnelMode.BypassSelected)]
    [InlineData(SplitTunnelMode.ProxySelected)]
    public void ExecutableSelectionReachesPolicyAndTriggersNetworkSettingsChange(SplitTunnelMode mode)
    {
        var settings = new UserSettings { SplitTunnelMode = mode };
        settings.SetSplitTunnelRules(mode, ["example.com"], []);
        var before = settings.NetworkSettingsFingerprint();
        const string executable = @"C:\Users\tester\AppData\Local\Vivaldi\Application\vivaldi.exe";
        settings.SetSplitTunnelRules(mode, ["example.com"], [executable]);

        var policy = settings.GetSplitTunnelPolicy();
        Assert.Equal(mode, policy.Mode);
        Assert.Equal([executable], policy.Processes);
        Assert.NotEqual(before, settings.NetworkSettingsFingerprint());
        // A policy passed to a running operation must remain a snapshot of its settings.
        settings.GetSplitTunnelRules(mode).Processes.Clear();
        Assert.Equal([executable], policy.Processes);
        Assert.Equal(before, settings.NetworkSettingsFingerprint());
    }

    [Fact] public void ChatGptAndCodexHelpersAreAutoSelectedTogether()
    {
        InstalledApplication[] installed =
        [
            new(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.930.2377.0_x64__publisher\app\ChatGPT.exe", "Codex"),
            new(@"C:\Users\tester\AppData\Local\OpenAI\Codex\bin\version\codex.exe", "Codex CLI"),
            new(@"C:\Tools\codex.exe", "codex"),
            new(@"C:\Program Files\nodejs\node.exe", "Node.js"),
            new(@"C:\Windows\System32\cmd.exe", "Windows Command Processor")
        ];
        Assert.Equal(installed.Take(3).Select(app => app.Id), ApplicationSelectionPresets.Select(SplitTunnelMode.ProxySelected, installed));
        Assert.Empty(ApplicationSelectionPresets.Select(SplitTunnelMode.BypassSelected, installed));
    }

    [Fact] public void InactiveRulesDoNotRestartProxyAllOrTheOtherSplitMode()
    {
        var settings = new UserSettings { SplitTunnelMode = SplitTunnelMode.ProxyAll };
        var all = settings.NetworkSettingsFingerprint();
        settings.SetSplitTunnelRules(SplitTunnelMode.BypassSelected, [], ["direct.exe"]);
        Assert.Equal(all, settings.NetworkSettingsFingerprint());
        settings.SplitTunnelMode = SplitTunnelMode.BypassSelected;
        var bypass = settings.NetworkSettingsFingerprint();
        settings.SetSplitTunnelRules(SplitTunnelMode.ProxySelected, [], ["proxy.exe"]);
        Assert.Equal(bypass, settings.NetworkSettingsFingerprint());
    }

    [Fact] public void AutoSelectionRecognizesDesktopAndAndroidWorkAppsWithoutMatchingUnrelatedTeams()
    {
        InstalledApplication[] installed =
        [
            new(@"C:\Users\me\Programs\VK Teams\vkworkspace.exe", "VK WorkSpace"),
            new(@"C:\Program Files\Yandex\YandexBrowser\Application\browser.exe", "Browser"),
            new(@"C:\Apps\Saby\sbis.exe", "Saby"),
            new(@"C:\Apps\2GIS\2gis.exe", "2Гис"),
            new(@"C:\Apps\Microsoft Office\OUTLOOK.EXE", "Microsoft Outlook"),
            new(@"C:\Apps\OpenCode\opencode.exe", "OpenCode"),
            new("com.microsoft.office.outlook", "Outlook"),
            new("com.doublegis.mobile", "2GIS"),
            new(@"C:\Apps\Microsoft Teams\ms-teams.exe", "Microsoft Teams"),
            new(@"C:\Apps\ChatGPT\ChatGPT.exe", "ChatGPT"),
            new(@"C:\Apps\Claude\claude.exe", "Claude Code"),
            new(@"C:\Apps\Telegram Desktop\Telegram.exe", "Telegram"),
            new("com.google.android.youtube", "YouTube")
        ];
        Assert.Equal(installed.Take(8).Select(app => app.Id), ApplicationSelectionPresets.Select(SplitTunnelMode.BypassSelected, installed));
        Assert.Equal(installed.Skip(9).Select(app => app.Id), ApplicationSelectionPresets.Select(SplitTunnelMode.ProxySelected, installed));
    }

    [Fact] public void ApplicationAutoSelectionAddsExpectedPackagesWithoutInventingMissingOnes()
    {
        InstalledApplication[] installed =
        [
            new("com.google.android.youtube", "YouTube"),
            new("com.openai.chatgpt", "ChatGPT"),
            new("com.vivaldi.browser", "Vivaldi Browser"),
            new("com.vivaldi.browser.snapshot", "Vivaldi Snapshot"),
            new("com.mobifitness", "Fitness"),
            new("ru.sberbankmobile", "СберБанк"),
            new("com.yandex.browser", "Яндекс Браузер"),
            new("com.example.notes", "Notes")
        ];

        Assert.Equal(["com.google.android.youtube", "com.openai.chatgpt", "com.vivaldi.browser", "com.vivaldi.browser.snapshot"], ApplicationSelectionPresets.Select(SplitTunnelMode.ProxySelected, installed));
        Assert.Equal(["com.mobifitness", "ru.sberbankmobile", "com.yandex.browser"], ApplicationSelectionPresets.Select(SplitTunnelMode.BypassSelected, installed));
    }
    [Fact] public void BypassAutoSelectionAddsRussianSocialAppsAndRuStore()
    {
        InstalledApplication[] installed =
        [
            new("com.vkontakte.android", "ВКонтакте"),
            new("com.vk.vkvideo", "VK Видео"),
            new("ru.oneme.app", "MAX"),
            new("ru.ok.android", "Одноклассники"),
            new("ru.vk.store", "RuStore"),
            new("com.example.notes", "Notes")
        ];

        Assert.Equal(
            ["com.vkontakte.android", "com.vk.vkvideo", "ru.oneme.app", "ru.ok.android", "ru.vk.store"],
            ApplicationSelectionPresets.Select(SplitTunnelMode.BypassSelected, installed));
    }
    [Fact] public void DiagnosticsExportsOnlySanitizedNetworkEvents()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "last-network.log"), "2026-09-06T00:00:00Z CONNECTED\nsecret https://example.com/token\n2026-09-06T00:00:01Z ERROR_STAGE_DNS\n");
            File.WriteAllText(Path.Combine(dir, "profiles.dat"), "secret");
            var output = Path.Combine(dir, "export.zip"); Diagnostics.Export(output, dir);
            using var zip = ZipFile.OpenRead(output);
            Assert.Equal(2, zip.Entries.Count);
            using var reader = new StreamReader(zip.GetEntry("last-network.log")!.Open()); var text = reader.ReadToEnd();
            Assert.Contains("CONNECTED", text); Assert.DoesNotContain("secret", text);
        }
        finally { Directory.Delete(dir, true); }
    }
}
