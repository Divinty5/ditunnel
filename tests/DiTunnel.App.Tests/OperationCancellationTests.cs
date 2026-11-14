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

    private sealed class RecoveryEngine : IProfileVpnEngine
    {
        public VpnStatus Status { get; private set; } = new(VpnConnectionState.Connected);
        public bool RequiresAdministrator => false;
        public List<string> SwitchedServers { get; } = [];
        public int Restarts => SwitchedServers.Count;
        public event EventHandler<VpnStatus>? StatusChanged { add { } remove { } }
        public Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SwitchAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
        {
            SwitchedServers.Add(profile.Name);
            return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Status = VpnStatus.Disconnected;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class HealthProbe : IServerProbe
    {
        public bool Available { get; set; }
        public string FailureKind { get; set; } = "Timeout";
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        {
            return CheckAsync();
        }

        protected Task<ServerProbeResult> CheckAsync()
        {
            return Task.FromResult(Available ? ProbePolicy.Success(15, "HTTPS", "HTTPS", true)
                : ProbePolicy.Failure(new ProbeFailureException("HTTPS", FailureKind), "HTTPS", "HTTPS", 100));
        }
    }

    private sealed class TunnelHealthProbe : HealthProbe, IActiveTunnelProbe
    {
        public bool Deferred { get; set; }
        public Task<ServerProbeResult> ProbeActiveTunnelAsync(CancellationToken cancellationToken = default) =>
            Deferred ? Task.FromResult(ProbePolicy.ActiveTunnelDeferred("Windows")) : CheckAsync();
    }

    private sealed class LowestStore : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() =>
            [new("A", "VLESS", "synthetic-a", "source", "Subscription"), new("B", "VLESS", "synthetic-b", "source", "Subscription")];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }

    [Fact]
    public async Task LowestUsesAlternativeOnlyAfterRestartingCurrentServer()
    {
        var engine = new RecoveryEngine();
        var probe = new TunnelHealthProbe { Available = true };
        var settings = new UserSettings { LowestMode = true };
        var vm = new MainViewModel(engine, new LowestStore(), probe, platform: AppPlatform.Android,
            selectionSettings: settings, persistSelection: () => { });
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(row => row.Name == "B");
            await vm.ProbeSelectedCommand.ExecuteAsync(null);
            Assert.Equal(15, vm.SelectedProfile.ProbeMilliseconds);
            vm.SelectedProfile = vm.Profiles.Single(row => row.Name == "A");
            probe.Available = false;

            for (var attempt = 0; attempt < 2; attempt++)
                await vm.CheckActiveConnectionAsync(default);
            Assert.Equal(["A"], engine.SwitchedServers);

            for (var attempt = 0; attempt < 3; attempt++)
                await vm.CheckActiveConnectionAsync(default);
            Assert.Equal(["A", "B"], engine.SwitchedServers);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(ReleaseTarget.WindowsX64)]
    [InlineData(ReleaseTarget.LinuxX64)]
    [InlineData(ReleaseTarget.AndroidArm64)]
    public async Task FailedActiveProbeChangesStatusAndRestartsAtMostOncePerOutage(ReleaseTarget target)
    {
        var engine = new RecoveryEngine();
        var probe = new TunnelHealthProbe();
        var platform = target == ReleaseTarget.LinuxX64 ? AppPlatform.Linux
            : target == ReleaseTarget.AndroidArm64 ? AppPlatform.Android : AppPlatform.Windows;
        var vm = new MainViewModel(engine, new Store(), probe, platform: platform);
        try
        {
            await vm.CheckActiveConnectionAsync(default);
            Assert.Equal("Туннель запущен · Проверяем связь", vm.StatusText);
            Assert.Equal(ConnectionPalette.AccentFor(VpnConnectionState.Connecting).ToString(), vm.PowerBorderBrush.ToString());
            Assert.Equal("Отключить", vm.PowerText);
            Assert.Equal(0, engine.Restarts);
            await vm.CheckActiveConnectionAsync(default);
            await vm.CheckActiveConnectionAsync(default);
            await vm.CheckActiveConnectionAsync(default);
            Assert.Equal(1, engine.Restarts);
            probe.Available = true;
            await vm.CheckActiveConnectionAsync(default);
            Assert.Equal("VPN подключён", vm.StatusText);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task TransientTunnelTimeoutDoesNotReplaceProfileResultOrCountAcrossDeferredCheck()
    {
        var engine = new RecoveryEngine();
        var probe = new TunnelHealthProbe { Available = true };
        var vm = new MainViewModel(engine, new Store(), probe, platform: AppPlatform.Windows);
        try
        {
            Assert.Equal("Туннель VPN запущен", vm.StatusText);
            await vm.ProbeSelectedCommand.ExecuteAsync(null);
            await vm.CheckActiveConnectionAsync(default);
            Assert.Equal("VPN подключён", vm.StatusText);
            probe.Available = false;
            await vm.CheckActiveConnectionAsync(default);
            Assert.Equal("Туннель запущен · Проверяем связь", vm.StatusText);
            Assert.Contains("Тип ошибки: Timeout", vm.DiagnosticText);
            Assert.DoesNotContain("Тип ошибки: None", vm.DiagnosticText);
            Assert.Equal(15, vm.SelectedProfile!.ProbeMilliseconds);
            probe.Deferred = true;
            await vm.CheckActiveConnectionAsync(default);
            probe.Deferred = false;
            await vm.CheckActiveConnectionAsync(default);
            Assert.Equal(0, engine.Restarts);
            probe.Available = true;
            await vm.CheckActiveConnectionAsync(default);
            Assert.Equal("VPN подключён", vm.StatusText);
            Assert.Contains("Тип ошибки: None", vm.DiagnosticText);
            Assert.DoesNotContain("Тайм-аут", vm.DiagnosticText);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(false, "Timeout")]
    [InlineData(true, "ControlService")]
    public async Task ProfileTimeoutOrControlSiteErrorNeverRestartsTunnel(bool throughTunnel, string kind)
    {
        var engine = new RecoveryEngine();
        HealthProbe probe = throughTunnel ? new TunnelHealthProbe() : new HealthProbe();
        probe.FailureKind = kind;
        var vm = new MainViewModel(engine, new Store(), probe, platform: AppPlatform.Windows);
        try
        {
            await vm.CheckActiveConnectionAsync(default);
            await vm.CheckActiveConnectionAsync(default);
            Assert.Equal(0, engine.Restarts);
            Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
            Assert.Equal(ConnectionPalette.AccentFor(VpnConnectionState.Connecting).ToString(), vm.PowerBorderBrush.ToString());
        }
        finally { await vm.ShutdownAsync(); }
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
        var vm = new MainViewModel(engine, new Store(), new Probe());
        var probeEnabled = new List<bool>();
        var probeAllEnabled = new List<bool>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.CanProbe)) probeEnabled.Add(vm.CanProbe);
            if (args.PropertyName == nameof(vm.CanProbeAll)) probeAllEnabled.Add(vm.CanProbeAll);
        };
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
            Assert.True(probeEnabled.Last());
            Assert.True(probeAllEnabled.Last());
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
            Assert.True(vm.CanProbe);
            Assert.True(vm.CanProbeAll);
        }
        finally { await vm.ShutdownAsync(); }
    }
}
