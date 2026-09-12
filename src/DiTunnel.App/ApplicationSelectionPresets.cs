using DiTunnel.Core.Connection;

namespace DiTunnel.App;

public static class ApplicationSelectionPresets
{
    private static readonly string[] ProxyPackageFragments =
    [
        "telegram", "discord", "whatsapp", "openai", "chatgpt", "anthropic", "claude", "youtube"
    ];

    private static readonly string[] ProxyNameFragments =
    [
        "telegram", "discord", "whatsapp", "chatgpt", "claude", "youtube"
    ];

    private static readonly string[] BypassPackagePrefixes =
    [
        "ru.", "com.yandex.", "com.ozon.", "com.wildberries.", "com.samokat."
    ];

    private static readonly string[] BypassNameFragments =
    [
        "сбер", "sber", "тинькофф", "t-bank", "т-банк", "втб", "альфа", "alfa", "mir pay", "мир pay",
        "рсхб", "rshb", "ozon", "wildberries", "самокат", "яндекс", "yandex", "max"
    ];

    public static IReadOnlyList<string> Select(SplitTunnelMode mode, IEnumerable<InstalledApplication> applications)
    {
        return applications.Where(application => mode switch
            {
                SplitTunnelMode.ProxySelected => ContainsAny(application.Id, ProxyPackageFragments)
                    || ContainsAny(application.Name, ProxyNameFragments),
                SplitTunnelMode.BypassSelected => BypassPackagePrefixes.Any(prefix => application.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    || ContainsAny(application.Name, BypassNameFragments),
                _ => false
            })
            .Select(application => application.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ContainsAny(string value, IEnumerable<string> fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
