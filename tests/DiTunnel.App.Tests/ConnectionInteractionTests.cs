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
        public bool IsNetworkProtectionActive => Status.State == VpnConnectionState.Connected;
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
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
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
        vm.ConnectionState = VpnConnectionState.Connected;
        Assert.Equal("Windows TUN · Xray-core", vm.ConnectionHint);
    }
    private sealed class SortingProbe : IServerProbe
    {
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) =>
            Task.FromResult(profile.Name switch
            {
                "A" => new ServerProbeResult(180, "HTTPS · 180 мс"),
                "C" => new ServerProbeResult(60, "HTTPS · 60 мс"),
                _ => new ServerProbeResult(null, "Тайм-аут")
            });
    }
    private sealed class SortingStore : IProfileStore
    {
        public List<ImportedProfile> Items { get; set; } =
        [
            new("B", "Hysteria 2", "hy2://b@192.0.2.2:443"),
            new("C", "Hysteria 2", "hy2://c@192.0.2.3:443"),
            new("A", "Hysteria 2", "hy2://a@192.0.2.1:443")
        ];
        public IReadOnlyList<ImportedProfile> Load() => Items;
        public void Save(IEnumerable<ImportedProfile> profiles) => Items = profiles.ToList();
    }
    [Fact] public async Task ProbeSortsSuccessfulServersBeforeTimeouts()
    {
        var vm = new MainViewModel(null, new SortingStore(), new SortingProbe());
        await vm.ProbeAllCommand.ExecuteAsync(null);
        Assert.Equal(["C", "A", "B"], vm.Profiles.Select(profile => profile.Name));
    }
    [Fact]
    public async Task FilteringRemovesOnlyServersThatFailedAProbe()
    {
        var store = new SortingStore();
        var vm = new MainViewModel(null, store, new SortingProbe());
        await vm.ProbeAllCommand.ExecuteAsync(null);
        Assert.True(vm.CanRemoveUnavailable);

        vm.RemoveUnavailableCommand.Execute(null);

        Assert.Equal(["A", "C"], store.Items.Select(profile => profile.Name).Order());
        Assert.DoesNotContain(vm.Profiles, profile => profile.Name == "B");
    }

    private sealed class GroupedStore : IProfileStore
    {
        public List<ImportedProfile> Items { get; set; } =
        [
            new("A1", "VLESS", "vless://a", "a", "Subscription A"),
            new("B1", "VLESS", "vless://b1", "b", "Subscription B"),
            new("B2", "VLESS", "vless://b2", "b", "Subscription B")
        ];
        public IReadOnlyList<ImportedProfile> Load() => Items;
        public void Save(IEnumerable<ImportedProfile> profiles) => Items = profiles.ToList();
    }

    private sealed class ProgressiveProbe : IServerProbe
    {
        public TaskCompletionSource ReleaseSuccessfulProbe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SuccessfulProbeWasCancelled { get; private set; }
        public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        {
            if (profile.Name == "B2") return new(null, "Таймаут");
            try { await ReleaseSuccessfulProbe.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { SuccessfulProbeWasCancelled = true; throw; }
            return new(48, "HTTP · 48 мс");
        }
    }

    [Fact]
    public async Task FilteringDuringProbeKeepsSelectionResultsAndRemainingChecks()
    {
        var store = new GroupedStore();
        var probe = new ProgressiveProbe();
        var vm = new MainViewModel(null, store, probe);
        vm.SelectedGroup = vm.Groups.Single(group => group.Id == "b");

        var checking = vm.ProbeAllCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.CanRemoveUnavailable);
        vm.RemoveUnavailableCommand.Execute(null);

        Assert.Equal("b", vm.SelectedGroup?.Id);
        Assert.True(vm.IsProbing);
        probe.ReleaseSuccessfulProbe.SetResult();
        await checking;

        Assert.False(probe.SuccessfulProbeWasCancelled);
        var remaining = Assert.Single(vm.Profiles);
        Assert.Equal("B1", remaining.Name);
        Assert.Equal(48, remaining.ProbeMilliseconds);
        Assert.Equal("b", vm.SelectedGroup?.Id);
    }

    private sealed class BatchProbe : IServerBatchProbe
    {
        public List<int> BatchSizes { get; } = [];
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) =>
            throw new InvalidOperationException("The batch path should be used.");
        public Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        {
            BatchSizes.Add(profiles.Count);
            return Task.FromResult<IReadOnlyList<ServerProbeResult>>(profiles.Select(_ => new ServerProbeResult(30, "HTTP · 30 мс")).ToArray());
        }
    }

    [Fact]
    public async Task BatchProbeRunsInGroupsOfAtMostFive()
    {
        var store = new GroupedStore();
        store.Items = Enumerable.Range(1, 12)
            .Select(index => new ImportedProfile($"P{index}", "VLESS", $"vless://p{index}", "batch", "Batch"))
            .ToList();
        var probe = new BatchProbe();
        var vm = new MainViewModel(null, store, probe);

        await vm.ProbeAllCommand.ExecuteAsync(null);

        Assert.Equal([5, 5, 2], probe.BatchSizes);
        Assert.All(vm.Profiles, profile => Assert.Equal(30, profile.ProbeMilliseconds));
    }
    [Fact] public async Task ProbeAllRemainsAvailableWhileVpnIsConnected()
    {
        var probe = new Probe();
        var vm = new MainViewModel(new Engine(), new Store(), probe) { ConnectionState = VpnConnectionState.Connected };
        Assert.True(vm.CanProbe);
        Assert.True(vm.CanProbeAll);
        Assert.True(vm.CanImport);
        await vm.ProbeAllCommand.ExecuteAsync(null);
        Assert.Equal(1, probe.Calls);
    }

    [Fact] public void CopyErrorButtonIsVisibleOnlyForConnectionError()
    {
        var vm = new MainViewModel(new Engine(), new Store());
        Assert.False(vm.HasConnectionError);
        vm.Notice = "Сбой настройки Windows.";
        vm.ConnectionState = VpnConnectionState.Error;
        Assert.True(vm.HasConnectionError);
        Assert.Equal("⧉", vm.CopyErrorText);
    }

    private sealed class SwitchingEngine : IProfileVpnEngine
    {
        public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
        public event EventHandler<VpnStatus>? StatusChanged;
        public bool RequiresAdministrator => false;
        public bool IsNetworkProtectionActive => Status.State == VpnConnectionState.Connected;
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

        vm.SelectProfileFromUser(vm.Profiles.Single(profile => profile.Name == "B"));
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), WaitForAsync(() => engine.ConnectedProfiles.Count == 2));

        Assert.Equal(["A", "B"], engine.ConnectedProfiles);
        Assert.Equal(1, engine.Disconnects);
        Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
    }

    [Fact] public async Task ApplyingNetworkSettingsReconnectsActiveVpn()
    {
        var engine = new SwitchingEngine();
        var vm = new MainViewModel(engine, new Store());
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.ApplyNetworkSettingsAsync();

        Assert.Equal(["HY2", "HY2"], engine.ConnectedProfiles);
        Assert.Equal(1, engine.Disconnects);
        Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
    }

    [Theory]
    [InlineData(VpnConnectionState.Error)]
    [InlineData(VpnConnectionState.Reconnecting)]
    [InlineData(VpnConnectionState.Connecting)]
    public void ImportCanBeOpenedWhileConnectionNeedsAttention(VpnConnectionState state)
    {
        var vm = new MainViewModel(new Engine(), new Store()) { ConnectionState = state };
        Assert.True(vm.CanImport);
        vm.OpenImportCommand.Execute(null);
        Assert.True(vm.IsImportOpen);
    }

    [Fact] public async Task SelectingProfileAfterFailedConnectionStartsItImmediately()
    {
        var engine = new SwitchingEngine();
        var vm = new MainViewModel(engine, new TwoProfileStore()) { ConnectionState = VpnConnectionState.Error };

        vm.SelectProfileFromUser(vm.Profiles.Single(profile => profile.Name == "B"));
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)), WaitForAsync(() => engine.ConnectedProfiles.Count == 1));

        Assert.Equal(["B"], engine.ConnectedProfiles);
        Assert.Equal(0, engine.Disconnects);
        Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(10);
    }
}
