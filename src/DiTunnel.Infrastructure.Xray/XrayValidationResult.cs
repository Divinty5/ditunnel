namespace DiTunnel.Infrastructure.Xray;

public sealed record XrayValidationResult(
    bool IsValid,
    int ExitCode,
    string StandardOutput,
    string StandardError);
