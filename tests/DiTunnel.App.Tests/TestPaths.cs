using System.Runtime.CompilerServices;

namespace DiTunnel.App.Tests;

internal static class TestPaths
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // View-model tests save preferences. Keep all of those writes away from
        // the installed application, including when tests run outside a sandbox.
        var root = Path.Combine(Path.GetTempPath(), "DiTunnel-app-tests-" + Guid.NewGuid().ToString("N"));
        AppPaths.Configure(root, root, root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, true); } catch { } };
    }
}
