using System.Text.RegularExpressions;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Linux.Desktop;

public sealed class LinuxInstalledApplications : IInstalledApplicationProvider
{
    public Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync(CancellationToken token = default)
        => Task.Run(() => ReadApplications(token), token);

    private static IReadOnlyList<InstalledApplication> ReadApplications(CancellationToken token)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Path.Combine(home, ".local/share/applications"), "/usr/local/share/applications", "/usr/share/applications" };
        var result = new Dictionary<string, InstalledApplication>(StringComparer.Ordinal);
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(root, "*.desktop", SearchOption.TopDirectoryOnly).Take(4096))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (new FileInfo(file).Length > 64 * 1024) continue;
                    var item = Parse(File.ReadAllText(file));
                    if (item is not null) result.TryAdd(item.Id, item);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        return result.Values.OrderBy(value => value.Name).ToArray();
    }
    public static InstalledApplication? Parse(string text)
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
        var match = Regex.Match(exec, "^(?:\"([^\"]+)\"|([^\\s]+))");
        var binary = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        // A launcher is not proof of the executable handling browser/sandbox traffic.
        if (Path.GetFileName(binary) is "env" or "sh" or "bash" or "flatpak" or "snap" || binary.StartsWith("/snap/")) return null;
        var path = binary.StartsWith('/') ? binary : new[] { "/usr/local/bin", "/usr/bin", "/bin" }.Select(dir => Path.Combine(dir, binary)).FirstOrDefault(File.Exists);
        if (path is null || !File.Exists(path)) return null;
        path = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path);
        return new(path, values.GetValueOrDefault("Name") ?? Path.GetFileName(path));
    }
}
