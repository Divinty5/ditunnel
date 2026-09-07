using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class ConnectionInteractionTests
{
    private sealed class Store : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => [new("HY2", "Hysteria 2", "hy2://test@192.0.2.1:443")];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }
    private sealed class Engine : IProfileVpnEngine
    {
        public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
        public event EventHandler<VpnStatus>? StatusChanged { add { } remove { } }
        public bool RequiresAdministrator => false;
        public bool FailCleanup;
        public bool HangCleanup;
        public bool Cancelled;
        public async Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
        {
            Status = new(VpnConnectionState.Connecting);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; Status = VpnStatus.Disconnected; throw; }
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => HangCleanup ? new TaskCompletionSource().Task : FailCleanup ? Task.FromException(new IOException("test")) : Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact] public async Task PowerButtonCanCancelInFlightConnection()
    {
        var engine = new Engine();
        var vm = new MainViewModel(engine, new Store());
        var connecting = vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnecting);
        Assert.True(vm.CanConnect);
        Assert.Equal("Отменить", vm.PowerText);
        await vm.ConnectCommand.ExecuteAsync(null);
        await connecting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(engine.Cancelled);
        Assert.False(vm.IsConnecting);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FailedOrHungCleanupDoesNotPreventClosing(bool fail, bool hang)
    {
        var vm = new MainViewModel(new Engine { FailCleanup = fail, HangCleanup = hang }, new Store());
        await vm.PrepareToCloseAsync(TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(2));
    }
    private sealed class Probe : IServerProbe
    {
        public int Calls;
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ServerProbeResult(42, "HTTPS · 42 мс")); }
    }
    [Fact] public async Task ProbeUpdatesRowAndReenablesControls()
    {
        var probe = new Probe();
        var vm = new MainViewModel(null, new Store(), probe);
        await vm.ProbeAllCommand.ExecuteAsync(null);
        Assert.Equal(1, probe.Calls);
        Assert.Equal("HTTPS · 42 мс", vm.Profiles[0].ProbeText);
        Assert.False(vm.IsProbing);
        Assert.True(vm.CanImport);
    }
    private sealed class SortingProbe : IServerProbe
    {
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(profile.Name switch
            {
                "A" => new ServerProbeResult(180, "HTTPS · 180 мс"),
                "C" => new ServerProbeResult(60, "HTTPS · 60 мс"),
                _ => new ServerProbeResult(null, "Тайм-аут")
            });
    }
    private sealed class SortingStore : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() =>
        [
            new("B", "Hysteria 2", "hy2://b@192.0.2.2:443"),
            new("C", "Hysteria 2", "hy2://c@192.0.2.3:443"),
            new("A", "Hysteria 2", "hy2://a@192.0.2.1:443")
        ];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }
    [Fact] public async Task ProbeSortsSuccessfulServersBeforeTimeouts()
    {
        var vm = new MainViewModel(null, new SortingStore(), new SortingProbe());
        await vm.ProbeAllCommand.ExecuteAsync(null);
        Assert.Equal(["C", "A", "B"], vm.Profiles.Select(profile => profile.Name));
    }
    [Fact] public async Task ProbeAllRemainsAvailableWhileVpnIsConnected()
    {
        var probe = new Probe();
        var vm = new MainViewModel(new Engine(), new Store(), probe) { ConnectionState = VpnConnectionState.Connected };
        Assert.True(vm.CanProbe);
        Assert.True(vm.CanProbeAll);
        Assert.False(vm.CanImport);
        await vm.ProbeAllCommand.ExecuteAsync(null);
        Assert.Equal(1, probe.Calls);
    }

    private sealed class SwitchingEngine : IProfileVpnEngine
    {
        public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
        public event EventHandler<VpnStatus>? StatusChanged;
        public bool RequiresAdministrator => false;
        public List<string> ConnectedProfiles { get; } = [];
        public int Disconnects { get; private set; }
        public Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
        {
            ConnectedProfiles.Add(profile.Name);
            Status = new(VpnConnectionState.Connected);
            StatusChanged?.Invoke(this, Status);
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Disconnects++;
            Status = VpnStatus.Disconnected;
            StatusChanged?.Invoke(this, Status);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class TwoProfileStore : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() =>
        [
            new("A", "Hysteria 2", "hy2://a@192.0.2.1:443"),
            new("B", "Hysteria 2", "hy2://b@192.0.2.2:443")
        ];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }
    [Fact] public async Task SelectingAnotherProfileReconnectsActiveVpn()
    {
        var engine = new SwitchingEngine();
        var vm = new MainViewModel(engine, new TwoProfileStore());
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.SelectedProfile = vm.Profiles.Single(profile => profile.Name == "B");
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), WaitForAsync(() => engine.ConnectedProfiles.Count == 2));

        Assert.Equal(["A", "B"], engine.ConnectedProfiles);
        Assert.Equal(1, engine.Disconnects);
        Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(10);
    }
}
