using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Connection;

public interface IServerCountryResolver
{
    Task<string?> ResolveAsync(ImportedProfile profile, CancellationToken cancellationToken = default);
}
