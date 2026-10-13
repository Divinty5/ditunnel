using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace DiTunnel.App;

[SupportedOSPlatform("windows")]
internal static class WindowsWindowBounds
{
    internal static PixelRect? Capture(IntPtr window) => GetWindowRect(window, out var rect)
        ? new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : null;

    internal static void Restore(IntPtr window, PixelRect rect)
    {
        if (rect.Width < 1 || rect.Height < 1) return;
        // Keep the physical outer rectangle, including the caption and resize border.
        // Reconstructing it from client DIPs loses snapped edges after Hide/Show.
        SetWindowPos(window, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, 0x0004 | 0x0010);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
