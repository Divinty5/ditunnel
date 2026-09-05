namespace DiTunnel.Platform.Windows;

public static class WindowsPlatform
{
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
}
