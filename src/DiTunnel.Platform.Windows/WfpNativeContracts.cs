using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;

namespace DiTunnel.Platform.Windows;

// Compile-time guard: these layouts are generated from the installed Windows SDK rather than
// re-declared by hand. The controller is added only after this contract remains stable.
internal static class WfpNativeContracts
{
    internal static int FilterSize => System.Runtime.InteropServices.Marshal.SizeOf<FWPM_FILTER0>();
    internal static int SubLayerSize => System.Runtime.InteropServices.Marshal.SizeOf<FWPM_SUBLAYER0>();
    internal static int ProviderSize => System.Runtime.InteropServices.Marshal.SizeOf<FWPM_PROVIDER0>();
    internal static Guid ConnectV4Layer => global::Windows.Win32.PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4;
    internal static Guid ConnectV6Layer => global::Windows.Win32.PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6;
}
