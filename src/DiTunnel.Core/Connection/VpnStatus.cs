namespace DiTunnel.Core.Connection;

public sealed record VpnStatus(
    VpnConnectionState State,
    string? Message = null,
    DateTimeOffset? ConnectedAt = null,
    double? DelayMilliseconds = null)
{
    public static VpnStatus Disconnected { get; } = new(VpnConnectionState.Disconnected);
}
