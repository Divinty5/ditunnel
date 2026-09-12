using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Connection;

public sealed record ServerProbeResult(double? Milliseconds, string Message);

public enum ServerProbeMode
{
    Fast,
    Https
}

public interface IServerProbe
{
    Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast);
}

public interface IServerBatchProbe : IServerProbe
{
    Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast);
}
