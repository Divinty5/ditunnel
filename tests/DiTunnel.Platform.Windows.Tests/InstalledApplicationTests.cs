using DiTunnel.Platform.Windows;
using System.ComponentModel;
using System.Diagnostics;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class InstalledApplicationTests
{
    [Fact]
    public void PackageManifestResolvesExecutablesWithoutEnumeratingWindowsApps()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiTunnel-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "app"));
        try
        {
            var executable = Path.Combine(root, "app", "ChatGPT.exe");
            File.WriteAllBytes(executable, []);
            File.WriteAllText(Path.Combine(root, "AppxManifest.xml"), """
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
                  <Applications>
                    <Application Id="Codex" Executable="app\ChatGPT.exe">
                      <uap:VisualElements DisplayName="ms-resource:AppName" />
                    </Application>
                    <Application Id="Missing" Executable="missing.exe" />
                    <Application Id="Outside" Executable="..\outside.exe" />
                  </Applications>
                </Package>
                """);
            var application = Assert.Single(WindowsInstalledApplicationProvider.ReadPackageManifest(root, "Codex"));
            Assert.Equal(executable, application.Id);
            Assert.Equal("Codex", application.Name);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RunningTestExecutableIsDiscoveredWithoutShortcutOrRegistration()
    {
        var applications = await new WindowsInstalledApplicationProvider().GetInstalledApplicationsAsync();
        Assert.Contains(applications, app => app.Id.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AccessibleChatGptAndCodexImagesAreDiscoveredWhenRunning()
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("codex")))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is { } path) expected.Add(path); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            }
        }
        var applications = await new WindowsInstalledApplicationProvider().GetInstalledApplicationsAsync();
        Assert.All(expected, path => Assert.Contains(applications, app => app.Id.Equals(path, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task StartMenuAndAppPathsProduceRealUniqueExecutables()
    {
        var applications = await new WindowsInstalledApplicationProvider().GetInstalledApplicationsAsync();
        Assert.Equal(applications.Count, applications.DistinctBy(app => app.Id, StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(applications, app =>
        {
            Assert.True(File.Exists(app.Id));
            Assert.EndsWith(".exe", app.Id, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
        });
    }
}
