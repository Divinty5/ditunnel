using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Styling;

namespace DiTunnel.App;

internal static class DesktopWindowStyle
{
    internal static void Update(Window window)
    {
        var platform = window.TryGetPlatformHandle();
        if (platform is null || platform.Handle == IntPtr.Zero) return;
        var dark = window.ActualThemeVariant == ThemeVariant.Dark;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            // Windows 10 caches non-client theme parts separately from DWM.
            // Invalidate those parts before setting the new caption theme.
            SetWindowTheme(platform.Handle, dark ? "DarkMode_Explorer" : null, null);
            var value = dark ? 1 : 0;
            var attribute = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985) ? 20 : 19;
            DwmSetWindowAttribute(platform.Handle, attribute, ref value, sizeof(int));
            // Repaint the existing frame without recalculating or recreating it.
            // Preserve activation, styles, bounds and all native caption buttons.
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
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, byte[] data, int count);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
}
