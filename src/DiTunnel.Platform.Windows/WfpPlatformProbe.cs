using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;

namespace DiTunnel.Platform.Windows;

/// <summary>
/// Read-only preflight for the Windows Filtering Platform. Installing filters is deliberately
/// kept out of this class; callers must first have a verified, privileged network host.
/// </summary>
public static unsafe class WfpPlatformProbe
{
    private const uint RpcCAuthnWinnt = 10;
    public static WfpAvailability Check()
    {
        if (!OperatingSystem.IsWindows()) return new(false, "Windows Filtering Platform is unavailable on this OS.");
        FWPM_ENGINE_HANDLE engine = default;
        var result = PInvoke.FwpmEngineOpen0(null, RpcCAuthnWinnt, null, null, &engine);
        if (result != 0)
            return new(false, new Win32Exception(unchecked((int)result)).Message, result);
        try { return new(true); }
        finally { _ = PInvoke.FwpmEngineClose0(engine); }
    }
}

public sealed record WfpAvailability(bool IsAvailable, string? Error = null, uint? ErrorCode = null);
