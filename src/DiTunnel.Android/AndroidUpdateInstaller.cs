using System.Security.Cryptography;
using Android.Content;
using AndroidX.Core.Content;
using DiTunnel.App;

namespace DiTunnel.Android;

internal sealed class AndroidUpdateInstaller(Context context) : IUpdateInstaller
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<string> DownloadAsync(AppRelease release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (release.InstallerUrl is null || release.ChecksumUrl is null)
            throw new InvalidOperationException("В релизе отсутствуют APK или его SHA-256.");
        var version = ReleaseChecker.FormatVersion(release.Version);
        var fileName = $"Di-Tunnel-{version}-arm64.apk";
        var directory = Path.Combine(context.CacheDir!.AbsolutePath, "updates");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var temporary = path + ".download";
        var checksum = await Client.GetStringAsync(release.ChecksumUrl, cancellationToken);
        var expected = ParseChecksum(checksum, fileName);
        if (File.Exists(path) && await HasExpectedHashAsync(path, expected, cancellationToken))
        {
            progress?.Report(100);
            return path;
        }
        if (File.Exists(temporary)) File.Delete(temporary);
        using (var response = await Client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(temporary);
            var buffer = new byte[81920]; long received = 0; int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); received += read;
                if (length > 0) progress?.Report((int)Math.Clamp(received * 100 / length.Value, 0, 100));
            }
        }
        await using var stream = File.OpenRead(temporary);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) { File.Delete(temporary); throw new InvalidDataException(); }
        File.Move(temporary, path, true); progress?.Report(100); return path;
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    public void Launch(string installerPath)
    {
        var uri = FileProvider.GetUriForFile(context, context.PackageName + ".updates", new Java.IO.File(installerPath));
        var intent = new Intent(Intent.ActionView).SetDataAndType(uri, "application/vnd.android.package-archive")
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
        context.StartActivity(intent);
    }

    private static string ParseChecksum(string text, string fileName)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)
                && (parts.Length == 1 || parts[^1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))) return parts[0];
        }
        throw new InvalidDataException();
    }
}
