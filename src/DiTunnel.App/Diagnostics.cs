using System.IO.Compression;
using System.Text.RegularExpressions;

namespace DiTunnel.App;

public static partial class Diagnostics
{
    // Export only the existing sanitized stage log format. Never copy profiles or runtime configs.
    [GeneratedRegex(@"^\S+ (?:STAGE_[A-Z_]+|ERROR_[A-Za-z0-9_-]+|CONNECTED|STOPPED|CANCELLED|EXIT_-?\d+)$")]
    private static partial Regex SafeLine();

    public static void Export(string destination, string? directory = null)
    {
        directory ??= UserSettings.DataDirectory;
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("version.txt").Open()))
            writer.WriteLine($"Di-Tunnel {UserSettings.Version}\n{Environment.OSVersion}\n{DateTimeOffset.Now:O}");
        var files = new List<string>();
        var current = Path.Combine(directory, "last-network.log");
        if (File.Exists(current)) files.Add(current);
        var history = Path.Combine(directory, "logs");
        if (Directory.Exists(history)) files.AddRange(Directory.GetFiles(history, "network-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(30));
        foreach (var file in files)
        {
            using var input = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            using var output = new StreamWriter(zip.CreateEntry(Path.GetFileName(file)).Open());
            for (var count = 0; count < 10000 && input.ReadLine() is { } line; count++)
                if (SafeLine().IsMatch(line) && DateTimeOffset.TryParse(line.Split(' ', 2)[0], out _)) output.WriteLine(line);
        }
    }
}
