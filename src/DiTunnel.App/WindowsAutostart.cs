using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DiTunnel.App;

[SupportedOSPlatform("windows")]
internal static class WindowsAutostart
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Di-Tunnel";

    internal static string Command(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"')) throw new ArgumentException("Invalid executable path.");
        return $"\"{executable}\" --autostart";
    }

    internal static void SetEnabled(bool enabled, string executable, string keyPath = RunKey)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (enabled) key.SetValue(ValueName, Command(executable), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
