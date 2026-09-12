using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Connection;

public interface IProfileVpnEngine : IAsyncDisposable
{
    VpnStatus Status { get; }
    ImportedProfile? ActiveProfile => null;
    event EventHandler<VpnStatus>? StatusChanged;
    bool RequiresAdministrator { get; }
    bool IsNetworkProtectionActive => false;
    Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    async Task SwitchAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken);
        await ConnectAsync(profile, cancellationToken);
    }
}
