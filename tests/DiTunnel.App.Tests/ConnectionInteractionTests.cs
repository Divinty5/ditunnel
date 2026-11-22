using System.Collections.Specialized;
using Avalonia;
using DiTunnel.App.Controls;
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
        public VpnStatus Status { get; set; } = VpnStatus.Disconnected;
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
        { Calls++; return Task.FromResult(ProbePolicy.Success(42, "HTTPS", "HTTPS", true)); }
    }
    [Fact]
    public async Task SuccessfulProfileProbeDoesNotOverwriteTunnelWarning()
    {
        const string warning = "Туннель активен. Сервер доступен, но контрольный запрос через TUN не выполнен.";
        var engine = new Engine { Status = new(VpnConnectionState.Connected, warning) };
        var probe = new Probe();
        var vm = new MainViewModel(engine, new Store(), probe);
        try
        {
            vm.Notice = warning;
            vm.ConnectionState = VpnConnectionState.Connected;
            await vm.CheckActiveConnectionAsync(CancellationToken.None);
            Assert.True(probe.Calls > 0);
            Assert.Equal(42d, vm.Profiles[0].ProbeMilliseconds);
            Assert.Equal(warning, vm.Notice);
            Assert.Equal(warning, vm.DiagnosticText);
        }
        finally { await vm.ShutdownAsync(); }
    }
    [Theory]
    [InlineData(ReleaseTarget.WindowsX64, "Windows TUN · Xray-core")]
    [InlineData(ReleaseTarget.AndroidArm64, "Android VPN · Xray-core")]
    [InlineData(ReleaseTarget.LinuxX64, "Linux TUN · Xray-core")]
    public async Task ProbeUpdatesRowAndReenablesControls(ReleaseTarget target, string expectedHint)
    {
        var probe = new Probe();
        var platform = target switch { ReleaseTarget.WindowsX64 => AppPlatform.Windows, ReleaseTarget.AndroidArm64 => AppPlatform.Android, _ => AppPlatform.Linux };
        var vm = new MainViewModel(null, new Store(), probe, platform: platform);
        await vm.ProbeAllCommand.ExecuteAsync(null);
        Assert.Equal(1, probe.Calls);
        Assert.Equal("HTTPS · 42 мс", vm.Profiles[0].ProbeText);
        Assert.False(vm.IsProbing);
        Assert.True(vm.CanImport);
        vm.ConnectionState = VpnConnectionState.Connected;
        Assert.Equal(expectedHint, vm.ConnectionHint);
    }
    private sealed class SortingProbe : IServerProbe
    {
        public TaskCompletionSource? HoldSuccessfulChecks { get; set; }

        public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        {
            if (profile.Name == "B") return new(null, "Тайм-аут");
            var hold = HoldSuccessfulChecks;
            if (hold is not null) await hold.Task.WaitAsync(cancellationToken);
            var slow = hold is null ? profile.Name == "A" : profile.Name == "C";
            return ProbePolicy.Success(slow ? 180 : 60, "HTTPS", "HTTPS", true);
        }
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
        var vm = new MainViewModel(null, new SortingStore(), new SortingProbe(),
            selectionSettings: new UserSettings(), persistSelection: () => { });
        try
        {
            var selected = vm.SelectedProfile;
            var changes = new List<NotifyCollectionChangedAction>();
            vm.Profiles.CollectionChanged += (_, change) => changes.Add(change.Action);

            await vm.ProbeAllCommand.ExecuteAsync(null);
            Assert.Equal(["C", "A", "B"], vm.Profiles.Select(profile => profile.Name));
            Assert.Equal([NotifyCollectionChangedAction.Move], changes);
            Assert.Same(selected, vm.SelectedProfile);

            changes.Clear();
            await vm.ProbeAllCommand.ExecuteAsync(null);
            Assert.Empty(changes);
        }
        finally { await vm.ShutdownAsync(); }
    }
    [Fact]
    public void BulkSortPublishesTheFinalOrderAndLookupInOneNotification()
    {
        var store = new GroupedStore
        {
            Items = Enumerable.Range(0, 1000)
                .Select(index => new ImportedProfile($"Profile {index:D4}", "VLESS", $"synthetic-{index % 999}", "a", "Subscription A"))
                .ToList()
        };
        var catalog = new ProfileCatalog(store);
        catalog.Load();
        var original = catalog.ShowGroup("a", profile => new ServerItemViewModel(profile));
        for (var index = 0; index < original.Length; index++) original[index].ProbeMilliseconds = original.Length - index;
        var notifications = 0;
        catalog.VisibleProfiles.CollectionChanged += (_, change) =>
        {
            notifications++;
            Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action);
            Assert.Equal(original.Reverse(), catalog.VisibleProfiles);
            Assert.Equal([original[^1], original[0]], catalog.FindVisibleRows(store.Items[0]));
        };

        Assert.True(catalog.SortByProbe());
        Assert.False(catalog.SortByProbe());
        Assert.Equal(1, notifications);
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
        Assert.Equal(["C", "A"], vm.Profiles.Select(profile => profile.Name));
    }

    [Fact]
    public async Task FilteringDuringProbePreservesRowOrderThroughCompletion()
    {
        var probe = new SortingProbe();
        var vm = new MainViewModel(null, new SortingStore(), probe, selectionSettings: new UserSettings(), persistSelection: () => { });
        try
        {
            await vm.ProbeAllCommand.ExecuteAsync(null);
            var retained = vm.Profiles.Take(2).ToArray();
            Assert.Equal(["C", "A"], retained.Select(row => row.Name));
            vm.SelectedProfile = retained[1];

            probe.HoldSuccessfulChecks = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var checking = vm.ProbeAllCommand.ExecuteAsync(null);
            await WaitForAsync(() => vm.CanRemoveUnavailable);
            vm.RemoveUnavailableCommand.Execute(null);
            Assert.Equal(retained, vm.Profiles.ToArray());
            Assert.Same(retained[1], vm.SelectedProfile);

            probe.HoldSuccessfulChecks.SetResult();
            await checking;
            Assert.Equal(retained, vm.Profiles.ToArray());
            Assert.Equal([180d, 60d], vm.Profiles.Select(row => row.ProbeMilliseconds!.Value));

            await vm.ProbeAllCommand.ExecuteAsync(null);
            Assert.Equal(["A", "C"], vm.Profiles.Select(row => row.Name));
        }
        finally { await vm.ShutdownAsync(); }
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
        public bool HoldSuccessfulProbe { get; set; } = true;
        public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        {
            if (profile.Name == "B2") return new(null, "Таймаут");
            try { if (HoldSuccessfulProbe) await ReleaseSuccessfulProbe.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { SuccessfulProbeWasCancelled = true; throw; }
            return new(48, "HTTP · 48 мс");
        }
    }

    [Fact]
    public async Task ProbeResultRemainsWithItsSourceAfterChangingGroups()
    {
        var store = new GroupedStore();
        store.Items[1] = store.Items[1] with { Content = store.Items[0].Content };
        var probe = new ProgressiveProbe();
        var vm = new MainViewModel(null, store, probe, selectionSettings: new UserSettings(), persistSelection: () => { });
        try
        {
            var first = vm.Groups.Single(group => group.Id == "a");
            vm.SelectedGroup = first;
            var checking = vm.ProbeAllCommand.ExecuteAsync(null);
            vm.SelectedGroup = vm.Groups.Single(group => group.Id == "b");
            var other = vm.Profiles.Single(row => row.Name == "B1");

            probe.ReleaseSuccessfulProbe.SetResult();
            await checking.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Same(other, vm.SelectedProfile);
            Assert.Null(other.ProbeMilliseconds);
            Assert.False(other.IsProbePending);
            vm.SelectedGroup = first;
            Assert.Equal(48d, Assert.Single(vm.Profiles).ProbeMilliseconds);
        }
        finally { await vm.ShutdownAsync(); }
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
        Assert.Equal("Проверяем…", Assert.Single(vm.Profiles).ProbeDisplayText);
        probe.ReleaseSuccessfulProbe.SetResult();
        await checking;

        Assert.False(probe.SuccessfulProbeWasCancelled);
        var remaining = Assert.Single(vm.Profiles);
        Assert.Equal("B1", remaining.Name);
        Assert.Equal(48, remaining.ProbeMilliseconds);
        Assert.Equal("b", vm.SelectedGroup?.Id);
    }

    [Fact]
    public async Task CancellingProbeRestoresPreviousResultAfterFilteringRebuildsRows()
    {
        var probe = new ProgressiveProbe { HoldSuccessfulProbe = false };
        var vm = new MainViewModel(null, new GroupedStore(), probe);
        try
        {
            vm.SelectedGroup = vm.Groups.Single(group => group.Id == "b");
            await vm.ProbeAllCommand.ExecuteAsync(null);
            probe.HoldSuccessfulProbe = true;

            var checking = vm.ProbeAllCommand.ExecuteAsync(null);
            await WaitForAsync(() => vm.CanRemoveUnavailable);
            vm.RemoveUnavailableCommand.Execute(null);
            Assert.Equal("Проверяем…", Assert.Single(vm.Profiles).ProbeDisplayText);
            vm.CancelProbesCommand.Execute(null);
            await checking.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(probe.SuccessfulProbeWasCancelled);
            Assert.Equal("48 мс · HTTP", Assert.Single(vm.Profiles).ProbeDisplayText);
        }
        finally { await vm.ShutdownAsync(); }
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

    [Fact]
    public async Task ProgressiveBatchShowsCompletedProfileBeforeSlowPeer()
    {
        var probe = new ProgressiveBatchProbe();
        var vm = new MainViewModel(null, new GroupedStore(), probe, platform: AppPlatform.Android);
        try
        {
            vm.SelectedGroup = vm.Groups.Single(group => group.Id == "b");
            var checking = vm.ProbeAllCommand.ExecuteAsync(null);
            await WaitForAsync(() => vm.Profiles.Any(row => row.Name == "B1" && row.ProbeMilliseconds == 30));
            Assert.Equal("Проверяем…", vm.Profiles.Single(row => row.Name == "B2").ProbeDisplayText);
            Assert.False(checking.IsCompleted);
            probe.Release.SetResult();
            await checking.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(vm.Profiles, row => Assert.Equal(30, row.ProbeMilliseconds));
        }
        finally { probe.Release.TrySetResult(); await vm.ShutdownAsync(); }
    }

    private sealed class ProgressiveBatchProbe : IProgressiveServerProbe
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles,
            CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) => throw new NotSupportedException();

        public async Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles,
            IProgress<(int Index, ServerProbeResult Result)> progress, CancellationToken cancellationToken, ServerProbeMode mode)
        {
            var result = new ServerProbeResult(30, "HTTP · 30 мс");
            progress.Report((0, result));
            await Release.Task.WaitAsync(cancellationToken);
            return [result, result];
        }
    }
    [Fact]
    public async Task CancelledProgressiveProbeCannotOverwriteNewerResults()
    {
        var probe = new ControlledProgressiveProbe();
        var vm = new MainViewModel(null, new Store(), probe);
        try
        {
            var baseline = StartProbe();
            probe.Current.Complete(42);
            await baseline.WaitAsync(TimeSpan.FromSeconds(3));

            // The cancel command can be invoked as soon as the UI sees IsProbing.
            vm.PropertyChanged += CancelOnStart;
            var cancelled = StartProbe();
            vm.PropertyChanged -= CancelOnStart;
            var obsolete = probe.Current;
            Assert.True(obsolete.Token.IsCancellationRequested);
            obsolete.Report(99);
            obsolete.Complete(99);
            await cancelled.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(42, vm.SelectedProfile!.ProbeMilliseconds);
            Assert.Equal("Проверка отменена.", vm.DiagnosticText);

            var next = StartProbe();
            probe.Current.Complete(16);
            await next.WaitAsync(TimeSpan.FromSeconds(3));
            obsolete.Report(100);

            Assert.Equal(16, vm.SelectedProfile!.ProbeMilliseconds);
            Assert.Contains("Длительность: 16 мс", vm.DiagnosticText);
        }
        finally { await vm.ShutdownAsync(); }

        Task StartProbe()
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new InlineProgressContext());
            try { return vm.ProbeSelectedCommand.ExecuteAsync(null); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        void CancelOnStart(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainViewModel.IsProbing) && vm.IsProbing)
                vm.CancelProbesCommand.Execute(null);
        }
    }

    private sealed class InlineProgressContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class ControlledProgressiveProbe : IProgressiveServerProbe
    {
        public Attempt Current { get; private set; } = null!;

        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile,
            CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles,
            CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles,
            IProgress<(int Index, ServerProbeResult Result)> progress, CancellationToken cancellationToken, ServerProbeMode mode)
        {
            Current = new(progress, cancellationToken);
            return Current.Results.Task;
        }

        public sealed class Attempt(IProgress<(int Index, ServerProbeResult Result)> progress, CancellationToken token)
        {
            public CancellationToken Token => token;
            public TaskCompletionSource<IReadOnlyList<ServerProbeResult>> Results { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Report(double milliseconds) => progress.Report((0, Result(milliseconds)));

            public void Complete(double milliseconds) => Results.SetResult([Result(milliseconds)]);

            private static ServerProbeResult Result(double milliseconds) => ProbePolicy.Success(milliseconds, "HTTPS", "HTTPS", true);
        }
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
        public System.Collections.Concurrent.ConcurrentQueue<string> ConnectedProfiles { get; } = new();
        public int Disconnects { get; private set; }
        public Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
        {
            ConnectedProfiles.Enqueue(profile.Name);
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

    [Fact]
    public async Task PowerButtonReplacesNoticesFromPreviousConnection()
    {
        var engine = new SwitchingEngine();
        var vm = new MainViewModel(engine, new Store(), platform: AppPlatform.Linux);
        try
        {
            vm.Notice = "Не удалось подключить VPN.";
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
            Assert.Equal("VPN подключён", vm.Notice);

            vm.Notice = "Туннель активен, но контрольный запрос не выполнен.";
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.Equal(VpnConnectionState.Disconnected, vm.ConnectionState);
            Assert.Equal("VPN отключён.", vm.Notice);
        }
        finally { await vm.ShutdownAsync(); }
    }
    [Theory]
    [InlineData(ReleaseTarget.AndroidArm64)]
    [InlineData(ReleaseTarget.WindowsX64)]
    [InlineData(ReleaseTarget.LinuxX64)]
    public async Task BrowsingPreservesActiveVpnAndExplicitActivationSwitchesIt(ReleaseTarget target)
    {
        var engine = new SwitchingEngine();
        var platform = target switch
        {
            ReleaseTarget.AndroidArm64 => AppPlatform.Android,
            ReleaseTarget.LinuxX64 => AppPlatform.Linux,
            _ => AppPlatform.Windows
        };
        var vm = new MainViewModel(engine, new TwoProfileStore(), platform: platform,
            selectionSettings: new(), persistSelection: () => { });
        try
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            var active = vm.Profiles.Single(profile => profile.Name == "A");
            var selected = vm.Profiles.Single(profile => profile.Name == "B");
            vm.SelectedProfile = selected;
            Assert.Equal("A", vm.SelectedName);
            Assert.Equal(["A"], engine.ConnectedProfiles);
            Assert.NotNull(active.ActiveConnectionBrush);
            Assert.Null(selected.ActiveConnectionBrush);
            await vm.ActivateProfileFromUserAsync(selected);
            await WaitForAsync(() => engine.ConnectedProfiles.Count == 2 && !vm.IsConnecting).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["A", "B"], engine.ConnectedProfiles.ToArray());
            Assert.Equal(1, engine.Disconnects);
            Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
            Assert.Null(active.ActiveConnectionBrush);
            Assert.NotNull(selected.ActiveConnectionBrush);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    public async Task ConnectionRefreshOnlyChangesHighlightsWhenTheActiveServerChanges(int profileCount)
    {
        var store = new GroupedStore
        {
            Items = Enumerable.Range(0, profileCount)
                .Select(index => new ImportedProfile($"Server {index:D3}", "VLESS", $"test-{index}", "sub", "Subscription"))
                .ToList()
        };
        var vm = new MainViewModel(null, store, platform: AppPlatform.Windows, selectionSettings: new(), persistSelection: () => { });
        try
        {
            var first = vm.Profiles[0];
            var second = vm.Profiles[1];
            var connected = new VpnStatus(VpnConnectionState.Connected);
            vm.ApplyEngineStatus(connected, first.Profile);
            var brush = Assert.IsAssignableFrom<Avalonia.Media.IBrush>(first.ActiveConnectionBrush);
            var changes = new List<ServerItemViewModel>();
            foreach (var row in vm.Profiles)
                row.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName == nameof(ServerItemViewModel.ActiveConnectionBrush)) changes.Add(row);
                };

            vm.RefreshSubscriptionPresentation();
            Assert.Empty(changes);
            Assert.Same(brush, first.ActiveConnectionBrush);

            vm.ApplyEngineStatus(connected, second.Profile);
            Assert.Equal([first, second], changes);
            Assert.Null(first.ActiveConnectionBrush);
            Assert.NotNull(second.ActiveConnectionBrush);

            changes.Clear();
            vm.ApplyEngineStatus(VpnStatus.Disconnected, null);
            Assert.Equal([second], changes);
            Assert.Null(second.ActiveConnectionBrush);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task MatchingRowsRestoreTheConnectionResultAndHighlightAfterBrowsingAnotherSource()
    {
        var store = new GroupedStore();
        var profile = store.Items[0];
        store.Items.Insert(1, profile with { Name = "A2" });
        store.Items[2] = store.Items[2] with { Content = profile.Content };
        var vm = new MainViewModel(null, store, platform: AppPlatform.Windows, selectionSettings: new(), persistSelection: () => { });
        try
        {
            var original = vm.Profiles.ToArray();
            var failure = ProbePolicy.Failure(new TimeoutException(), "HTTPS", "HTTPS", 75, "Windows");
            vm.ApplyEngineStatus(new(VpnConnectionState.Error, failure.Message, Probe: failure), profile);
            Assert.Equal(2, vm.UnavailableProfileCount);
            Assert.All(original, row => Assert.NotNull(row.ActiveConnectionBrush));

            vm.SelectedGroup = vm.Groups.Single(group => group.Id == "b");
            Assert.All(original, row => Assert.Null(row.ActiveConnectionBrush));
            Assert.All(vm.Profiles, row =>
            {
                Assert.Null(row.ActiveConnectionBrush);
                Assert.Null(row.ActiveProbeResult);
            });

            vm.SelectedGroup = vm.Groups.Single(group => group.Id == "a");
            Assert.All(vm.Profiles, row =>
            {
                Assert.NotNull(row.ActiveConnectionBrush);
                Assert.Equal("HTTPS · Тайм-аут", row.ProbeDisplayText);
            });
            Assert.Equal(2, vm.UnavailableProfileCount);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task AndroidDoubleTapConnectsTheChosenProfileFromDisconnectedState()
    {
        var engine = new SwitchingEngine();
        var vm = new MainViewModel(engine, new TwoProfileStore(), platform: AppPlatform.Android,
            selectionSettings: new(), persistSelection: () => { });
        try
        {
            var taps = new ProfileTouchTaps();
            var first = vm.Profiles.Single(profile => profile.Name == "A");
            var target = vm.Profiles.Single(profile => profile.Name == "B");
            ServerItemViewModel? Tap(ServerItemViewModel row, int pointer, Point position, ulong timestamp)
            {
                taps.Press(row, pointer, position, timestamp);
                return taps.Release(row, pointer, position, timestamp + 20, new Size(8, 8),
                    new Size(24, 24), TimeSpan.FromMilliseconds(300));
            }
            Assert.Null(Tap(first, 1, new Point(10, 10), 100));
            Assert.Null(Tap(target, 2, new Point(10, 10), 200));
            Assert.Empty(engine.ConnectedProfiles);
            var activated = Tap(target, 3, new Point(22, 12), 340);
            Assert.Same(target, activated);
            await vm.ActivateProfileFromUserAsync(activated!);
            Assert.Equal(["B"], engine.ConnectedProfiles.ToArray());
            Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public void AndroidScrollCancelsTheDoubleTapSequence()
    {
        var taps = new ProfileTouchTaps();
        var row = new ServerItemViewModel(new("A", "VLESS", "vless://synthetic-a"));
        var point = new Point(10, 10);
        var tolerance = new Size(8, 8);
        var interval = TimeSpan.FromMilliseconds(300);
        taps.Press(row, 1, point, 100);
        Assert.Null(taps.Release(row, 1, point, 130, tolerance, new Size(24, 24), interval));
        taps.Press(row, 2, point, 200);
        taps.Move(2, new Point(10, 40), tolerance);
        Assert.Null(taps.Release(row, 2, point, 260, tolerance, new Size(24, 24), interval));
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

    [Fact] public async Task ActivatingProfileAfterFailedConnectionStartsItImmediately()
    {
        var engine = new SwitchingEngine();
        var vm = new MainViewModel(engine, new TwoProfileStore(), selectionSettings: new(), persistSelection: () => { }) { ConnectionState = VpnConnectionState.Error };
        try
        {
            await vm.ActivateProfileFromUserAsync(vm.Profiles.Single(profile => profile.Name == "B"));
            await WaitForAsync(() => engine.ConnectedProfiles.Count == 1 && !vm.IsConnecting).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["B"], engine.ConnectedProfiles.ToArray());
            Assert.Equal(0, engine.Disconnects);
            Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
        }
        finally { await vm.ShutdownAsync(); }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(10);
    }
}
