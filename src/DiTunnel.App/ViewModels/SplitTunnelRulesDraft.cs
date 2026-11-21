using DiTunnel.Core.Connection;

namespace DiTunnel.App.ViewModels;

internal sealed class SplitTunnelRulesDraft
{
    private static readonly SplitTunnelMode[] EditableModes = [SplitTunnelMode.BypassSelected, SplitTunnelMode.ProxySelected];
    private readonly Dictionary<SplitTunnelMode, (string[] Domains, string[] Applications)> rules = [];

    public SplitTunnelRulesDraft(UserSettings settings)
    {
        foreach (var mode in EditableModes)
        {
            var (domains, applications) = settings.GetSplitTunnelRules(mode);
            rules[mode] = (NormalizeDomains(domains), applications.Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    public IEnumerable<string> Applications => rules.Values.SelectMany(rule => rule.Applications);
    public string DomainText(SplitTunnelMode mode) => string.Join(Environment.NewLine, rules[mode].Domains);
    public IReadOnlyList<string> ApplicationsFor(SplitTunnelMode mode) => rules[mode].Applications;

    public void Capture(SplitTunnelMode mode, string? domainText, IEnumerable<string> applications)
    {
        var domains = domainText?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        rules[mode] = (NormalizeDomains(domains), applications.ToArray());
    }

    public void ApplyTo(UserSettings settings, SplitTunnelMode selectedMode)
    {
        settings.SplitTunnelMode = selectedMode;
        settings.SplitTunnelDefaultsApplied = true;
        foreach (var mode in EditableModes)
        {
            var (domains, applications) = rules[mode];
            settings.SetSplitTunnelRules(mode, domains, applications.OrderBy(value => value, StringComparer.Ordinal));
        }
    }

    private static string[] NormalizeDomains(IEnumerable<string> domains) => domains.Select(SplitTunnelPolicy.NormalizeDomainForDisplay)
        .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
