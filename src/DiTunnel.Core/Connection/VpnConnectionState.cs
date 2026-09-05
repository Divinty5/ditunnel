namespace DiTunnel.Core.Connection;

public enum VpnConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Error
}
