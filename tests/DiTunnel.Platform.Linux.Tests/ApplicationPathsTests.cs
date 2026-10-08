using DiTunnel.Platform.Linux.Desktop;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class ApplicationPathsTests
{
    [Fact]
    public void EmptyAndRelativeXdgPathsUseHomeDefaults()
    {
        var home = Path.GetTempPath();
        var paths = LinuxApplicationPaths.FromEnvironment(home, name => name == "XDG_CONFIG_HOME" ? "relative" : "");
        Assert.Equal(Path.Combine(home, ".config", "ditunnel"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(home, ".local/share", "ditunnel"), paths.DataDirectory);
        Assert.Equal(Path.Combine(home, ".local/state", "ditunnel"), paths.StateDirectory);
    }

    [Fact]
    public void AbsoluteOverridesRemainSeparate()
    {
        var root = Path.GetTempPath();
        var paths = LinuxApplicationPaths.FromEnvironment(root, name => Path.Combine(root, name));
        Assert.Equal(Path.Combine(root, "XDG_CONFIG_HOME", "ditunnel"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(root, "XDG_DATA_HOME", "ditunnel", "profiles.dat"), paths.ProfileFile);
        Assert.Equal(Path.Combine(root, "XDG_STATE_HOME", "ditunnel"), paths.StateDirectory);
    }
}
