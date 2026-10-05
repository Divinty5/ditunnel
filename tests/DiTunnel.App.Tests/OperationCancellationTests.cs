using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class OperationCancellationTests
{
    private sealed class Store : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => [new("AWG", "AmneziaWG", "awg", "a", "A"), new("Other", "VLESS", "vless", "b", "B")];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }
    private sealed class Probe : IServerProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(null, "Unexpected");
        }
    }
    private sealed class Engine : IProfileVpnEngine
    {
        public VpnStatus Status { get; set; } = VpnStatus.Disconnected;
        public bool RequiresAdministrator => false;
        public event EventHandler<VpnStatus>? StatusChanged { add { } remove { } }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
        {
            Status = new(VpnConnectionState.Connecting);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { Status = VpnStatus.Disconnected; ended.TrySetResult(); }
        }
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (Started.Task.IsCompleted) await ended.Task.WaitAsync(cancellationToken);
            Status = VpnStatus.Disconnected;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CleaningEngine : IProfileVpnEngine
    {
        public VpnStatus Status { get; private set; } = new(VpnConnectionState.Reconnecting);
        public bool RequiresAdministrator => false;
        public event EventHandler<VpnStatus>? StatusChanged;
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            // The broker can publish Disconnected before its final rollback reply arrives.
            Status = VpnStatus.Disconnected;
            StatusChanged?.Invoke(this, Status);
            CleanupStarted.TrySetResult();
            await FinishCleanup.Task.WaitAsync(cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProtectedEngine : IProfileVpnEngine
    {
        public VpnStatus Status => VpnStatus.Disconnected;
        public bool IsNetworkProtectionActive => true;
        public bool RequiresAdministrator => false;
        public event EventHandler<VpnStatus>? StatusChanged { add { } remove { } }
        public Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task PersistedProtectionIsVisibleWithoutAnActiveTunnel()
    {
        var vm = new MainViewModel(new ProtectedEngine(), new Store());
        try
        {
            Assert.Equal(VpnConnectionState.Disconnected, vm.ConnectionState);
            Assert.True(vm.ShowKillSwitchStatus);
            Assert.Contains("блокирует интернет", vm.KillSwitchText);
            Assert.Contains("восстановите доступ", vm.KillSwitchText);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task CancellationStaysVisibleUntilTheBrokerConfirmsCleanup()
    {
        var engine = new CleaningEngine();
        var vm = new MainViewModel(engine, new Store());
        try
        {
            var cancel = vm.CancelConnectionCommand.ExecuteAsync(null);
            await engine.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(vm.HasPendingOperation);
            Assert.True(vm.HasPendingConnection);
            Assert.False(vm.CanCancelConnection);
            Assert.False(vm.CanConnect);
            Assert.False(vm.CanSelectProfile);
            Assert.Equal("Отменяем подключение…", vm.CancelConnectionText);
            Assert.Equal("Отменяем", vm.PowerText);
            engine.FinishCleanup.TrySetResult();
            await cancel.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(vm.HasPendingOperation);
            Assert.True(vm.CanSelectProfile);
        }
        finally { engine.FinishCleanup.TrySetResult(); await vm.ShutdownAsync(); }
    }
    [Fact]
    public async Task CancelConnectingUnlocksSubscriptionSelection()
    {
        var engine = new Engine();
        var vm = new MainViewModel(engine, new Store());
        try
        {
            var connect = vm.ConnectCommand.ExecuteAsync(null);
            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(vm.CanCancelConnection);
            Assert.False(vm.CanSelectProfile);
            await vm.CancelConnectionCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
            await connect;
            Assert.False(vm.HasPendingConnection);
            Assert.True(vm.CanSelectProfile);
            vm.SelectedGroup = vm.Groups.Single(group => group.Id == "b");
            Assert.Equal("Other", vm.SelectedProfile!.Name);
        }
        finally { await vm.ShutdownAsync(); }
    }
    [Fact]
    public async Task ExternalReconnectExposesCancellationWithoutLocalConnectCommand()
    {
        var engine = new Engine { Status = new(VpnConnectionState.Reconnecting) };
        var vm = new MainViewModel(engine, new Store());
        try
        {
            Assert.False(vm.IsConnecting);
            Assert.True(vm.CanCancelConnection);
            Assert.Equal("Отменить", vm.PowerText);
            await vm.CancelConnectionCommand.ExecuteAsync(null);
            Assert.True(vm.CanSelectProfile);
        }
        finally { await vm.ShutdownAsync(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualAndLowestProbesUseTheVisibleCancelAction(bool automatic)
    {
        var probe = new Probe();
        var vm = new MainViewModel(null, new Store(), probe: probe);
        try
        {
            Task operation = automatic ? vm.CheckLowestAsync(CancellationToken.None) : vm.ProbeAllCommand.ExecuteAsync(null);
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(vm.IsProbing);
            Assert.False(vm.CanSelectProfile);
            vm.CancelProbesCommand.Execute(null);
            await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(vm.IsProbing);
            Assert.True(vm.CanSelectProfile);
        }
        finally { await vm.ShutdownAsync(); }
    }
}
