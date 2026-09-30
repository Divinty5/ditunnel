using System.Diagnostics;
using System.Security.Cryptography;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Platform.Windows;

internal sealed class WindowsAmneziaWgRuntime : IAsyncDisposable
{
    private readonly Process process;
    private readonly string configurationPath;
    private readonly string readyPath;
    private readonly Task<string> stderr;
    public XrayProfileConfiguration Proxy { get; }
    private WindowsAmneziaWgRuntime(Process process, string configurationPath, string readyPath, Task<string> stderr, XrayProfileConfiguration proxy)
    {
        this.process = process; this.configurationPath = configurationPath; this.readyPath = readyPath; this.stderr = stderr; Proxy = proxy;
    }

    internal static string Find()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Runtime", "AmneziaWG", "ditunnel-awg.exe");
        var hashPath = path + ".sha256";
        if (!File.Exists(path) || !File.Exists(hashPath))
            throw new InvalidOperationException("Ядро AmneziaWG не установлено. Выполните scripts/Build-AmneziaWG.ps1 и пересоберите Windows-клиент.");
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(File.ReadAllText(hashPath).Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Контрольная сумма ядра AmneziaWG не совпала. Переустановите Di-Tunnel.");
        return path;
    }

    internal static (string Username, string Password) CreateCredentials() =>
        (Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    internal static async Task ValidateAsync(AmneziaWgProfileConfiguration configuration, CancellationToken cancellationToken)
    {
        var runtime = Find();
        var directory = WindowsRuntime.CreateSession();
        var path = Path.Combine(directory, "amneziawg.json");
        var credentials = CreateCredentials();
        try
        {
            await File.WriteAllTextAsync(path, configuration.BuildRuntimeConfiguration("192.0.2.1", credentials.Username, credentials.Password), cancellationToken);
            using var process = Start(runtime, path, validate: true);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); } }
            await errors;
            if (process.ExitCode != 0 || (await output).Trim() != "VALID")
                throw new FormatException("Ядро AmneziaWG отклонило конфигурацию. Проверьте параметры обфускации и версию протокола сервера.");
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(directory); }
    }

    internal static async Task<WindowsAmneziaWgRuntime> StartAsync(AmneziaWgProfileConfiguration configuration, string serverAddress, string directory, string? sourceAddress, CancellationToken cancellationToken)
    {
        var runtime = Find();
        var path = Path.Combine(directory, "amneziawg.json");
        var ready = Path.Combine(directory, "amneziawg.ready");
        var credentials = CreateCredentials();
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(path, configuration.BuildRuntimeConfiguration(serverAddress, credentials.Username, credentials.Password, ready, sourceAddress), cancellationToken);
            process = Start(runtime, path, validate: false);
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null || !line.StartsWith("READY_", StringComparison.Ordinal) || !int.TryParse(line[6..], out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException("Не удалось запустить ядро AmneziaWG. Проверьте конфигурацию.");
            return new(process, path, ready, stderr, configuration.CreateProxyConfiguration(port, credentials.Username, credentials.Password));
        }
        catch
        {
            if (process is not null) { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); } process.Dispose(); }
            DeleteSessionFiles(path, ready);
            throw;
        }
    }

    private static Process Start(string runtime, string path, bool validate)
    {
        var start = new ProcessStartInfo { FileName = runtime, WorkingDirectory = Path.GetDirectoryName(runtime), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-config"); start.ArgumentList.Add(path);
        if (validate) start.ArgumentList.Add("-validate");
        else { start.ArgumentList.Add("-owner"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        return Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить ядро AmneziaWG.");
    }

    private static void DeleteSessionFiles(string configuration, string ready)
    {
        foreach (var path in new[] { configuration, ready, ready + ".tmp" }) if (File.Exists(path)) File.Delete(path);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); } await stderr; }
        finally { process.Dispose(); DeleteSessionFiles(configurationPath, readyPath); }
    }
}
