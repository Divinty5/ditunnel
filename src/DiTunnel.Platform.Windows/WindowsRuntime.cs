using System.Security.AccessControl;
using System.Security.Principal;

namespace DiTunnel.Platform.Windows;

internal static class WindowsRuntime
{
    internal static string Find()
    {
        var runtime = Path.Combine(AppContext.BaseDirectory, "Runtime", "xray.exe");
        if (File.Exists(runtime) && File.Exists(Path.Combine(Path.GetDirectoryName(runtime)!, "wintun.dll"))) return runtime;
        throw new InvalidOperationException("Xray или Wintun не установлены. Пересоберите приложение после scripts/Install-Xray.ps1.");
    }
    internal static string CreateSession()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiTunnel", "runtime", Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        return path;
    }
}
