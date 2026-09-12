using System.Globalization;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Data.Converters;

namespace DiTunnel.App;

public static class L
{
    private static readonly Dictionary<string, string> Translations = Load();
    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("DiTunnel.App.Translations.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString() ?? string.Empty,
            StringComparer.Ordinal);
    }
    public static event Action? Changed;
    public static string T(string text)
    {
        if (UserSettings.Current.Language != "en") return text;
        if (Translations.TryGetValue(text, out var translated)) return translated;
        foreach (var entry in Translations.OrderByDescending(p => p.Key.Length))
            text = text.Replace(entry.Key, entry.Value, StringComparison.Ordinal);
        return text;
    }
    public static void Apply()
    {
        if (Application.Current is { } app)
        {
            // Stable keys allow translations to be reordered without changing XAML bindings.
            foreach (var entry in Translations) app.Resources["Text" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Key)))[..12]] = T(entry.Key);
        }
        Changed?.Invoke();
    }
}

public sealed class TranslationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string text ? L.T(text) : value;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
