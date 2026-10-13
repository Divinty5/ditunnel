using System.Globalization;
using System.Net;

namespace DiTunnel.Core.Connection;

public enum SplitTunnelMode { ProxyAll, BypassSelected, ProxySelected }

public sealed record SplitTunnelPolicy(SplitTunnelMode Mode, IReadOnlyList<string> Domains, IReadOnlyList<string> Processes)
{
    public static SplitTunnelPolicy Default { get; } = new(SplitTunnelMode.ProxyAll, [], []);

    public static string NormalizeDomain(string value)
    {
        var domain = ExtractDomain(value);
        if (domain.Length == 0 || IPAddress.TryParse(domain, out _)) return domain;
        try { return new IdnMapping().GetAscii(domain).Trim('.').ToLowerInvariant(); }
        // Preserve the existing handling of malformed entries: normalization must
        // not crash settings or silently remove a user's routing rule.
        catch (ArgumentException) { return domain; }
    }

    public static string NormalizeDomainForDisplay(string value)
    {
        var domain = NormalizeDomain(value);
        if (domain.Length == 0 || IPAddress.TryParse(domain, out _)) return domain;
        try { return new IdnMapping().GetUnicode(domain).ToLowerInvariant(); }
        catch (ArgumentException) { return domain; }
    }

    private static string ExtractDomain(string value)
    {
        var input = value.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri)) return uri.Host.Trim('.').ToLowerInvariant();
        var delimiter = input.IndexOfAny(['/', '?', '#']);
        if (delimiter >= 0) input = input[..delimiter];
        return input.Trim('.').ToLowerInvariant();
    }
}
