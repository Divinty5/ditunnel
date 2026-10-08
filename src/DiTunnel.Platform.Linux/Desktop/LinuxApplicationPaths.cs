namespace DiTunnel.Platform.Linux.Desktop;

public sealed record LinuxApplicationPaths(string ConfigDirectory, string DataDirectory, string StateDirectory)
{
    public string ProfileFile => Path.Combine(DataDirectory, "profiles.dat");

    public static LinuxApplicationPaths Discover() => FromEnvironment(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);

    public static LinuxApplicationPaths FromEnvironment(string home, Func<string, string?> getVariable)
    {
        // XDG explicitly requires absolute paths and ignores relative overrides.
        if (!Path.IsPathFullyQualified(home)) throw new ArgumentException("Требуется абсолютный домашний каталог.", nameof(home));
        string DirectoryFor(string variable, string fallback)
        {
            var value = getVariable(variable);
            var root = !string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value) ? value : Path.Combine(home, fallback);
            return Path.Combine(root, "ditunnel");
        }
        return new(DirectoryFor("XDG_CONFIG_HOME", ".config"),
            DirectoryFor("XDG_DATA_HOME", ".local/share"), DirectoryFor("XDG_STATE_HOME", ".local/state"));
    }
}
