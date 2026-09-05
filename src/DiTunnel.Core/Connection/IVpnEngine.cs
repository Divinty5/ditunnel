namespace DiTunnel.Core.Connection;

public interface IVpnEngine
{
    VpnStatus Status { get; }

    event EventHandler<VpnStatus>? StatusChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
