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
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            var value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(platform.Handle, 20, ref value, sizeof(int)) != 0) return;
            // Changing the DWM attribute alone does not repaint an existing caption.
            SetWindowPos(platform.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
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
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, byte[] data, int count);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
}
