using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace DiTunnel.App;

[SupportedOSPlatform("windows")]
internal sealed class WindowsShellShortcut : IDisposable
{
    private readonly object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
    private IShellLink Link => (IShellLink)instance;
    private IPersistFile File => (IPersistFile)instance;

    internal void Load(string path, bool writable = false) => File.Load(path, writable ? 2 : 0);
    internal void Save(string path) => File.Save(path, true);
    internal string Target
    {
        get
        {
            var text = new StringBuilder(32768);
            Link.GetPath(text, text.Capacity, IntPtr.Zero, 4);
            return Environment.ExpandEnvironmentVariables(text.ToString());
        }
        set => Link.SetPath(value);
    }
    internal (string Path, int Index) Icon
    {
        get
        {
            var text = new StringBuilder(32768);
            Link.GetIconLocation(text, text.Capacity, out var index);
            return (text.ToString(), index);
        }
        set => Link.SetIconLocation(value.Path, value.Index);
    }
    internal void SetApplicationId(string applicationId = WindowsTaskbarIcon.ApplicationId) => WindowsTaskbarIcon.SetShortcutApplicationId(instance, applicationId);

    internal static bool Repair(string path, string cacheDirectory, string target)
    {
        using var shortcut = new WindowsShellShortcut();
        shortcut.Load(path);
        var shortcutTarget = shortcut.Target;
        if (!Path.IsPathFullyQualified(shortcutTarget) ||
            !string.Equals(Path.GetFullPath(shortcutTarget), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return false;
        var id = WindowsTaskbarIcon.GetShortcutApplicationId(shortcut.instance);
        if (!string.IsNullOrEmpty(id) && !id.Equals(WindowsTaskbarIcon.ApplicationId, StringComparison.OrdinalIgnoreCase)) return false;
        var icon = Environment.ExpandEnvironmentVariables(shortcut.Icon.Path);
        var name = Path.GetFileName(icon);
        var legacyIcon = Path.IsPathFullyQualified(icon) &&
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(icon)), Path.GetFullPath(cacheDirectory), StringComparison.OrdinalIgnoreCase) &&
            (name.Equals("taskbar.ico", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("taskbar-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase));
        if (!legacyIcon && !string.IsNullOrEmpty(id)) return false;
        shortcut.Load(path, writable: true);
        if (legacyIcon) shortcut.Icon = (target, 0);
        if (string.IsNullOrEmpty(id)) shortcut.SetApplicationId();
        shortcut.Save(path);
        return true;
    }
    public void Dispose() => Marshal.ReleaseComObject(instance);

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
