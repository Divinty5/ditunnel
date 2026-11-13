using System.Diagnostics;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using DiTunnel.App;

namespace DiTunnel.Desktop;

internal sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    private const long MaximumBytes = 512L * 1024 * 1024;
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
        var checksumText = await RetryDownloadAsync(token => Client.GetStringAsync(release.ChecksumUrl, token), cancellationToken);
        string expectedHash;
        try { expectedHash = ParseChecksum(checksumText, fileName); }
        catch (InvalidDataException) { throw new UpdateInstallException("GitHub вернул некорректный файл SHA-256."); }
        if (File.Exists(installerPath) && await HasExpectedHashAsync(installerPath, expectedHash, cancellationToken))
        {
            progress?.Report(100);
            return installerPath;
        }
        await RetryDownloadAsync(async token =>
        {
            var temporaryPath = installerPath + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                using var response = await Client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                if (length is <= 0 or > MaximumBytes) throw new UpdateInstallException("Недопустимый размер установщика Windows.");
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        received += read;
                        if (received > MaximumBytes) throw new UpdateInstallException("Недопустимый размер установщика Windows.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        if (length > 0) progress?.Report((int)Math.Min(99, received * 100 / length.Value));
                    }
                }
                if (!await HasExpectedHashAsync(temporaryPath, expectedHash, token))
                    throw new UpdateInstallException("Контрольная сумма загруженного установщика не совпала.");
                File.Move(temporaryPath, installerPath, true);
                return true;
            }
            finally { File.Delete(temporaryPath); }
        }, cancellationToken);
        progress?.Report(100);
        return installerPath;
    }

    private static async Task<T> RetryDownloadAsync<T>(Func<CancellationToken, Task<T>> download, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            try { return await download(deadline.Token); }
            catch (HttpRequestException error) when (error.StatusCode is null or HttpStatusCode.RequestTimeout || (int?)error.StatusCode >= 500) { }
            catch (HttpIOException) { }
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            return await download(deadline.Token);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        { throw new UpdateInstallException("Файлы обновления больше недоступны на GitHub. Проверьте обновления ещё раз."); }
        catch (Exception error) when (error is HttpRequestException or HttpIOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { throw new UpdateInstallException("Не удалось загрузить обновление с GitHub. Проверьте подключение к сети."); }
    }

    private static async Task<bool> HasExpectedHashAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken))
            .Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    public async Task LaunchAsync(string installerPath)
    {
        var id = Guid.NewGuid().ToString("N");
        var security = new EventWaitHandleSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new(identity.User!, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.Modify, AccessControlType.Allow));
        using var ready = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset,
            @"Local\DiTunnel.Update." + id, out _, security);
        // Inno elevates its own worker and retains the original user for the relaunch.
        var start = new ProcessStartInfo(installerPath) { UseShellExecute = false,
            ArgumentList = { "/ALLUSERS", "/VERYSILENT", "/SP-", "/NORESTART", "/RESTARTEXITCODE=3010", "/DITUNNELUPDATE=" + id } };
        using var process = Process.Start(start) ?? throw new IOException();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (true)
        {
            if (ready.WaitOne(0)) return;
            if (process.HasExited)
            {
                if (ready.WaitOne(0)) return;
                throw new UpdateInstallException(process.ExitCode == 2 ? "Установка обновления отменена."
                    : $"Установка пакета Windows завершилась ошибкой (код {process.ExitCode}).");
            }
            try { await Task.Delay(100, timeout.Token); }
            catch (OperationCanceledException)
            { throw new UpdateInstallException("Установщик Windows не подтвердил готовность. Повторите обновление."); }
        }
    }

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
