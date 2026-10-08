namespace DiTunnel.App;

public static class AppPaths
{
    private static readonly string defaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiTunnel");
    public static string ConfigDirectory { get; private set; } = defaultDirectory;
    public static string DataDirectory { get; private set; } = defaultDirectory;
    public static string StateDirectory { get; private set; } = defaultDirectory;

    // The entry point must configure paths before accessing UserSettings.Current.
    public static void Configure(string configDirectory, string dataDirectory, string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        if (!Path.IsPathFullyQualified(configDirectory) || !Path.IsPathFullyQualified(dataDirectory)
            || !Path.IsPathFullyQualified(stateDirectory)) throw new ArgumentException("Требуются абсолютные пути.");
        if (UserSettings.IsCurrentLoaded) throw new InvalidOperationException("Каталоги должны быть настроены до чтения настроек.");
        ConfigDirectory = configDirectory;
        DataDirectory = dataDirectory;
        StateDirectory = stateDirectory;
    }
}
