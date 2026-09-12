using DiTunnel.App.ViewModels;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class ProfileGroupTests
{
    private sealed class Store : IProfileStore
    {
        public List<ImportedProfile> Items = [new("A1", "VLESS", "a1", "a", "Subscription A"), new("A2", "SS", "a2", "a", "Subscription A"), new("B1", "VLESS", "b1", "b", "Subscription B")];
        public bool FailSave;
        public IReadOnlyList<ImportedProfile> Load() => Items;
        public void Save(IEnumerable<ImportedProfile> profiles) { if (FailSave) throw new IOException(); Items = profiles.ToList(); }
    }
    [Fact] public void SwitchingGroupSelectsOnlyItsServers()
    {
        var vm = new MainViewModel(null, new Store());
        vm.SelectedGroup = vm.Groups.Single(group => group.Id == "a");
        Assert.Equal(2, vm.Profiles.Count);
        vm.SelectedGroup = vm.Groups.Single(group => group.Id == "b");
        Assert.Equal("B1", Assert.Single(vm.Profiles).Name);
        Assert.Equal("B1", vm.SelectedProfile!.Name);
    }
    [Fact] public void RemovingSubscriptionKeepsOtherSubscriptionsAndPersists()
    {
        var store = new Store();
        var vm = new MainViewModel(null, store);
        vm.SelectedGroup = vm.Groups.Single(group => group.Id == "a");
        vm.RemoveGroupCommand.Execute(null);
        Assert.Equal("B1", Assert.Single(store.Items).Name);
        var reloaded = new MainViewModel(null, store);
        Assert.Equal("Subscription B", Assert.Single(reloaded.Groups).Name);
    }
    [Fact] public void FailedSaveDoesNotRemoveAnyServers()
    {
        var store = new Store { FailSave = true };
        var vm = new MainViewModel(null, store);
        vm.SelectedGroup = vm.Groups.Single(group => group.Id == "a");
        vm.RemoveGroupCommand.Execute(null);
        Assert.Equal(3, store.Items.Count);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal(2, vm.Groups.Count);
    }
    [Fact] public void RemovingLastServerRemovesEmptyGroup()
    {
        var vm = new MainViewModel(null, new Store());
        vm.SelectedGroup = vm.Groups[1];
        vm.RemoveProfileCommand.Execute(null);
        Assert.Single(vm.Groups);
        Assert.Equal(2, vm.Profiles.Count);
    }
    [Fact] public void LegacyRecordsHaveMigrationGroup()
    {
        var profile = System.Text.Json.JsonSerializer.Deserialize<ImportedProfile>("{\"Name\":\"Legacy\",\"Kind\":\"SS\",\"Content\":\"legacy\"}")!;
        Assert.Equal("legacy", profile.SourceId);
        Assert.Equal("Ранее импортированные", profile.SourceName);
    }
}
