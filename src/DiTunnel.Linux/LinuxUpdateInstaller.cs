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
        if (File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaximumBytes &&
            await MatchesChecksumAsync(path, hash, cancellationToken))
        {
            progress?.Report(100);
            return path;
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        try
        {
            using var response = await Client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length > MaximumBytes) throw new InvalidDataException();
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Options = FileOptions.Asynchronous, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    total += count;
                    if (total > MaximumBytes) throw new InvalidDataException();
                    await target.WriteAsync(buffer.AsMemory(0, count), token);
                    if (length > 0) progress?.Report((int)Math.Clamp(total * 100 / length.Value, 0, 100));
                }
            }
            if (!await MatchesChecksumAsync(temporary, hash, token))
                throw new InvalidDataException("Контрольная сумма пакета не совпала.");
            File.Move(temporary, path, overwrite: true);
            progress?.Report(100);
            return path;
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<bool> MatchesChecksumAsync(string path, string hash, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(hash, StringComparison.OrdinalIgnoreCase);
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

    public async Task LaunchAsync(string installerPath)
    {
        const string prefix = "Di-Tunnel-";
        const string suffix = "-linux-amd64.deb";
        var name = Path.GetFileName(installerPath);
        if (!Path.IsPathFullyQualified(installerPath) || !File.Exists(installerPath) ||
            name.Length <= prefix.Length + suffix.Length ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal) ||
            !Version.TryParse(name[prefix.Length..^suffix.Length], out var expected))
            throw new ArgumentException("Некорректное имя пакета Linux.", nameof(installerPath));
        // Keep the UI alive until apt finishes. Starting pkexec does not mean dpkg installed the package.
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/pkexec")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "--disable-internal-agent", "/usr/bin/apt-get", "-o", "Dpkg::Use-Pty=0",
                "-o", "DPkg::Lock::Timeout=120", "install", "--yes", "--", installerPath }
        }) ?? throw new IOException();
        await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
            process.StandardError.BaseStream.CopyToAsync(Stream.Null), process.WaitForExitAsync()).ConfigureAwait(false);
        if (process.ExitCode == 126) throw new UpdateInstallException("Установка обновления отменена.");
        if (process.ExitCode == 127) throw new UpdateInstallException("Linux не подтвердил права для установки. Проверьте системный запрос пароля.");
        if (process.ExitCode != 0) throw new UpdateInstallException($"Установка пакета Linux завершилась ошибкой apt (код {process.ExitCode}).");
        using var query = Process.Start(new ProcessStartInfo("/usr/bin/dpkg-query")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            ArgumentList = { "-W", "-f=${db:Status-Status} ${Version}", "ditunnel:amd64" }
        }) ?? throw new IOException();
        var installed = await query.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await query.WaitForExitAsync().ConfigureAwait(false);
        if (query.ExitCode != 0 || installed != $"installed {ReleaseChecker.FormatVersion(expected)}")
            throw new UpdateInstallException("Linux не подтвердил установку новой версии Di-Tunnel.");
        try
        {
            using var restart = Process.Start(new ProcessStartInfo("/usr/bin/di-tunnel")
                { UseShellExecute = false, ArgumentList = { "--after-update" } }) ?? throw new IOException();
        }
        catch { throw new UpdateInstallException("Пакет установлен. Закройте Di-Tunnel и запустите новую версию."); }
    }
}
