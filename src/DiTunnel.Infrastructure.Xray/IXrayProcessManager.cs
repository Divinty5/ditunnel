namespace DiTunnel.Infrastructure.Xray;

public interface IXrayProcessManager : IAsyncDisposable
{
    bool IsRunning { get; }

    int? ProcessId { get; }

    event Action<XrayLogEntry>? LogReceived;

    event Action<int>? Exited;

    Task<XrayValidationResult> ValidateConfigurationAsync(
        string configurationPath,
        CancellationToken cancellationToken = default);

    Task StartAsync(string configurationPath, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
