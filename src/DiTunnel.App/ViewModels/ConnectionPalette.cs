using Avalonia.Media;
using DiTunnel.Core.Connection;

namespace DiTunnel.App.ViewModels;

internal static class ConnectionPalette
{
    public static Color AccentFor(VpnConnectionState state) => state switch
    {
        VpnConnectionState.Connected => Color.FromRgb(0x55, 0xD7, 0x84),
        VpnConnectionState.Error => Color.FromRgb(0xF0, 0x64, 0x70),
        _ => Color.FromRgb(0xA5, 0x8A, 0xFF)
    };

    public static Color RecolourTunnelPixel(Color pixel, Color accent)
    {
        if (pixel.A == 0) return pixel;
        var hsv = pixel.ToHsv();
        // Select purple illumination, leaving the neutral silver D untouched.
        // Blend weak reflections so the selection does not leave a hard edge.
        var hueAmount = Math.Min(Math.Clamp((hsv.H - 230) / 30, 0, 1), Math.Clamp((350 - hsv.H) / 25, 0, 1));
        var amount = hueAmount * Math.Clamp((hsv.S - .18) / .22, 0, 1);
        if (amount == 0) return pixel;
        var tinted = new HsvColor(hsv.A, accent.ToHsv().H, hsv.S, hsv.V).ToRgb();
        byte Blend(byte original, byte target) => (byte)Math.Round(original + (target - original) * amount);
        return Color.FromArgb(pixel.A, Blend(pixel.R, tinted.R), Blend(pixel.G, tinted.G), Blend(pixel.B, tinted.B));
    }
}
