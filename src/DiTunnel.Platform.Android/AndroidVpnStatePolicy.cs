using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Android;

internal static class AndroidVpnStatePolicy
{
    internal static VpnConnectionState Observed(bool active, bool recovering, bool processAlive, bool systemVpnActive) =>
        active && processAlive && systemVpnActive ? VpnConnectionState.Connected
            : recovering && processAlive ? VpnConnectionState.Reconnecting : VpnConnectionState.Disconnected;

    internal static VpnStatus Reconcile(VpnStatus cached, VpnStatus observed, bool operationPending)
    {
        if (operationPending) return cached;
        if (cached.State == observed.State) return cached;
        if (cached.State == VpnConnectionState.Error && observed.State == VpnConnectionState.Disconnected) return cached;
        return observed;
    }
}
