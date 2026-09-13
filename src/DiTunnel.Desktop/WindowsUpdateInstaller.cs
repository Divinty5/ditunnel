using System.Diagnostics;
using System.Security.Cryptography;
using DiTunnel.App;

namespace DiTunnel.Desktop;

internal sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<string> DownloadAsync(AppRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.InstallerUrl is null || release.ChecksumUrl is null)
            throw new InvalidOperationException("В релизе отсутствуют установщик Windows или его SHA-256.");
        var version = ReleaseChecker.FormatVersion(release.Version);
        var directory = Path.Combine(UserSettings.DataDirectory, "Updates", version);
        Directory.CreateDirectory(directory);
        var fileName = $"Di-Tunnel-{version}-Setup-x64.exe";
        var installerPath = Path.Combine(directory, fileName);
        var temporaryPath = installerPath + ".download";
        var checksumText = await Client.GetStringAsync(release.ChecksumUrl, cancellationToken);
        var expectedHash = ParseChecksum(checksumText, fileName);
        using (var response = await Client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (length > 0) progress?.Report((int)Math.Clamp(received * 100 / length.Value, 0, 100));
            }
        }
        string actualHash;
        await using (var downloaded = File.OpenRead(temporaryPath))
            actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporaryPath);
            throw new InvalidDataException("Контрольная сумма загруженного установщика не совпала.");
        }
        File.Move(temporaryPath, installerPath, true);
        progress?.Report(100);
        return installerPath;
    }

    public void Launch(string installerPath) => Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true, Verb = "runas" });

    internal static string ParseChecksum(string text, string expectedFileName)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)
                && (parts.Length == 1 || string.Equals(parts[^1].TrimStart('*'), expectedFileName, StringComparison.OrdinalIgnoreCase)))
                return parts[0];
        }
        throw new InvalidDataException("Файл SHA-256 имеет неподдерживаемый формат.");
    }
}
