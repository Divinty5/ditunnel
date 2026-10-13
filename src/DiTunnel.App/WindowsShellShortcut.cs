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
    internal void SetApplicationId() => WindowsTaskbarIcon.SetShortcutApplicationId(instance);
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
