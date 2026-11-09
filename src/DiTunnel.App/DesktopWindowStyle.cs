using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Styling;

namespace DiTunnel.App;

internal static class DesktopWindowStyle
{
    private sealed class CaptionState { public IntPtr Handle; public bool? Dark; }
    private static readonly ConditionalWeakTable<Window, CaptionState> Captions = new();
    internal static void Update(Window window)
    {
        var platform = window.TryGetPlatformHandle();
        if (platform is null || platform.Handle == IntPtr.Zero) return;
        var dark = window.ActualThemeVariant == ThemeVariant.Dark;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            if (window.ExtendClientAreaToDecorationsHint)
            {
                // Avalonia paints the caption. Only colour the remaining native
                // resize border; don't invalidate or re-theme its managed chrome.
                var borderDark = dark ? 1 : 0;
                DwmSetWindowAttribute(platform.Handle, 20, ref borderDark, sizeof(int));
                return;
            }
            var state = Captions.GetOrCreateValue(window);
            var changed = state.Handle != platform.Handle || state.Dark != dark;
            // Explorer's control theme is not the native window-frame theme. On
            // Windows 10 it can leave caption buttons painted with wrong colours.
            // Restore the window class's own visual styles before applying DWM.
            if (changed) SetWindowTheme(platform.Handle, null, null);
            var value = dark ? 1 : 0;
            var attribute = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985) ? 20 : 19;
            DwmSetWindowAttribute(platform.Handle, attribute, ref value, sizeof(int));
            if (changed)
            {
                // Windows 10 can defer its cached non-client frame until a resize.
                // Recalculate it in place without moving, resizing or activating.
                state.Handle = platform.Handle; state.Dark = dark;
                SetWindowPos(platform.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
            }
            SendMessage(platform.Handle, 0x0086, GetForegroundWindow() == platform.Handle ? 1 : 0, 0); // WM_NCACTIVATE
            RedrawWindow(platform.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0100 | 0x0400);
        }
        else if (OperatingSystem.IsLinux() && platform.HandleDescriptor == "XID")
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            try
            {
                var property = XInternAtom(display, "_GTK_THEME_VARIANT", false);
                var type = XInternAtom(display, "UTF8_STRING", false);
                var bytes = Encoding.UTF8.GetBytes(dark ? "dark" : "light");
                XChangeProperty(display, platform.Handle, property, type, 8, 0, bytes, bytes.Length);
                XFlush(display);
            }
            finally { XCloseDisplay(display); }
        }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string? appName, string? idList);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, byte[] data, int count);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
}
