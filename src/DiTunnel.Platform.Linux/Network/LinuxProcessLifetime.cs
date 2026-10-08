using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiTunnel.Infrastructure.Xray;
using Microsoft.Win32.SafeHandles;

namespace DiTunnel.Platform.Linux.Network;

[SupportedOSPlatform("linux")]
public sealed partial class LinuxProcessLifetime : IXrayProcessLifetime
{
    private readonly SafeFileHandle pidfd;
    [LibraryImport("libc", SetLastError = true)] private static partial int pidfd_open(int pid, uint flags);
    [LibraryImport("libc", SetLastError = true)] private static partial int pidfd_send_signal(SafeFileHandle pidfd, int signal, nint info, uint flags);

    public LinuxProcessLifetime(Process process)
    {
        var fd = pidfd_open(process.Id, 0);
        if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        pidfd = new((nint)fd, ownsHandle: true);
    }

    public void RequestShutdown()
    {
        if (pidfd_send_signal(pidfd, 15, 0, 0) < 0 && Marshal.GetLastPInvokeError() != 3) // ESRCH: already exited.
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Dispose() => pidfd.Dispose();
}
