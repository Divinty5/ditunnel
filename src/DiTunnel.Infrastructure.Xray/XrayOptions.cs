using System.Diagnostics;

namespace DiTunnel.Infrastructure.Xray;

public sealed class XrayOptions
{
    public required string ExecutablePath { get; init; }

    public required string WorkingDirectory { get; init; }

    public TimeSpan StartupGracePeriod { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public bool ValidateConfigurationBeforeStart { get; init; } = true;

    public Func<Process, IXrayProcessLifetime>? ProcessLifetimeFactory { get; init; }
}

public interface IXrayProcessLifetime : IDisposable
{
    void RequestShutdown();
}
