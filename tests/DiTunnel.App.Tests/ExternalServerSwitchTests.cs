using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class ExternalServerSwitchTests
{
    private sealed class Store : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() =>
        [new("HTTPU", "VLESS", "vless://httpu", "sub", "Subscription"),
         new("HY2", "Hysteria 2", "hy2://hy2", "sub", "Subscription"),
         new("WS", "VLESS", "vless://ws", "sub", "Subscription")];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }
    private sealed class Engine(ImportedProfile profile) : IProfileVpnEngine
    {
        public VpnStatus Status => new(VpnConnectionState.Connected);
        public ImportedProfile? ActiveProfile => profile;
        public bool RequiresAdministrator => false;
        public bool IsNetworkProtectionActive => true;
        public event EventHandler<VpnStatus>? StatusChanged { add { } remove { } }
        public Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact]
    public async Task HeaderNotificationsContainTheNewActiveServerAfterEachExternalSwitch()
    {
        var store = new Store();
        var vm = new MainViewModel(new Engine(store.Load()[0]), store);
        try
        {
            foreach (var name in new[] { "HY2", "WS" })
            {
                var target = vm.Profiles.Single(row => row.Name == name);
                vm.SelectedProfile = target; // Widget selection arrives before the tunnel switch.
                var notifications = new List<string>();
                System.ComponentModel.PropertyChangedEventHandler observe = (_, args) =>
                {
                    if (args.PropertyName == nameof(vm.SelectedName)) notifications.Add(vm.SelectedName);
                };
                vm.PropertyChanged += observe;
                vm.ApplyEngineStatus(new(VpnConnectionState.Reconnecting), null);
                vm.ApplyEngineStatus(new(VpnConnectionState.Connected), target.Profile);
                vm.PropertyChanged -= observe;
                Assert.Equal(name, vm.SelectedName);
                Assert.Equal(name, notifications.Last());
                Assert.DoesNotContain(notifications.SkipWhile(value => value != name), value => value != name);
            }
        }
        finally { await vm.ShutdownAsync(); }
    }
    [Fact]
    public async Task ConnectedRefreshInvalidatesHeaderEvenWithoutAStateTransition()
    {
        var store = new Store();
        var vm = new MainViewModel(new Engine(store.Load()[0]), store);
        try
        {
            var target = vm.Profiles.Single(row => row.Name == "WS");
            var notifications = new List<string>();
            vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.SelectedName)) notifications.Add(vm.SelectedName); };
            vm.ApplyEngineStatus(new(VpnConnectionState.Connected), target.Profile);
            Assert.Equal("WS", vm.SelectedName);
            Assert.Equal("WS", notifications.Last());
        }
        finally { await vm.ShutdownAsync(); }
    }
}
