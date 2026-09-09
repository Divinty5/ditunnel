using System.IO.Compression;
using Avalonia;
using DiTunnel.App.ViewModels;
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
        store.Fail = true;
        await vm.RefreshSubscriptionsAsync((_, _) => Task.FromResult<IReadOnlyList<ImportedProfile>>([new("New", "VLESS", "new")]));
        Assert.Equal("old", vm.SelectedProfile!.Profile.Content);
        Assert.Contains("Не удалось обновить: 1", vm.SubscriptionStatus);
        Assert.False(vm.IsBusy);
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
            new UserSettings { Theme = "light", Language = "en", CloseAction = "hide", KillSwitchEnabled = true, AllowLocalNetwork = true, StartWithWindows = true, AutoConnect = true, Window = new(10, 20, 600, 700, true) }.Save(path);
            var loaded = UserSettings.Load(path); Assert.Equal("en", loaded.Language); Assert.Equal("light", loaded.Theme); Assert.True(loaded.Window!.Maximized); Assert.True(loaded.GetConnectionPolicy().KillSwitchEnabled); Assert.True(loaded.GetConnectionPolicy().AutoConnect);
            File.WriteAllText(path, "{\"Language\":\"bad\",\"Theme\":\"bad\"}");
            Assert.Equal("ru", UserSettings.Load(path).Language); Assert.Equal("system", UserSettings.Load(path).Theme);
            File.WriteAllText(path, "broken"); Assert.Equal("ask", UserSettings.Load(path).CloseAction);
        }
        finally { Directory.Delete(dir, true); }
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
