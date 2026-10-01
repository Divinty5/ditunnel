using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Data.Converters;

namespace DiTunnel.App;

public static class L
{
    public sealed record Language(string Code, string Name);
    public static IReadOnlyList<Language> Languages { get; } =
        [new("ru", "Русский"), new("en", "English"), new("es", "Español"), new("zh-Hans", "简体中文")];
    private static readonly Dictionary<string, Dictionary<string, string>> Translations = Load();
    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("DiTunnel.App.Translations.json")!;
        using var document = JsonDocument.Parse(stream);
        var english = document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString() ?? string.Empty,
            StringComparer.Ordinal);
        using var extraStream = typeof(L).Assembly.GetManifestResourceStream("DiTunnel.App.Translations.extra.json")!;
        using var extra = JsonDocument.Parse(extraStream);
        var result = new Dictionary<string, Dictionary<string, string>> { ["en"] = english };
        foreach (var language in Languages.Where(l => l.Code is not ("ru" or "en")))
        {
            var translated = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in english)
                if (extra.RootElement.TryGetProperty(entry.Value, out var row) && row.TryGetProperty(language.Code, out var value))
                    translated[entry.Key] = value.GetString()!;
            result[language.Code] = translated;
        }
        return result;
    }
    public static event Action? Changed;
    public static string T(string text)
    {
        return Translate(text, UserSettings.Current.Language);
    }
    public static string Translate(string text, string language)
    {
        if (!Translations.TryGetValue(language, out var catalog)) return text;
        if (catalog.TryGetValue(text, out var translated)) return translated;
        foreach (var entry in catalog.OrderByDescending(p => p.Key.Length))
            text = text.Replace(entry.Key, entry.Value, StringComparison.Ordinal);
        return text;
    }
    public static void Apply()
    {
        if (Application.Current is { } app)
        {
            // Stable keys allow translations to be reordered without changing XAML bindings.
            foreach (var entry in Translations["en"]) app.Resources["Text" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Key)))[..12]] = T(entry.Key);
        }
        Changed?.Invoke();
    }
}

public sealed class TranslationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string text ? L.T(text) : value;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
