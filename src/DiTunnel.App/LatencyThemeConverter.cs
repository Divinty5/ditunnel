using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using DiTunnel.App.ViewModels;

namespace DiTunnel.App;

public sealed class LatencyThemeConverter : IMultiValueConverter
{
    public static LatencyThemeConverter Instance { get; } = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2 && values[0] is IBrush brush
            ? LatencyPalette.ForTheme(brush, values[1] as ThemeVariant == ThemeVariant.Light)
            : null;
}
