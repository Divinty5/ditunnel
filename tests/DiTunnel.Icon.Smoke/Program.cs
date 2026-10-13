using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Styling;
using DiTunnel.App.Views;
using DiTunnel.App.ViewModels;
using DiTunnel.App;
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
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        using var stream = AssetLoader.Open(new Uri("avares://DiTunnel.App/Assets/icon.png"));
        var original = new WindowIcon(stream);
        var window = new Window { Icon = original };
        VerifyWindowThemeAndBounds(window);
        var draft = new TextBox { Text = "ru\nрф" };
        var section = Dialogs.SettingsSection("Соединение", draft);
        Require(!section.IsExpanded, "Settings sections start collapsed");
        section.IsExpanded = true; draft.Text = "example.ru"; section.IsExpanded = false;
        Require(ReferenceEquals(section.Content, draft) && draft.Text == "example.ru", "Collapsing keeps settings drafts");
        var model = new MainViewModel(null, new EmptyStore());
        window.DataContext = model;
        var taskbarPath = Path.Combine(Path.GetTempPath(), "DiTunnel-icon-" + Guid.NewGuid().ToString("N"), "taskbar.ico");
        var ourShortcut = Path.ChangeExtension(taskbarPath, ".lnk");
        var foreignShortcut = Path.ChangeExtension(taskbarPath, ".foreign.lnk");
        var foreignTarget = Path.Combine(Path.GetDirectoryName(taskbarPath)!, "other-application.exe");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(taskbarPath)!);
            var installedShortcut = Path.ChangeExtension(taskbarPath, ".installed.lnk");
            CreateShortcut(installedShortcut, Environment.ProcessPath!, (Environment.ProcessPath!, 0));
            var installedBytes = File.ReadAllBytes(installedShortcut);
            File.SetAttributes(installedShortcut, File.GetAttributes(installedShortcut) | FileAttributes.ReadOnly);
            WindowsTaskbarIcon.EnsureUserShortcut(installedShortcut, ourShortcut);
            Require(File.Exists(ourShortcut) && installedBytes.SequenceEqual(File.ReadAllBytes(installedShortcut)), "User copy preserves installed shortcut");
            Require((File.GetAttributes(ourShortcut) & FileAttributes.ReadOnly) == 0, "User shortcut remains writable");
            CreateShortcut(foreignShortcut, foreignTarget, (foreignTarget, 7));
            WindowsTaskbarIcon.EnsureUserShortcut(foreignShortcut, Path.ChangeExtension(taskbarPath, ".foreign-copy.lnk"));
            Require(!File.Exists(Path.ChangeExtension(taskbarPath, ".foreign-copy.lnk")), "Other applications never copied to user Start menu");
        }
        var shortcutPaths = new List<string> { ourShortcut, foreignShortcut };
        using var indicator = new VpnWindowIconIndicator(window, taskbarPath, shortcutPaths);
        original = window.Icon!;
        // Linux runs this test only under Xvfb; Windows never shows a test window.
        if (OperatingSystem.IsLinux()) window.Show();
        Require(ReferenceEquals(window.Icon, original), "Disconnected icon");
        var purple = ReadX11Icon(window);
        var purpleWindows = ReadWindowsIcon(window);
        var purpleFile = OperatingSystem.IsWindows() ? File.ReadAllBytes(ReadShortcutIcon(ourShortcut).Path) : [];
        var purpleResource = OperatingSystem.IsWindows() ? ReadShortcutIcon(ourShortcut).Path : "";
        model.ConnectionState = VpnConnectionState.Connected;
        var green = window.Icon;
        Require(!ReferenceEquals(green, original), "Connected icon");
        var greenPixels = ReadX11Icon(window);
        var greenWindows = ReadWindowsIcon(window);
        var greenFile = OperatingSystem.IsWindows() ? File.ReadAllBytes(ReadShortcutIcon(ourShortcut).Path) : [];
        var greenResource = OperatingSystem.IsWindows() ? ReadShortcutIcon(ourShortcut).Path : "";
        model.ConnectionState = VpnConnectionState.Error;
        var red = window.Icon;
        Require(!ReferenceEquals(red, green) && !ReferenceEquals(red, original), "Error icon");
        var redPixels = ReadX11Icon(window);
        var redWindows = ReadWindowsIcon(window);
        if (OperatingSystem.IsWindows())
        {
            var redResource = ReadShortcutIcon(ourShortcut).Path;
            Require(purpleResource != greenResource && greenResource != redResource, "Each colour uses a fresh Shell image resource");
            Require(!purpleFile.SequenceEqual(greenFile), "Shortcut resource pixels change");
            Require(!greenFile.SequenceEqual(File.ReadAllBytes(redResource)), "Shortcut red icon pixels");
            Require(ReadShortcutIcon(foreignShortcut) == (foreignTarget, 7), "Other application shortcuts untouched");
            Require(ReadShellString(window, 5) == "DiVintyInteractive.DiTunnel", "Shell application identity");
            Require(ReadShellString(window, 3) == redResource + ",0", "Shell pinned group icon resource");
            Require(ReadShellString(window, 2) == $"\"{Environment.ProcessPath}\"", "Shell relaunch command");
            SendMessage(window.TryGetPlatformHandle()!.Handle, RegisterWindowMessage("TaskbarButtonCreated"), IntPtr.Zero, IntPtr.Zero);
            Dispatcher.UIThread.RunJobs();
            Require(ReadShellString(window, 3) == redResource + ",0", "Shell icon restored after Explorer recreates its button");
            Require(!purpleFile.SequenceEqual(greenFile) && !greenFile.SequenceEqual(File.ReadAllBytes(redResource)), "Persistent pinned icon changes");
            Require(!purpleWindows.SequenceEqual(greenWindows) && !greenWindows.SequenceEqual(redWindows), "Native Windows large icon updates");
            Require(greenWindows.Count(pixel => Channel(pixel, 8) > Channel(pixel, 16) + 20 && Channel(pixel, 8) > Channel(pixel, 0) + 20) > 10, "Windows green tunnel");
            Require(redWindows.Count(pixel => Channel(pixel, 16) > Channel(pixel, 8) + 20 && Channel(pixel, 16) > Channel(pixel, 0) + 20) > 10, "Windows red tunnel");
        }
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
        var lateShortcut = Path.ChangeExtension(taskbarPath, ".pinned-late.lnk");
        if (OperatingSystem.IsWindows())
        {
            // Pinning can copy the current resource after the last state update.
            CreateShortcut(lateShortcut, Environment.ProcessPath!, ReadShortcutIcon(ourShortcut));
            shortcutPaths.Add(lateShortcut);
        }
        indicator.Dispose(); indicator.Dispose();
        if (OperatingSystem.IsWindows())
        {
            Require(ReadShortcutIcon(ourShortcut) == (Environment.ProcessPath!, 0), "Original shortcut icon restored on exit");
            Require(ReadShortcutIcon(lateShortcut) == (Environment.ProcessPath!, 0), "Shortcut pinned after last update restored on exit");
            Require(ReadShortcutIcon(foreignShortcut) == (foreignTarget, 7), "Foreign shortcut unchanged after exit");
            foreach (var path in Directory.GetFiles(Path.GetDirectoryName(taskbarPath)!))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            Directory.Delete(Path.GetDirectoryName(taskbarPath)!);
        }
        replacement.ConnectionState = VpnConnectionState.Connected;
        Require(ReferenceEquals(window.Icon, red), "Disposal unsubscribes state changes");
        window.Close();
        Console.WriteLine(OperatingSystem.IsLinux() ? "PASS_NATIVE_X11_VPN_ICON_COLOURS_CACHE_DISPOSAL" : "PASS_NATIVE_WINDOWS_VPN_ICON_COLOURS_SHELL_SHORTCUTS_FOREIGN_ISOLATION_RESTORATION");
    }

    private static void VerifyWindowThemeAndBounds(Window window)
    {
        var handle = window.TryGetPlatformHandle()!.Handle;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            window.RequestedThemeVariant = theme;
            Dispatcher.UIThread.RunJobs();
            DesktopWindowStyle.Update(window);
            if (OperatingSystem.IsWindows())
            {
                Require(DwmGetWindowAttribute(handle, 20, out var dark, sizeof(int)) == 0, "Native caption theme attribute");
                Require(dark == (theme == ThemeVariant.Dark ? 1 : 0), "Native caption theme updates immediately");
            }
            else if (OperatingSystem.IsLinux()) Require(ReadThemeHint(window) == (theme == ThemeVariant.Dark ? "dark" : "light"), "X11 caption theme hint updates immediately");
        }
        if (OperatingSystem.IsWindows())
        {
            var original = WindowsWindowBounds.Capture(handle)!.Value;
            WindowsWindowBounds.Restore(handle, new PixelRect(original.X, original.Y, original.Width, Math.Max(100, original.Height - 40)));
            WindowsWindowBounds.Restore(handle, original);
            Require(WindowsWindowBounds.Capture(handle) == original, "Exact physical outer bounds preserved");
        }
        Console.WriteLine("PASS_SETTINGS_SECTIONS_NATIVE_THEME_AND_WINDOW_BOUNDS");
    }

    private static string ReadThemeHint(Window window)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        try
        {
            var atom = XInternAtom(display, "_GTK_THEME_VARIANT", false);
            var result = XGetWindowProperty(display, window.TryGetPlatformHandle()!.Handle, atom, 0, 64,
                false, IntPtr.Zero, out _, out var format, out var count, out _, out var data);
            try
            {
                Require(result == 0 && format == 8 && count is > 0 and < 64, "X11 caption hint format");
                return Marshal.PtrToStringUTF8(data, (int)count)!;
            }
            finally { if (data != IntPtr.Zero) XFree(data); }
        }
        finally { XCloseDisplay(display); }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

    private static int Channel(ulong pixel, int shift) => (int)(pixel >> shift & 255);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [SupportedOSPlatform("windows")]
    private static void CreateShortcut(string path, string target, (string Path, int Index) icon)
    {
        using var shortcut = new WindowsShellShortcut { Target = target, Icon = icon };
        shortcut.Save(path);
    }

    [SupportedOSPlatform("windows")]
    private static (string Path, int Index) ReadShortcutIcon(string path)
    {
        using var shortcut = new WindowsShellShortcut();
        shortcut.Load(path);
        return shortcut.Icon;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadShellString(Window window, uint id)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window.TryGetPlatformHandle()!.Handle, ref iid, out var store));
        try
        {
            var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try { Require(value.Type == 31, "Shell property string ABI"); return Marshal.PtrToStringUni(value.Pointer); }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropertyVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyVariant value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);

    private static ulong[] ReadWindowsIcon(Window window)
    {
        if (!OperatingSystem.IsWindows()) return [];
        Dispatcher.UIThread.RunJobs();
        var icon = SendMessage(window.TryGetPlatformHandle()!.Handle, 0x7f, new IntPtr(1), IntPtr.Zero);
        Require(icon != IntPtr.Zero, "Windows ICON_BIG");
        return ReadIconPixels(icon);
    }

    private static ulong[] ReadIconPixels(IntPtr icon)
    {
        Require(GetIconInfo(icon, out var info), "Windows icon bitmap");
        try
        {
            Require(GetObject(info.Color, Marshal.SizeOf<NativeBitmap>(), out var bitmap) != 0, "Windows icon size");
            Require(bitmap.Width is >= 16 and <= 64 && bitmap.Height == bitmap.Width, "Native taskbar icon size");
            var header = new BitmapInfo { Size = 40, Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, BitCount = 32 };
            var pixels = new uint[bitmap.Width * bitmap.Height];
            var dc = GetDC(IntPtr.Zero);
            try { Require(GetDIBits(dc, info.Color, 0, (uint)bitmap.Height, pixels, ref header, 0) == bitmap.Height, "Windows icon pixels"); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            Console.WriteLine($"Windows icon {bitmap.Width}x{bitmap.Height}: {pixels.Distinct().Count()} colours");
            return pixels.Select(pixel => (ulong)pixel).ToArray();
        }
        finally { DeleteObject(info.Color); DeleteObject(info.Mask); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int Icon; public uint X, Y; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, uint[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr value, int size, out NativeBitmap bitmap);

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
