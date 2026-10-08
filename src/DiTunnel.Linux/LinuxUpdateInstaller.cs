using System.Diagnostics;
using System.Security.Cryptography;
using DiTunnel.App;

namespace DiTunnel.Linux;

internal sealed class LinuxUpdateInstaller : IUpdateInstaller
{
    private const long MaximumBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<string> DownloadAsync(AppRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var name = $"Di-Tunnel-{ReleaseChecker.FormatVersion(release.Version)}-linux-amd64.deb";
        static bool ValidUrl(string? url, string name) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/Divinty5/ditunnel/releases/download/", StringComparison.OrdinalIgnoreCase)
            && Uri.UnescapeDataString(uri.AbsolutePath.Split('/')[^1]) == name;
        if (!ValidUrl(release.InstallerUrl, name) || !ValidUrl(release.ChecksumUrl, name + ".sha256")) throw new InvalidDataException();
        var checksum = await Client.GetStringAsync(release.ChecksumUrl, cancellationToken);
        var hash = ParseChecksum(checksum, name);
        var directory = Path.Combine(AppPaths.StateDirectory, "updates", ReleaseChecker.FormatVersion(release.Version));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await Client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length > MaximumBytes) throw new InvalidDataException();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += count;
                    if (total > MaximumBytes) throw new InvalidDataException();
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    if (length > 0) progress?.Report((int)Math.Clamp(total * 100 / length.Value, 0, 100));
                }
            }
            await using (var file = File.OpenRead(temporary))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Контрольная сумма пакета не совпала.");
            File.Move(temporary, path, overwrite: true);
            progress?.Report(100);
            return path;
        }
        finally { File.Delete(temporary); }
    }

    internal static string ParseChecksum(string text, string name)
    {
        if (text.Length > 4096) throw new InvalidDataException();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1) throw new InvalidDataException();
        var parts = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit) || parts[1].TrimStart('*') != name) throw new InvalidDataException();
        return parts[0];
    }

    public void Launch(string installerPath)
    {
        if (!Path.IsPathFullyQualified(installerPath) || !File.Exists(installerPath) || !installerPath.EndsWith("-linux-amd64.deb", StringComparison.Ordinal)) throw new ArgumentException();
        // apt resolves native dependencies; pkexec uses the desktop's normal admin
        // prompt. Arguments are passed directly, never through a shell.
        _ = Process.Start(new ProcessStartInfo("/usr/bin/pkexec") { UseShellExecute = false,
            ArgumentList = { "/usr/bin/apt-get", "install", "--yes", "--", installerPath } }) ?? throw new IOException();
    }
}
