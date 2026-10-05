using DiTunnel.Core.Connection;

namespace DiTunnel.App;

public static class ApplicationSelectionPresets
{
    private static readonly string[] ProxyPackageFragments =
    [
        "telegram", "discord", "whatsapp", "openai", "chatgpt", "codex", "anthropic", "claude", "youtube", "vivaldi"
    ];

    private static readonly string[] ProxyNameFragments =
    [
        "telegram", "discord", "whatsapp", "chatgpt", "codex", "claude", "youtube", "vivaldi"
    ];

    private static readonly string[] BypassPackagePrefixes =
    [
        "ru.", "com.yandex.", "com.ozon.", "com.wildberries.", "com.samokat.",
        "com.vkontakte.", "com.vk.", "com.mail.ru.", "com.mobifitness.", "com.doublegis.", "com.microsoft.office.outlook"
    ];

    private static readonly string[] BypassNameFragments =
    [
        "сбер", "sber", "тинькофф", "t-bank", "т-банк", "втб", "альфа", "alfa", "mir pay", "мир pay",
        "рсхб", "rshb", "ozon", "wildberries", "самокат", "яндекс", "yandex", "max",
        "вконтакте", "vk видео", "vk video", "одноклассники", "rutube", "рутуб", "тамтам", "дзен", "dzen", "rustore",
        "mobifitness", "мобифитнес", "vk workspace", "vkworkspace", "vk teams", "vkteams",
        "saby", "сбис", "sbis", "2гис", "2gis", "doublegis", "outlook", "opencode",
        "госуслуги", "gosuslugi", "mos.ru", "мтс", "mts link", "mts-link", "mtslink",
        "мегафон", "megafon", "билайн", "beeline", "ростелеком", "rostelecom",
        "rambler", "рамблер", "avito", "авито", "cdek", "сдэк", "rzd", "ржд", "почта россии"
    ];

    public static IReadOnlyList<string> Select(SplitTunnelMode mode, IEnumerable<InstalledApplication> applications)
    {
        return applications.Where(application => mode switch
            {
                SplitTunnelMode.ProxySelected => ContainsAny(application.Id, ProxyPackageFragments)
                    || ContainsAny(application.Name, ProxyNameFragments),
                SplitTunnelMode.BypassSelected => application.Id.Equals("com.mobifitness", StringComparison.OrdinalIgnoreCase)
                    || BypassPackagePrefixes.Any(prefix => application.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    || ContainsAny(application.Name, BypassNameFragments)
                    // Windows paths may identify the vendor even when the Start menu title is generic.
                    || (application.Id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && MatchesWindowsBypass(application.Id)),
                _ => false
            })
            .Select(application => application.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ContainsAny(string value, IEnumerable<string> fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesWindowsBypass(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Any(part => part.Equals("Yandex", StringComparison.OrdinalIgnoreCase)
            || part.Equals("YandexBrowser", StringComparison.OrdinalIgnoreCase)
            || part.Equals("VK Teams", StringComparison.OrdinalIgnoreCase))
            || new[] { "vkworkspace", "vkteams", "saby", "sbis", "2gis", "doublegis", "outlook", "olk", "opencode" }
                .Contains(Path.GetFileNameWithoutExtension(parts[^1]), StringComparer.OrdinalIgnoreCase);
    }
}
