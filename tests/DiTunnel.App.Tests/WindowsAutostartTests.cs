using Microsoft.Win32;

namespace DiTunnel.App.Tests;

public sealed class WindowsAutostartTests
{
    [Fact]
    public void Startup_is_opt_in_and_preserves_minimized_preference()
    {
        var path = Path.Combine(Path.GetTempPath(), "DiTunnel-startup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{}"); var settings = UserSettings.Load(path);
            Assert.False(settings.StartWithWindows); Assert.False(settings.StartMinimized);
            settings.StartWithWindows = true; settings.StartMinimized = true; settings.Save(path);
            var loaded = UserSettings.Load(path);
            Assert.True(loaded.StartWithWindows); Assert.True(loaded.StartMinimized);
            Assert.Equal(settings.NetworkSettingsFingerprint(), loaded.NetworkSettingsFingerprint());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Registration_quotes_spaces_and_removes_only_its_value_in_an_isolated_key()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = @"Software\DiTunnel.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var registry = Registry.CurrentUser.CreateSubKey(key)) registry.SetValue("Foreign", "preserved");
            WindowsAutostart.SetEnabled(true, @"C:\Program Files\Di-Tunnel\Di-Tunnel.exe", key);
            using (var registry = Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("\"C:\\Program Files\\Di-Tunnel\\Di-Tunnel.exe\" --autostart", registry!.GetValue(WindowsAutostart.ValueName));
            WindowsAutostart.SetEnabled(false, @"C:\Program Files\Di-Tunnel\Di-Tunnel.exe", key);
            using (var registry = Registry.CurrentUser.OpenSubKey(key))
            { Assert.Null(registry!.GetValue(WindowsAutostart.ValueName)); Assert.Equal("preserved", registry.GetValue("Foreign")); }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
    }
}
