namespace DiTunnel.Infrastructure.Xray;

public enum XrayLogStream
{
    StandardOutput,
    StandardError
}

public sealed record XrayLogEntry(
    DateTimeOffset Timestamp,
    XrayLogStream Stream,
    string Message);
