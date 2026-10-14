using System.Text.RegularExpressions;

namespace DiTunnel.Platform.Linux.Desktop;

// Resolve launchers by reading metadata. Never run desktop files or evaluate shell scripts.
internal static class LinuxApplicationExecutable
{
    internal static string? Resolve(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> entry)
    {
        if (args.Count == 0) return null;
        var binary = args[0];
        if (Path.GetFileName(binary) is "sh" or "bash") return null;
        if (Path.GetFileName(binary) == "flatpak")
        {
            var id = entry.GetValueOrDefault("X-Flatpak");
            var command = args.FirstOrDefault(value => value.StartsWith("--command="))?[10..];
            if (!SafeName(id) || !SafeName(command)) return null;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var root in new[] { Path.Combine(home, ".local/share/flatpak"), "/var/lib/flatpak" })
            {
                var files = Path.Combine(root, "app", id!, "current/active/files");
                var path = Path.Combine(files, "bin", command!);
                for (var depth = 0; depth < 8; depth++)
                {
                    var target = new FileInfo(path).LinkTarget;
                    if (target is null) break;
                    path = target.StartsWith("/app/") ? Path.Combine(files, target[5..]) : Path.GetFullPath(target, Path.GetDirectoryName(path)!);
                }
                if (NativeExecutable(path) is { } found) return found;
            }
            return null;
        }
        if (Path.GetFileName(binary) == "snap" || binary.StartsWith("/snap/bin/"))
        {
            var app = binary.StartsWith("/snap/bin/") ? Path.GetFileName(binary)
                : args.Skip(1).SkipWhile(value => value == "run" || value.StartsWith('-')).FirstOrDefault();
            if (!SafeName(app)) return null;
            var name = app!.Split('.')[0];
            var root = "/snap/" + name + "/current";
            var metadata = Path.Combine(root, "meta/snap.yaml");
            if (!File.Exists(metadata) || new FileInfo(metadata).Length > 256 * 1024) return null;
            var appName = app.Contains('.') ? app[(app.IndexOf('.') + 1)..] : name;
            var section = Regex.Match(File.ReadAllText(metadata), @"(?m)^  " + Regex.Escape(appName) + @":\s*\n((?:[ \t]+[^\n]*\n?)*)");
            var command = Regex.Match(section.Groups[1].Value, @"(?m)^    command:\s*(.+)$").Groups[1].Value.Trim(' ', '\'', '"', '\r');
            var target = LinuxInstalledApplications.CommandArguments(command).FirstOrDefault();
            return target is not null && !target.StartsWith('/') && !target.Split('/').Contains("..")
                ? NativeExecutable(Path.Combine(root, target)) : null;
        }
        var pathToBinary = binary.StartsWith('/') ? binary : new[] { "/usr/local/bin", "/usr/bin", "/bin" }
            .Select(dir => Path.Combine(dir, binary)).FirstOrDefault(File.Exists);
        return pathToBinary is null ? null : NativeExecutable(pathToBinary);
    }

    private static bool SafeName(string? name) => name is not null && Regex.IsMatch(name, @"^[a-zA-Z0-9][\w.-]*$");

    internal static string? NativeExecutable(string path)
    {
        if (!File.Exists(path)) return null;
        path = CanonicalPath(path);
        if (IsElf(path)) return path;
        // Known .deb scripts launch an ELF in the same directory.
        string[] names = Path.GetFileName(path) switch
        {
            "yandex-browser" or "yandex-browser-stable" or "yandex-browser-beta" => ["yandex_browser"],
            "google-chrome" or "google-chrome-stable" => ["chrome"],
            "chromium" or "chromium-browser" => ["chromium", "chrome"],
            "firefox" => ["firefox-bin"],
            "telegram-desktop" => ["Telegram", "telegram-desktop"],
            _ => []
        };
        return names.Select(name => Path.Combine(Path.GetDirectoryName(path)!, name)).FirstOrDefault(IsElf) is { } native ? CanonicalPath(native) : null;
    }

    private static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            current = info.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return current;
    }

    private static bool IsElf(string path)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        return file.Read(signature) == 4 && signature.SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' });
    }
}
