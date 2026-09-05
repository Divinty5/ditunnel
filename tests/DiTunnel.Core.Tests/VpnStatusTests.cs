using DiTunnel.Core.Connection;

namespace DiTunnel.Core.Tests;

public sealed class VpnStatusTests
{
    [Fact]
    public void Disconnected_status_has_expected_state()
    {
        Assert.Equal(VpnConnectionState.Disconnected, VpnStatus.Disconnected.State);
        Assert.Null(VpnStatus.Disconnected.ConnectedAt);
    }
}
