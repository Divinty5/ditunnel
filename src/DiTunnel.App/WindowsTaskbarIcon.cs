using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DiTunnel.App;

/// <summary>Lets the running taskbar group use the window's full state icon.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsTaskbarIcon : IDisposable
{
    internal const string ApplicationId = "DiVintyInteractive.DiTunnel";
    private readonly Window window;
    private readonly bool enableShellHook;
    private readonly int mode;
    private readonly uint buttonCreated = RegisterWindowMessage("TaskbarButtonCreated");
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource lifetime = new();
    private nint handle;
    private uint attachedProcess;
    private bool resolving, disposed, repaired;
    private DateTime nextAttempt;

    internal static void InitializeProcess(string applicationId = ApplicationId) =>
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(applicationId));

    internal WindowsTaskbarIcon(Window window, bool enableShellHook = true, int mode = 1)
    {
        this.window = window; this.enableShellHook = enableShellHook; this.mode = mode;
        window.Opened += Opened; window.Closed += Closed; timer.Tick += Tick;
        Win32Properties.AddWndProcHookCallback(window, WindowMessage);
        if (window.IsVisible) Opened(null, EventArgs.Empty);
    }

    internal void Update(WindowIcon icon)
    {
        // No shortcut mutation, relaunch properties, button recreation or overlay.
        if (disposed) return;
        if (!ReferenceEquals(window.Icon, icon)) window.Icon = icon;
        if (handle != 0) EnsureAttached();
    }

    private void Opened(object? sender, EventArgs args)
    {
        if (disposed || !enableShellHook) return;
        handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (!repaired) { repaired = true; RepairLegacyShortcuts(); }
        timer.Start(); EnsureAttached();
    }
    private void Closed(object? sender, EventArgs args) => Dispose();

    private static void RepairLegacyShortcuts()
    {
        // One-time recovery of launchers coloured by the previous implementation.
        // State changes never rewrite shortcuts. Custom/foreign icons are preserved.
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var paths = new List<string> { Path.Combine(programs, "Di-Tunnel", "Di-Tunnel.lnk"), Path.Combine(programs, "Di-Tunnel.lnk") };
        var pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");
        try { if (Directory.Exists(pinned)) paths.AddRange(Directory.EnumerateFiles(pinned, "*.lnk")); }
        catch (Exception error) { Trace.TraceWarning("Unable to enumerate legacy Di-Tunnel launchers: {0}", error.Message); }
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                if (WindowsShellShortcut.RepairLegacyIcon(path, Path.Combine(UserSettings.DataDirectory, "Icons"), Environment.ProcessPath!))
                    SHChangeNotify(0x2000, 0x0005 | 0x1000, path, 0);
            }
            catch (Exception error) { Trace.TraceWarning("Unable to restore legacy Di-Tunnel launcher: {0}", error.Message); }
        }
    }
    private void Tick(object? sender, EventArgs args) => EnsureAttached();
    private nint WindowMessage(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == buttonCreated) Dispatcher.UIThread.Post(() => { nextAttempt = DateTime.MinValue; EnsureAttached(); });
        return 0;
    }

    private async void EnsureAttached()
    {
        if (disposed || !enableShellHook || handle == 0 || resolving || DateTime.UtcNow < nextAttempt ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        WindowsShellSymbols.GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out var currentProcess);
        if (currentProcess != 0 && currentProcess == attachedProcess && GetProp(handle, "DiTunnel.ShellIcon.Status.v1") == 1) return;
        resolving = true;
        try
        {
            var resolved = await Task.Run(() => WindowsShellSymbols.ResolveAsync(lifetime.Token), lifetime.Token);
            if (disposed) return;
            WindowsShellSymbols.GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out currentProcess);
            if (currentProcess != resolved.ProcessId) return;
            if (attachedProcess != 0) DiTunnelShellDetach(handle);
            var symbols = resolved.Value;
            var status = DiTunnelShellAttach(handle, mode, ref symbols);
            if (status != 0) throw new InvalidOperationException($"Taskbar icon component returned {status}.");
            attachedProcess = currentProcess;
            // The first update may predate symbol resolution. Re-send the same
            // HICON without changing the HWND, taskbar button, order or focus.
            var icon = SendMessage(handle, 0x7f, 1, 0);
            if (icon != 0) SendMessage(handle, 0x80, 1, icon);
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error)
        {
            nextAttempt = DateTime.UtcNow.AddMinutes(1);
            Trace.TraceWarning("Unable to enable VPN taskbar icon: {0}", error.Message);
        }
        finally { resolving = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); timer.Stop();
        window.Opened -= Opened; window.Closed -= Closed; timer.Tick -= Tick;
        Win32Properties.RemoveWndProcHookCallback(window, WindowMessage);
        if (attachedProcess != 0)
        {
            try { DiTunnelShellDetach(handle); }
            catch (Exception error) { Trace.TraceWarning("Unable to detach VPN taskbar icon: {0}", error.Message); }
        }
        attachedProcess = 0;
        // The native watchdog also handles destruction of the HWND or a crash.
    }

    [DllImport("DiTunnel.ShellIcon.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
    private static extern uint DiTunnelShellAttach(nint window, int mode, ref WindowsShellSymbols.Symbols symbols);
    [DllImport("DiTunnel.ShellIcon.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
    private static extern uint DiTunnelShellDetach(nint window);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string applicationId);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint eventId, uint flags, string item, nint other);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    internal static void SetShortcutApplicationId(object shortcut, string applicationId = ApplicationId)
    {
        var store = (IPropertyStore)shortcut;
        SetString(store, 5, applicationId);
        Marshal.ThrowExceptionForHR(store.Commit());
    }

    private static void SetString(IPropertyStore store, uint id, string text)
    {
        var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
        var value = new PropertyVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(text) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
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

}
