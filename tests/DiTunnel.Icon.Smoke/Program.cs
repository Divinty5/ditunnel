using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DiTunnel.App.ViewModels;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run()
    {
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        using var stream = AssetLoader.Open(new Uri("avares://DiTunnel.App/Assets/icon.png"));
        var original = new WindowIcon(stream);
        var window = new Window { Icon = original };
        var model = new MainViewModel(null, new EmptyStore());
        window.DataContext = model;
        var type = typeof(MainViewModel).Assembly.GetType("DiTunnel.App.VpnWindowIconIndicator")!;
        using var indicator = (IDisposable)Activator.CreateInstance(type, window)!;
        // Linux runs this test only under Xvfb; Windows never shows a test window.
        if (OperatingSystem.IsLinux()) window.Show();
        Require(ReferenceEquals(window.Icon, original), "Disconnected icon");
        var purple = ReadX11Icon(window);
        model.ConnectionState = VpnConnectionState.Connected;
        var green = window.Icon;
        Require(!ReferenceEquals(green, original), "Connected icon");
        var greenPixels = ReadX11Icon(window);
        model.ConnectionState = VpnConnectionState.Error;
        var red = window.Icon;
        Require(!ReferenceEquals(red, green) && !ReferenceEquals(red, original), "Error icon");
        var redPixels = ReadX11Icon(window);
        if (OperatingSystem.IsLinux())
        {
            Require(purple.Length > 2 && greenPixels.Length == purple.Length && redPixels.Length == purple.Length, "X11 icon dimensions");
            Require(!purple.SequenceEqual(greenPixels) && !greenPixels.SequenceEqual(redPixels), "Native X11 icon updates");
            Require(greenPixels.Skip(2).Count(pixel => Channel(pixel, 8) > Channel(pixel, 16) + 20 && Channel(pixel, 8) > Channel(pixel, 0) + 20) > 100, "Green tunnel");
            Require(redPixels.Skip(2).Count(pixel => Channel(pixel, 16) > Channel(pixel, 8) + 20 && Channel(pixel, 16) > Channel(pixel, 0) + 20) > 100, "Red tunnel");
        }
        foreach (var state in new[] { VpnConnectionState.Connecting, VpnConnectionState.Reconnecting, VpnConnectionState.Disconnecting, VpnConnectionState.Disconnected })
        {
            model.ConnectionState = state;
            Require(ReferenceEquals(window.Icon, original), "Purple icon for " + state);
        }
        model.ConnectionState = VpnConnectionState.Connected;
        Require(ReferenceEquals(window.Icon, green), "Icon cache");
        var replacement = new MainViewModel(null, new EmptyStore()) { ConnectionState = VpnConnectionState.Error };
        window.DataContext = replacement;
        Require(ReferenceEquals(window.Icon, red), "DataContext replacement");
        indicator.Dispose(); indicator.Dispose();
        replacement.ConnectionState = VpnConnectionState.Connected;
        Require(ReferenceEquals(window.Icon, red), "Disposal unsubscribes state changes");
        window.Close();
        Console.WriteLine(OperatingSystem.IsLinux() ? "PASS_NATIVE_X11_VPN_ICON_COLOURS_CACHE_DISPOSAL" : "PASS_WINDOWS_VPN_ICON_COLOURS_CACHE_DISPOSAL");
    }

    private static int Channel(ulong pixel, int shift) => (int)(pixel >> shift & 255);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static ulong[] ReadX11Icon(Window window)
    {
        if (!OperatingSystem.IsLinux()) return [];
        Dispatcher.UIThread.RunJobs();
        // The test has no running dispatcher loop to flush Avalonia's X11
        // connection, so explicitly synchronize it before using a second one.
        var implementation = typeof(TopLevel).GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(window)!;
        var globals = implementation.GetType().GetField("_x11", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(implementation)!;
        var displayField = globals.GetType().GetField("Display", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var windowDisplay = (IntPtr)(displayField?.GetValue(globals)
            ?? globals.GetType().GetProperty("Display", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(globals))!;
        XSync(windowDisplay, false);
        var display = XOpenDisplay(IntPtr.Zero);
        Require(display != IntPtr.Zero, "X11 display");
        try
        {
            var atom = XInternAtom(display, "_NET_WM_ICON", false);
            var status = XGetWindowProperty(display, window.TryGetPlatformHandle()!.Handle, atom, 0, 100000,
                false, IntPtr.Zero, out _, out var format, out var count, out _, out var data);
            try
            {
                Require(status == 0 && format == 32 && count > 2 && count <= 100000, "X11 _NET_WM_ICON property");
                return Enumerable.Range(0, (int)count).Select(index => (ulong)Marshal.ReadInt64(data, index * IntPtr.Size)).ToArray();
            }
            finally { if (data != IntPtr.Zero) XFree(data); }
        }
        finally { XCloseDisplay(display); }
    }

    private sealed class EmptyStore : IProfileStore
    {
        public IReadOnlyList<ImportedProfile> Load() => [];
        public void Save(IEnumerable<ImportedProfile> profiles) { }
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property,
        long offset, long length, [MarshalAs(UnmanagedType.Bool)] bool delete, IntPtr requestedType,
        out IntPtr actualType, out int actualFormat, out ulong count, out ulong bytesAfter, out IntPtr data);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
