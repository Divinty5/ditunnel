using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace DiTunnel.Platform.Windows.Network;

internal static class NetworkPeer
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    internal static void Check(PipeStream pipe, int expectedPid, bool server)
    {
        uint pid;
        var found = server ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid);
        if (!found || pid != expectedPid) throw new InvalidOperationException("Не удалось подтвердить владельца сетевого соединения.");
    }

    internal static void CheckOwner(Process owner, long startTicks)
    {
        if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != startTicks ||
            !string.Equals(Path.GetFullPath(owner.MainModule!.FileName!), Path.Combine(AppContext.BaseDirectory, "Di-Tunnel.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Сетевой модуль запущен без действующего владельца Di-Tunnel.");
    }
}
