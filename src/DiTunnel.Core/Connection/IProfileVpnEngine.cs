using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Connection;

public interface IProfileVpnEngine : IAsyncDisposable
{
    VpnStatus Status { get; }
    event EventHandler<VpnStatus>? StatusChanged;
    bool RequiresAdministrator { get; }
    Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
