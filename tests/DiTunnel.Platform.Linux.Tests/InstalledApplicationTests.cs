using DiTunnel.Platform.Linux.Desktop;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class InstalledApplicationTests
{
    [Fact]
    public void User_and_sandbox_desktop_directories_are_discovered()
    {
        var roots = LinuxInstalledApplications.ApplicationDirectories("/home/test", key => key switch
        {
            "XDG_DATA_HOME" => "/data", "XDG_DATA_DIRS" => "/custom:/usr/share:relative", _ => null
        }).ToArray();
        Assert.Contains(Path.Combine("/data", "applications"), roots);
        Assert.Contains(Path.Combine("/custom", "applications"), roots);
        Assert.Contains("/var/lib/snapd/desktop/applications", roots);
        Assert.Contains("/var/lib/flatpak/exports/share/applications", roots);
        Assert.DoesNotContain("relative/applications", roots);
    }

    [Fact]
    public void Telegram_env_launcher_preserves_the_actual_command_and_quoted_path()
    {
        var app = LinuxInstalledApplications.Parse("[Desktop Entry]\nType=Application\nName=Telegram\nExec=env BAMF_DESKTOP_FILE_HINT=/test.desktop \"/home/test/Telegram Desktop/Telegram\" -- %u", (args, _) =>
        {
            Assert.Equal(new[] { "/home/test/Telegram Desktop/Telegram", "--", "%u" }, args);
            return args[0];
        });
        Assert.Equal("Telegram", app!.Name);
    }

    [Fact]
    public void Browser_launcher_resolves_to_network_executable_without_executing_script()
    {
        var root = Path.Combine(Path.GetTempPath(), "ditunnel-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var launcher = Path.Combine(root, "yandex-browser");
            File.WriteAllText(launcher, "#!/bin/sh\nexit 99\n");
            var binary = Path.Combine(root, "yandex_browser");
            File.WriteAllBytes(binary, [0x7f, (byte)'E', (byte)'L', (byte)'F']);
            Assert.Equal(binary, LinuxApplicationExecutable.NativeExecutable(launcher));
            var settings = new DiTunnel.App.UserSettings();
            settings.SetSplitTunnelRules(DiTunnel.Core.Connection.SplitTunnelMode.ProxySelected, ["example.test"], [launcher, "/missing/custom-app"]);
            Assert.True(DiTunnel.Linux.Program.NormalizeProcessRules(settings));
            Assert.Equal(new[] { binary, "/missing/custom-app" }, settings.ProxySelectedSplitTunnelProcesses);
            Assert.False(DiTunnel.Linux.Program.NormalizeProcessRules(settings));
            File.Delete(binary);
            Assert.Null(LinuxApplicationExecutable.NativeExecutable(launcher));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Hidden=true")]
    [InlineData("NoDisplay=true")]
    public void Hidden_launchers_do_not_become_split_rules(string setting)
        => Assert.Null(LinuxInstalledApplications.Parse($"[Desktop Entry]\nType=Application\nName=Hidden\nExec=/test\n{setting}", (_, _) => throw new Exception("Not reached")));
}
