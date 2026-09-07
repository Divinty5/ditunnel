using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Connection;

public sealed record ServerProbeResult(double? Milliseconds, string Message);

public interface IServerProbe
{
    Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default);
}
