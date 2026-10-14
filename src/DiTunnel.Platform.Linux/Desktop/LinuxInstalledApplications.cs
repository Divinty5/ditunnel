using System.Text.RegularExpressions;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Linux.Desktop;

public sealed class LinuxInstalledApplications : IInstalledApplicationProvider
{
    public Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync(CancellationToken token = default)
        => Task.Run(() => ReadApplications(token), token);

    public static string ResolveProcessPath(string path)
    {
        try
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 3 && parts[0] == "snap" && (parts[2] == "current" || parts[2].All(char.IsAsciiDigit)))
            {
                var current = "/snap/" + parts[1] + "/current/" + string.Join('/', parts.Skip(3));
                if (LinuxApplicationExecutable.NativeExecutable(current) is { } updated) return updated;
            }
            return LinuxApplicationExecutable.NativeExecutable(path) ?? path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return path; }
    }

    private static IReadOnlyList<InstalledApplication> ReadApplications(CancellationToken token)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = ApplicationDirectories(home, Environment.GetEnvironmentVariable);
        var result = new Dictionary<string, InstalledApplication>(StringComparer.Ordinal);
        var desktopIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots.Distinct().Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(root, "*.desktop", SearchOption.TopDirectoryOnly).Take(4096))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!desktopIds.Add(Path.GetFileName(file)) || new FileInfo(file).Length > 64 * 1024) continue;
                    var item = Parse(File.ReadAllText(file));
                    if (item is not null) result.TryAdd(item.Id, item);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        return result.Values.OrderBy(value => value.Name).ToArray();
    }
    internal static IEnumerable<string> ApplicationDirectories(string home, Func<string, string?> environment)
    {
        var data = environment("XDG_DATA_HOME");
        yield return Path.Combine(data?.StartsWith('/') == true ? data : Path.Combine(home, ".local/share"), "applications");
        foreach (var root in (environment("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share").Split(':'))
            if (root.StartsWith('/')) yield return Path.Combine(root, "applications");
        yield return "/var/lib/snapd/desktop/applications";
        yield return "/var/lib/flatpak/exports/share/applications";
        yield return Path.Combine(home, ".local/share/flatpak/exports/share/applications");
    }

    public static InstalledApplication? Parse(string text) => Parse(text, LinuxApplicationExecutable.Resolve);

    internal static InstalledApplication? Parse(string text, Func<IReadOnlyList<string>, IReadOnlyDictionary<string,string>, string?> resolve)
    {
        if (text.Length > 64 * 1024) return null;
        var entry = false; var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { entry = line == "[Desktop Entry]"; continue; }
            var separator = line.IndexOf('=');
            if (entry && separator > 0) values.TryAdd(line[..separator], line[(separator + 1)..]);
        }
        if (values.GetValueOrDefault("Type") != "Application" || values.GetValueOrDefault("Hidden") == "true"
            || values.GetValueOrDefault("NoDisplay") == "true" || !values.TryGetValue("Exec", out var exec)) return null;
        var arguments = CommandArguments(exec);
        if (arguments.Count > 0 && Path.GetFileName(arguments[0]) == "env")
            arguments = arguments.Skip(1).SkipWhile(value => value.Contains('=') || value == "--").ToArray();
        var path = resolve(arguments, values);
        if (path is null) return null;
        return new(path, values.GetValueOrDefault("Name") ?? Path.GetFileName(path));
    }

    internal static IReadOnlyList<string> CommandArguments(string value) => Regex.Matches(value, "\"([^\"]*)\"|([^\\s]+)")
        .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToArray();
}
