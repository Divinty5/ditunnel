namespace DiTunnel.Core.Connection;

public enum SplitTunnelMode { ProxyAll, BypassSelected, ProxySelected }

public sealed record SplitTunnelPolicy(SplitTunnelMode Mode, IReadOnlyList<string> Domains, IReadOnlyList<string> Processes)
{
    public static SplitTunnelPolicy Default { get; } = new(SplitTunnelMode.ProxyAll, [], []);

    public static string NormalizeDomain(string value)
    {
        var input = value.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri)) return uri.Host.Trim('.').ToLowerInvariant();
        var delimiter = input.IndexOfAny(['/', '?', '#']);
        if (delimiter >= 0) input = input[..delimiter];
        return input.Trim('.').ToLowerInvariant();
    }
}
