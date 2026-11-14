using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Connection;

public sealed record ServerProbeResult(double? Milliseconds, string Message, bool IsDeferred = false,
    ProbeDiagnostics? Diagnostics = null);

// Only generated, non-sensitive fields belong here. Never include exception messages or profiles.
public sealed record ProbeDiagnostics(string Method, string Stage, string ErrorType, double DurationMilliseconds,
    bool ProfileConfirmed, string ClientVersion, string Attempts = "", string Platform = "Android")
{
    public string ToDisplayString() => FormattableString.Invariant(
        $"Di-Tunnel {Platform} {ClientVersion}\nМетод: {Method}\nЭтап: {Stage}\nДлительность: {DurationMilliseconds:F0} мс\nТип ошибки: {ErrorType}\nРабота профиля подтверждена: {(ProfileConfirmed ? "да" : "нет")}\n{Attempts}").TrimEnd();
}

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

public interface IProgressiveServerProbe : IServerBatchProbe
{
    Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles,
        IProgress<(int Index, ServerProbeResult Result)> progress, CancellationToken cancellationToken, ServerProbeMode mode);
}

public interface IActiveTunnelProbe
{
    Task<ServerProbeResult> ProbeActiveTunnelAsync(CancellationToken cancellationToken = default);
}
