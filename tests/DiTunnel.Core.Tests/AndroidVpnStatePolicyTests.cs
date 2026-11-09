using DiTunnel.Core.Connection;
using DiTunnel.Platform.Android;

namespace DiTunnel.Core.Tests;

public sealed class AndroidVpnStatePolicyTests
{
    [Theory]
    [InlineData(true, false, false, false, VpnConnectionState.Disconnected)]
    [InlineData(true, false, false, true, VpnConnectionState.Disconnected)]
    [InlineData(true, false, true, false, VpnConnectionState.Disconnected)]
    [InlineData(true, false, true, true, VpnConnectionState.Connected)]
    [InlineData(false, true, false, false, VpnConnectionState.Disconnected)]
    [InlineData(false, true, true, false, VpnConnectionState.Reconnecting)]
    [InlineData(false, false, true, true, VpnConnectionState.Disconnected)]
    public void SnapshotAloneCannotProveLiveVpn(bool active, bool recovering, bool processAlive, bool networkActive, VpnConnectionState expected)
        => Assert.Equal(expected, AndroidVpnStatePolicy.Observed(active, recovering, processAlive, networkActive));

    [Fact]
    public void StaleConnectedTileBecomesDisconnectedAfterProcessOrNetworkLoss()
    {
        var cached = new VpnStatus(VpnConnectionState.Connected, "Old success", DelayMilliseconds: 24);
        var observed = AndroidVpnStatePolicy.Reconcile(cached, VpnStatus.Disconnected, false);
        Assert.Equal(VpnConnectionState.Disconnected, observed.State);
        Assert.Null(observed.DelayMilliseconds);
    }

    [Fact]
    public void PendingCommandAndCurrentVerifiedConnectionKeepTheirOwnStatus()
    {
        var connecting = new VpnStatus(VpnConnectionState.Connecting);
        Assert.Same(connecting, AndroidVpnStatePolicy.Reconcile(connecting, VpnStatus.Disconnected, true));
        var connected = new VpnStatus(VpnConnectionState.Connected, DelayMilliseconds: 30);
        Assert.Same(connected, AndroidVpnStatePolicy.Reconcile(connected, new(VpnConnectionState.Connected), false));
    }
}
