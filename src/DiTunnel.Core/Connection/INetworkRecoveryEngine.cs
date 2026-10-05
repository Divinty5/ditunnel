namespace DiTunnel.Core.Connection;

public interface INetworkRecoveryEngine
{
    // Explicit user action: stop the tunnel and remove only our own protection.
    Task RestoreNetworkAsync(CancellationToken cancellationToken = default);
}
