using Avalonia.Media;

namespace DiTunnel.App.ViewModels;

internal static class LatencyPalette
{
    private static readonly IBrush Green = Brush(0x55, 0xD7, 0x84);
    private static readonly IBrush Lime = Brush(0xA8, 0xD9, 0x5B);
    private static readonly IBrush Yellow = Brush(0xE8, 0xC9, 0x68);
    private static readonly IBrush Orange = Brush(0xF3, 0x9A, 0x4A);
    private static readonly IBrush Red = Brush(0xF0, 0x64, 0x70);
    private static readonly IBrush Pending = Brush(0xA5, 0x8A, 0xFF);

    public static IBrush For(double? milliseconds, bool unavailable = false) => unavailable || milliseconds > 4000
        ? Red
        : milliseconds switch
        {
            null => Pending,
            <= 1000 => Green,
            <= 2000 => Lime,
            <= 3000 => Yellow,
            <= 4000 => Orange,
            _ => Red
        };

    private static IBrush Brush(byte red, byte green, byte blue) =>
        new SolidColorBrush(Color.FromRgb(red, green, blue));
}
