using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class SubscriptionPresentationTests
{
    private sealed class Store(SubscriptionUsage usage) : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => [new("Synthetic", "VLESS", "vless://synthetic", Usage: usage)];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }
    private sealed class Engine : IProfileVpnEngine
    {
        public int Starts;
        public VpnStatus Status => VpnStatus.Disconnected;
        public bool RequiresAdministrator => false;
        public event EventHandler<VpnStatus>? StatusChanged { add { } remove { } }
        public Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default) { Starts++; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(ReleaseTarget.WindowsX64)] [InlineData(ReleaseTarget.AndroidArm64)] [InlineData(ReleaseTarget.LinuxX64)]
    public async Task AuthoritativeExpiryBlocksStartAndOverridesGreenPresentation(ReleaseTarget target)
    {
        var platform = target == ReleaseTarget.WindowsX64 ? AppPlatform.Windows : target == ReleaseTarget.AndroidArm64 ? AppPlatform.Android : AppPlatform.Linux;
        var engine = new Engine();
        var vm = new MainViewModel(engine, new Store(new(0, 0, 100, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds())), platform: platform);
        try
        {
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.Equal(0, engine.Starts);
            vm.ConnectionState = VpnConnectionState.Connected;
            Assert.True(vm.IsSubscriptionExhausted);
            Assert.Equal("Подписка исчерпана", vm.StatusText);
            Assert.Equal(SubscriptionAccess.ExhaustedMessage, vm.DisplayNotice);
            Assert.Equal(ConnectionPalette.AccentFor(VpnConnectionState.Error).ToString(), ((Avalonia.Media.ISolidColorBrush)vm.PowerBorderBrush).Color.ToString());
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(0, 0, true, false)] [InlineData(6, 9, false, true)] [InlineData(7, 10, false, false)]
    public async Task TimeAndTrafficThresholdsUseExactRemainingQuota(int days, int gigabytes, bool exhausted, bool warning)
    {
        var usage = new SubscriptionUsage(0, 0, gigabytes == 0 ? 1 : gigabytes * 1073741824L,
            DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds());
        if (gigabytes == 0) usage = usage with { Download = 1 };
        var vm = new MainViewModel(null, new Store(usage), platform: AppPlatform.Android);
        try
        {
            Assert.Equal(exhausted, vm.IsSubscriptionExhausted);
            Assert.Equal(vm.SubscriptionDaysBrush.ToString(), vm.SubscriptionTrafficBrush.ToString());
            if (warning) Assert.Equal(LatencyPalette.For(2500).ToString(), vm.SubscriptionDaysBrush.ToString());
            if (exhausted) Assert.Equal(LatencyPalette.For(null, true).ToString(), vm.SubscriptionDaysBrush.ToString());
        }
        finally { await vm.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(ReleaseTarget.WindowsX64)] [InlineData(ReleaseTarget.AndroidArm64)] [InlineData(ReleaseTarget.LinuxX64)]
    public async Task DetailedReportIsCopyableButNotShownOnAnyPlatform(ReleaseTarget target)
    {
        var platform = target == ReleaseTarget.WindowsX64 ? AppPlatform.Windows : target == ReleaseTarget.AndroidArm64 ? AppPlatform.Android : AppPlatform.Linux;
        var vm = new MainViewModel(null, new Store(new(null, null, null, null)), platform: platform);
        try
        {
            vm.Notice = "Ошибка TLS. HTTPS не проверен\nМетод: HTTPS\nЭтап: TLS";
            Assert.Equal("Ошибка TLS. HTTPS не проверен", vm.DisplayNotice);
            Assert.Contains("Этап: TLS", vm.DiagnosticText);
            Assert.True(vm.HasConnectionError);
        }
        finally { await vm.ShutdownAsync(); }
    }
}
