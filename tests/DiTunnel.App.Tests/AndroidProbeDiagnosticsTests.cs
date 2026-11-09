using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class AndroidProbeDiagnosticsTests
{
    private static ProbeDiagnostics Details(string method, string error = "None", bool confirmed = false) =>
        new(method, method == "TCP" ? "Подключение к серверу" : "HTTPS", error, 123, confirmed, "0.6.6");

    private sealed class Store : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() =>
        [new("A", "VLESS", "vless://synthetic-a"), new("B", "VLESS", "vless://synthetic-b"), new("C", "VLESS", "vless://synthetic-c")];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }

    private sealed class Probe : IServerBatchProbe
    {
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) =>
            Task.FromResult(Result(profile));
        public Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) =>
            Task.FromResult<IReadOnlyList<ServerProbeResult>>(profiles.Select(Result).ToArray());
        private static ServerProbeResult Result(ImportedProfile profile) => profile.Name switch
        {
            "A" => new(10, "TCP · 10 мс · работа профиля не проверена", Diagnostics: Details("TCP")),
            "B" => new(null, "HTTPS: тайм-аут", Diagnostics: Details("HTTPS", "Timeout")),
            _ => new(80, "HTTPS · 80 мс", Diagnostics: Details("HTTPS", confirmed: true))
        };
    }

    private sealed class ThrowingPeerProbe : IServerProbe
    {
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) =>
            profile.Name == "B" ? Task.FromException<ServerProbeResult>(new IOException("synthetic-secret-must-not-leak"))
                : Task.FromResult(new ServerProbeResult(12, "HTTP · 12 мс"));
    }

    [Theory]
    [InlineData(ReleaseTarget.WindowsX64)] [InlineData(ReleaseTarget.AndroidArm64)] [InlineData(ReleaseTarget.LinuxX64)]
    public async Task UnexpectedPeerFailureKeepsOtherResultsAndDoesNotCopyRawException(ReleaseTarget target)
    {
        var platform = target == ReleaseTarget.WindowsX64 ? AppPlatform.Windows
            : target == ReleaseTarget.LinuxX64 ? AppPlatform.Linux : AppPlatform.Android;
        var vm = new MainViewModel(null, new Store(), new ThrowingPeerProbe(), platform: platform);
        try
        {
            await vm.ProbeAllCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.Profiles.Count(row => row.ProbeMilliseconds == 12));
            var failed = vm.Profiles.Single(row => row.Name == "B");
            Assert.True(failed.ProbeTimedOut);
            vm.SelectedProfile = failed;
            Assert.DoesNotContain("synthetic-secret", vm.DiagnosticText);
            Assert.Contains("Connection", vm.DiagnosticText);
            Assert.False(vm.IsProbing);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task DiagnosticsRemainIndependentAndAvailableForCopyAfterSelectingEachRow()
    {
        var vm = new MainViewModel(null, new Store(), new Probe(), platform: AppPlatform.Android);
        try
        {
            await vm.ProbeAllCommand.ExecuteAsync(null);
            Assert.Equal(10, vm.Profiles.Single(row => row.Name == "A").ProbeMilliseconds);
            Assert.Equal("10 мс · TCP", vm.Profiles.Single(row => row.Name == "A").ProbeDisplayText);
            Assert.Equal(LatencyPalette.For(10), vm.Profiles.Single(row => row.Name == "A").ProbeBrush);
            Assert.Null(vm.Profiles.Single(row => row.Name == "B").ProbeMilliseconds);
            Assert.Equal(80, vm.Profiles.Single(row => row.Name == "C").ProbeMilliseconds);
            // User-requested filtering includes failed HTTPS, while the tunnel stays untouched.
            Assert.Equal(1, vm.UnavailableProfileCount);
            Assert.Equal(LatencyPalette.For(null, true), vm.Profiles.Single(row => row.Name == "B").ProbeBrush);
            foreach (var row in vm.Profiles)
            {
                vm.SelectedProfile = row;
                Assert.True(vm.HasConnectionError);
                Assert.Contains("Di-Tunnel Android 0.6.6", vm.DiagnosticText);
                Assert.Contains(row.Name == "A" ? "Метод: TCP" : "Метод: HTTPS", vm.DiagnosticText);
                Assert.Contains(row.Name == "B" ? "Тип ошибки: Timeout" : "Тип ошибки: None", vm.DiagnosticText);
            }
            Assert.Contains("Ошибки: 1", vm.DisplayNotice);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task LowestDoesNotChooseTcpSuccessOverVerifiedProfile()
    {
        var vm = new MainViewModel(null, new Store(), new Probe(), platform: AppPlatform.Android);
        try
        {
            await vm.CheckLowestAsync(CancellationToken.None);
            Assert.Equal("C", vm.SelectedProfile?.Name);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task ManualFilteringRemovesTlsAndTimeoutFailuresAndKeepsSuccessfulPeer()
    {
        var store = new FilteringStore();
        var vm = new MainViewModel(null, store, new FilteringProbe(), platform: AppPlatform.Android);
        try
        {
            await vm.ProbeAllCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.UnavailableProfileCount);
            Assert.True(vm.CanRemoveUnavailable);
            Assert.All(vm.Profiles.Where(row => row.Name != "C"), row => Assert.Equal(LatencyPalette.For(null, true), row.ProbeBrush));
            vm.RemoveUnavailableCommand.Execute(null);
            Assert.Equal("C", Assert.Single(vm.Profiles).Name);
            Assert.Equal("C", Assert.Single(store.Items).Name);
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Fact]
    public async Task MainNoticeHidesSuccessfulDiagnosticFieldsWhileCopyRetainsThem()
    {
        var vm = new MainViewModel(null, new Store(), platform: AppPlatform.Android);
        try
        {
            vm.Notice = "VPN подключён.\n" + Details("HTTPS", confirmed: true).ToDisplayString();
            Assert.Equal("VPN подключён.", vm.DisplayNotice);
            Assert.Contains("Метод: HTTPS", vm.DiagnosticText);
            Assert.Contains("0.6.6", vm.DiagnosticText);
        }
        finally { await vm.ShutdownAsync(); }
    }

    private sealed class FilteringStore : IProfileStore
    {
        public List<ImportedProfile> Items = [new("A", "VLESS", "vless://a"), new("B", "VLESS", "vless://b"), new("C", "VLESS", "vless://c")];
        public IReadOnlyList<ImportedProfile> Load() => Items;
        public void Save(IEnumerable<ImportedProfile> profiles) => Items = profiles.ToList();
    }

    private sealed class FilteringProbe : IServerProbe
    {
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) =>
            Task.FromResult(profile.Name == "C" ? new ServerProbeResult(18, "HTTPS · 18 мс", Diagnostics: Details("HTTPS", confirmed: true))
                : new(null, profile.Name == "A" ? "Ошибка TLS. HTTPS не проверен" : "Тайм-аут. HTTPS не проверен",
                    Diagnostics: Details("HTTPS", profile.Name == "A" ? "Tls" : "Timeout")));
    }

    [Fact]
    public async Task TunnelStartWithoutVerifiedHttpsDoesNotDisplayConnectedStatus()
    {
        var vm = new MainViewModel(null, new Store(), platform: AppPlatform.Android);
        try
        {
            vm.ApplyEngineStatus(new(VpnConnectionState.Connected, "TUN запущен"), vm.SelectedProfile!.Profile);
            Assert.Equal("Туннель VPN запущен", vm.StatusText);
            Assert.False(vm.HasDisplayDelay);
            vm.SelectedProfile!.IsProbeUnconfirmed = true;
            vm.ApplyEngineStatus(new(VpnConnectionState.Connected, "HTTPS подтверждён", DelayMilliseconds: 42), vm.SelectedProfile!.Profile);
            Assert.Equal("VPN подключён", vm.StatusText);
            Assert.False(vm.SelectedProfile.IsProbeUnconfirmed);
            Assert.True(vm.HasDisplayDelay);
            vm.ApplyEngineStatus(new(VpnConnectionState.Connected, "HTTPS не подтверждён"), vm.SelectedProfile!.Profile);
            Assert.Equal("Туннель VPN запущен", vm.StatusText);
            Assert.False(vm.HasDisplayDelay);
        }
        finally { await vm.ShutdownAsync(); }
    }

    private sealed class ActiveProbe : IServerProbe, IActiveTunnelProbe
    {
        public int ProfileCalls, TunnelCalls;
        public Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        {
            ProfileCalls++;
            return Task.FromResult(new ServerProbeResult(1, "TCP", Diagnostics: Details("TCP")));
        }
        public Task<ServerProbeResult> ProbeActiveTunnelAsync(CancellationToken cancellationToken = default)
        {
            TunnelCalls++;
            return Task.FromResult(new ServerProbeResult(null, "HTTPS: тайм-аут", Diagnostics: Details("HTTPS", "Timeout")));
        }
    }

    [Fact]
    public async Task ActiveAndroidChecksUseTunAndKeepWarningCopyableWithoutDisconnecting()
    {
        var probe = new ActiveProbe();
        var vm = new MainViewModel(null, new Store(), probe, platform: AppPlatform.Android);
        try
        {
            vm.ConnectionState = VpnConnectionState.Connected;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (probe.TunnelCalls == 0 || vm.IsProbing) await Task.Delay(20, timeout.Token);
            Assert.Equal(0, probe.ProfileCalls);
            Assert.Equal(VpnConnectionState.Connected, vm.ConnectionState);
            Assert.Contains("Туннель запущен", vm.DisplayNotice);
            Assert.DoesNotContain("Di-Tunnel Android", vm.DisplayNotice);
            Assert.DoesNotContain("Метод:", vm.DisplayNotice);
            Assert.Contains("Тип ошибки: Timeout", vm.DiagnosticText);
            Assert.True(vm.HasConnectionError);
        }
        finally { await vm.ShutdownAsync(); }
    }
}
