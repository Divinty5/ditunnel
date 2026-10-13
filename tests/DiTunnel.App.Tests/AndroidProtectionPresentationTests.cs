using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.Tests;

public sealed class AndroidProtectionPresentationTests
{
    [Theory]
    [InlineData(VpnConnectionState.Connecting)]
    [InlineData(VpnConnectionState.Connected)]
    [InlineData(VpnConnectionState.Reconnecting)]
    [InlineData(VpnConnectionState.Disconnecting)]
    [InlineData(VpnConnectionState.Disconnected)]
    [InlineData(VpnConnectionState.Error)]
    public void RunningAndroidTunnelDoesNotClaimKillSwitchProtection(VpnConnectionState state)
    {
        // Android may report an already running VPN service before the UI receives
        // Connected. That protects the tunnel, but is not an application kill switch.
        var vm = new MainViewModel(new RunningTunnel(), new EmptyStore(), platform: AppPlatform.Android)
        { ConnectionState = state };
        Assert.False(vm.IsKillSwitchEnabled);
        Assert.False(vm.ShowKillSwitchStatus);
        Assert.Empty(vm.KillSwitchText);
    }

    private sealed class EmptyStore : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => [];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }
    private sealed class RunningTunnel : IProfileVpnEngine
    {
        public VpnStatus Status => new(VpnConnectionState.Connected, "VPN running");
        public bool IsNetworkProtectionActive => true;
        public bool RequiresAdministrator => false;
        public event EventHandler<VpnStatus>? StatusChanged { add { } remove { } }
        public Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
