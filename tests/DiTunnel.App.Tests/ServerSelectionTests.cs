using DiTunnel.App.ViewModels;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class ServerSelectionTests
{
    private sealed class Store : IProfileStore
    {
        public List<ImportedProfile> Items = [new("HTTPU", "VLESS", "synthetic-httpu", "sub", "Subscription", "https://example.test/sub"),
            new("HY2", "Hysteria 2", "synthetic-hy2", "sub", "Subscription", "https://example.test/sub"), new("Other", "SS", "synthetic-other", "other", "Other")];
        public IReadOnlyList<ImportedProfile> Load() => Items;
        public void Save(IEnumerable<ImportedProfile> profiles) => Items = profiles.ToList();
    }

    [Fact]
    public async Task Selection_survives_restart_refresh_group_switch_and_changed_credentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiTunnel-selection-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var settings = new UserSettings(); var store = new Store();
        try
        {
            var first = new MainViewModel(null, store, selectionSettings: settings, persistSelection: () => settings.Save(path));
            first.SelectedGroup = first.Groups.Single(g => g.Id == "sub");
            first.SelectProfileFromUser(first.Profiles.Single(p => p.Name == "HY2"));
            first.SelectedGroup = first.Groups.Single(g => g.Id == "other");
            first.SelectedGroup = first.Groups.Single(g => g.Id == "sub");
            Assert.Equal("HY2", first.SelectedProfile!.Name);
            var serialized = File.ReadAllText(path);
            Assert.DoesNotContain("synthetic-hy2", serialized);
            Assert.DoesNotContain("HY2", serialized);
            var loaded = UserSettings.Load(path);
            var second = new MainViewModel(null, store, selectionSettings: loaded, persistSelection: () => loaded.Save(path));
            Assert.Equal("HY2", second.SelectedProfile!.Name);
            await second.RefreshSubscriptionsAsync((_, _) => Task.FromResult<IReadOnlyList<ImportedProfile>>(
                store.Items.Where(p => p.SourceId == "sub").Select(p => p with { Content = p.Content + "-updated" }).ToArray()), showToast: false);
            Assert.Equal("HY2", second.SelectedProfile!.Name);
            Assert.EndsWith("-updated", second.SelectedProfile.Profile.Content);
            store.Items.RemoveAll(p => p.Name == "HY2");
            var third = new MainViewModel(null, store, selectionSettings: loaded, persistSelection: () => loaded.Save(path));
            Assert.Equal("HTTPU", third.SelectedProfile!.Name);
            await first.ShutdownAsync(); await second.ShutdownAsync(); await third.ShutdownAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
