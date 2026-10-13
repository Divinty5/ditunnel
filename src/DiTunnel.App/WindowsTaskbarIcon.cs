using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DiTunnel.App;

/// <summary>Updates Explorer's group icon, including an application's pinned button.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsTaskbarIcon : IDisposable
{
    internal const string ApplicationId = "DiVintyInteractive.DiTunnel";
    private readonly Window window;
    private readonly string iconPath;
    private readonly uint buttonCreated = RegisterWindowMessage("TaskbarButtonCreated");
    private byte[]? current;
    private string? currentResource;
    private readonly IReadOnlyList<string>? shortcutPaths;
    private readonly Dictionary<string, (string Path, int Index)> originalShortcutIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> resources = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    private IntPtr identityHandle;

    internal WindowsTaskbarIcon(Window window, string? iconPath = null, IReadOnlyList<string>? shortcutPaths = null)
    {
        this.window = window;
        this.iconPath = iconPath ?? Path.Combine(UserSettings.DataDirectory, "Icons", "taskbar.ico");
        this.shortcutPaths = shortcutPaths;
        window.Opened += Opened;
        Win32Properties.AddWndProcHookCallback(window, WindowMessage);
    }

    internal void Update(WindowIcon icon)
    {
        using var data = new MemoryStream();
        icon.Save(data);
        var bytes = data.ToArray();
        if (current is not null && current.AsSpan().SequenceEqual(bytes)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        // Explorer caches ICO contents. Each of the three colour variants has
        // its own immutable resource; the application and button identity stay
        // unchanged when the Shell is notified of the updated shortcut.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
        currentResource = Path.ChangeExtension(iconPath, null) + "-" + hash + ".ico";
        if (!System.IO.File.Exists(currentResource)) WriteIcon(bytes, currentResource);
        resources.Add(currentResource);
        current = bytes;
        UpdateShortcuts();
        Refresh();
    }

    private static void WriteIcon(byte[] bytes, string path)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Opened(object? sender, EventArgs args) => RefreshSafely();
    private IntPtr WindowMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == buttonCreated) Dispatcher.UIThread.Post(RefreshSafely);
        return IntPtr.Zero;
    }

    private void RefreshSafely()
    {
        if (disposed) return;
        try { UpdateShortcuts(); Refresh(); }
        catch (Exception error) { Trace.TraceWarning("Unable to refresh VPN taskbar icon: {0}", error.Message); }
    }

    private void Refresh()
    {
        if (current is null || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(handle, ref iid, out var store));
        try
        {
            // Set relaunch properties before the explicit ID, as required by the Shell.
            if (identityHandle != handle)
            {
                SetString(store, 2, $"\"{Environment.ProcessPath}\"");
                SetString(store, 4, "Di-Tunnel");
            }
            SetString(store, 3, currentResource + ",0");
            if (identityHandle != handle) SetString(store, 5, ApplicationId);
            Marshal.ThrowExceptionForHR(store.Commit());
            identityHandle = handle;
        }
        finally { Marshal.ReleaseComObject(store); }
        SHChangeNotify(0x2000, 0x0005 | 0x1000, currentResource!, IntPtr.Zero);
    }

    private IEnumerable<string> ShortcutPaths()
    {
        if (shortcutPaths is not null) return shortcutPaths;
        // The GUI runs without elevation. A user copy of the installed shortcut
        // lets it update its own icon without changing the all-users Start menu.
        var userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        foreach (var relative in new[] { Path.Combine("Di-Tunnel", "Di-Tunnel.lnk"), "Di-Tunnel.lnk" })
        {
            try { EnsureUserShortcut(Path.Combine(commonPrograms, relative), Path.Combine(userPrograms, relative)); }
            catch (Exception error) { Trace.TraceWarning("Unable to prepare Di-Tunnel user shortcut: {0}", error.Message); }
        }
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        if (Directory.Exists(pinned))
            foreach (var path in Directory.EnumerateFiles(pinned, "*.lnk")) result.Add(path);
        foreach (var programs in new[] { userPrograms })
        {
            foreach (var path in new[] { Path.Combine(programs, "Di-Tunnel", "Di-Tunnel.lnk"), Path.Combine(programs, "Di-Tunnel.lnk") })
                if (System.IO.File.Exists(path)) result.Add(path);
        }
        return result;
    }

    internal static void EnsureUserShortcut(string installedShortcut, string userShortcut)
    {
        if (System.IO.File.Exists(userShortcut) || !System.IO.File.Exists(installedShortcut)) return;
        using var shortcut = new WindowsShellShortcut();
        shortcut.Load(installedShortcut);
        if (!IsOurTarget(shortcut.Target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(userShortcut)!);
        System.IO.File.Copy(installedShortcut, userShortcut, overwrite: false);
        var attributes = System.IO.File.GetAttributes(userShortcut);
        System.IO.File.SetAttributes(userShortcut, attributes & ~FileAttributes.ReadOnly);
        SHChangeNotify(0x2, 0x0005 | 0x1000, userShortcut, IntPtr.Zero);
    }

    private static bool IsOurTarget(string target) => !string.IsNullOrWhiteSpace(target) &&
        string.Equals(Path.GetFullPath(target), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    private void UpdateShortcuts()
    {
        if (currentResource is null) return;
        foreach (var path in ShortcutPaths())
        {
            try
            {
                using var shortcut = new WindowsShellShortcut();
                shortcut.Load(path);
                if (!IsOurTarget(shortcut.Target)) continue;
                var old = shortcut.Icon;
                if (!originalShortcutIcons.ContainsKey(path))
                {
                    // Recover a launcher left coloured after an abnormal exit.
                    var belongsToCache = Path.GetFullPath(Environment.ExpandEnvironmentVariables(old.Path.Length == 0 ? Environment.ProcessPath! : old.Path))
                        .StartsWith(Path.ChangeExtension(iconPath, null) + "-", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(old.Path, iconPath, StringComparison.OrdinalIgnoreCase);
                    if (belongsToCache && old.Path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) resources.Add(old.Path);
                    originalShortcutIcons[path] = belongsToCache ? (Environment.ProcessPath!, 0) : old;
                }
                if (old == (currentResource, 0)) continue;
                shortcut.Load(path, writable: true);
                shortcut.Icon = (currentResource, 0);
                shortcut.SetApplicationId();
                shortcut.Save(path);
                SHChangeNotify(0x2000, 0x0005 | 0x1000, path, IntPtr.Zero);
                SHChangeNotify(0x1000, 0x0005 | 0x1000, Path.GetDirectoryName(path)!, IntPtr.Zero);
            }
            catch (Exception error) { Trace.TraceWarning("Unable to update Di-Tunnel shortcut icon: {0}", error.Message); }
        }
    }

    internal static void SetShortcutApplicationId(object shortcut)
    {
        var store = (IPropertyStore)shortcut;
        SetString(store, 5, ApplicationId);
        Marshal.ThrowExceptionForHR(store.Commit());
    }

    private static void SetString(IPropertyStore store, uint id, string text)
    {
        var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
        var value = new PropertyVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(text) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.Opened -= Opened;
        Win32Properties.RemoveWndProcHookCallback(window, WindowMessage);
        // Include a shortcut pinned since the last colour change.
        try { UpdateShortcuts(); }
        catch (Exception error) { Trace.TraceWarning("Unable to inspect Di-Tunnel shortcuts at exit: {0}", error.Message); }
        foreach (var entry in originalShortcutIcons)
        {
            try
            {
                using var shortcut = new WindowsShellShortcut();
                shortcut.Load(entry.Key, writable: true);
                if (!IsOurTarget(shortcut.Target) || !resources.Contains(shortcut.Icon.Path)) continue;
                shortcut.Icon = entry.Value;
                shortcut.Save(entry.Key);
                SHChangeNotify(0x2000, 0x0005 | 0x1000, entry.Key, IntPtr.Zero);
            }
            catch (Exception error) { Trace.TraceWarning("Unable to restore Di-Tunnel shortcut icon: {0}", error.Message); }
        }
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
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint eventId, uint flags, string item, IntPtr other);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}
