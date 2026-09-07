using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Platform.Windows;

public sealed class WindowsVpnEngine : IProfileVpnEngine
{
    private readonly Func<SplitTunnelPolicy> splitTunnelPolicy;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? host;
    private Task? monitor;
    private string? sessionDirectory;
    private bool stopping;
    private bool cleanupFailed;
    private double? delayMilliseconds;
    public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
    public event EventHandler<VpnStatus>? StatusChanged;
    public WindowsVpnEngine(Func<SplitTunnelPolicy>? splitTunnelPolicy = null) => this.splitTunnelPolicy = splitTunnelPolicy ?? (() => SplitTunnelPolicy.Default);
    public bool RequiresAdministrator => !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public async Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (host is { HasExited: true }) await StopCoreAsync();
            if (host is not null) throw new InvalidOperationException("Сначала отключите активный туннель.");
            if (RequiresAdministrator) throw new InvalidOperationException("Закройте Di-Tunnel и запустите его от имени администратора. Права нужны для TUN-адаптера, маршрутов и DNS.");
            var configuration = XrayProfileConverter.Convert(profile);
            var policy = splitTunnelPolicy();
            if (policy.Mode == SplitTunnelMode.ProxySelected && policy.Domains.Count == 0)
                throw new InvalidOperationException("Для режима «Только выбранные через VPN» добавьте хотя бы один домен.");
            var runtime = WindowsRuntime.Find();
            var script = Path.Combine(AppContext.BaseDirectory, "Network", "Run-Tunnel.ps1");
            if (!File.Exists(script)) throw new InvalidOperationException("Сетевой модуль не найден. Пересоберите Windows-клиент.");
            Publish(VpnConnectionState.Connecting, "Запускаем Xray и настраиваем системный туннель…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(75));
            var address = (await Dns.GetHostAddressesAsync(configuration.ServerHost, timeout.Token)).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new NotSupportedException("Для сервера пока требуется IPv4-адрес.");
            var splitAddresses = await ResolveSplitAddressesAsync(policy, timeout.Token);
            sessionDirectory = WindowsRuntime.CreateSession();
            var configPath = Path.Combine(sessionDirectory, "config.json");
            Publish(VpnConnectionState.Connecting, "Проверяем сервер через Xray (до 12 секунд)…");
            delayMilliseconds = await XrayServerProbe.MeasureAsync(configuration, address, runtime, configPath, timeout.Token);
            Publish(VpnConnectionState.Connecting, "Запускаем сетевой модуль Windows…");
            await File.WriteAllTextAsync(configPath, configuration.Build(address.ToString(), true, splitTunnel: policy), timeout.Token);
            await using (var validator = new XrayProcessManager(new XrayOptions { ExecutablePath = runtime, WorkingDirectory = Path.GetDirectoryName(runtime)! }))
            {
                var validation = await validator.ValidateConfigurationAsync(configPath, timeout.Token);
                if (!validation.IsValid) throw new InvalidOperationException("Xray отклонил конфигурацию профиля. Маршруты не изменены.");
            }
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var splitDomains = policy.Domains.Where(domain => !string.IsNullOrWhiteSpace(domain)).Select(SplitTunnelPolicy.NormalizeDomain).Where(domain => domain.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-RuntimePath", runtime, "-ConfigurationPath", configPath, "-ServerAddress", address.ToString(), "-OwnerProcessId", Environment.ProcessId.ToString(), "-SplitTunnelMode", policy.Mode.ToString(), "-SplitAddresses", string.Join(',', splitAddresses.Select(ip => ip.ToString())), "-SplitDomains", string.Join(';', splitDomains) }) start.ArgumentList.Add(argument);
            stopping = false;
            cleanupFailed = false;
            host = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить сетевой модуль.");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor = MonitorAsync(host, ready);
            await ready.Task.WaitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            await StopCoreAsync();
            Publish(error is OperationCanceledException && cancellationToken.IsCancellationRequested ? VpnConnectionState.Disconnected : VpnConnectionState.Error, error is OperationCanceledException && cancellationToken.IsCancellationRequested ? "Подключение отменено." : error is InvalidOperationException or NotSupportedException or FormatException or TimeoutException ? error.Message : "Подключение не установлено. Проверьте профиль и доступность сервера.");
            throw;
        }
        finally { gate.Release(); }
    }

    private static async Task<IPAddress[]> ResolveSplitAddressesAsync(SplitTunnelPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.Mode == SplitTunnelMode.ProxyAll) return [];
        var addresses = new List<IPAddress>();
        foreach (var domain in policy.Domains.Where(domain => !string.IsNullOrWhiteSpace(domain)).Select(SplitTunnelPolicy.NormalizeDomain).Where(domain => domain.Length > 0))
        {
            try { addresses.AddRange((await Dns.GetHostAddressesAsync(domain, cancellationToken)).Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)); }
            catch (SocketException) { }
        }
        if (policy.Domains.Count > 0 && addresses.Count == 0)
            throw new InvalidOperationException("Не удалось получить адреса выбранных доменов. Проверьте написание и интернет-соединение.");
        return addresses.Distinct().ToArray();
    }

    private async Task MonitorAsync(Process process, TaskCompletionSource ready)
    {
        var stderr = process.StandardError.ReadToEndAsync();
        string? line;
        string? error = null;
        var rollbackConfirmed = false;
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiTunnel", "last-network.log");
        try
        {
            var archive = Path.Combine(Path.GetDirectoryName(logPath)!, "logs");
            Directory.CreateDirectory(archive);
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 0)
                File.Copy(logPath, Path.Combine(archive, $"network-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log"));
            await File.WriteAllTextAsync(logPath, "");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(line, "^(STAGE_[A-Z_]+|ERROR_[A-Za-z0-9_-]+|CONNECTED|STOPPED|CANCELLED)$"))
                await AppendDiagnosticAsync(logPath, line);
            if (line.StartsWith("STAGE_", StringComparison.Ordinal))
            {
                var message = line switch
                {
                    "STAGE_PRECHECK" => "Проверяем маршруты и настройки Windows…",
                    "STAGE_XRAY" => "Создаём TUN-адаптер (до 20 секунд)…",
                    "STAGE_ADDRESSES" => "Назначаем адреса TUN-адаптеру…",
                    "STAGE_ADDRESS_READY" => "Ожидаем готовности адреса TUN (до 10 секунд)…",
                    "STAGE_ROUTES" => "Настраиваем маршруты IPv4 и IPv6…",
                    "STAGE_DNS_RULE" => "Создаём правило DNS для туннеля…",
                    "STAGE_DNS_CACHE" => "Обновляем кэш DNS…",
                    "STAGE_ROUTE_CHECK" => "Ожидаем выбора маршрута через TUN (до 5 секунд)…",
                    "STAGE_PROBE" => "Проверяем интернет через TUN (до 15 секунд)…",
                    "STAGE_CLEANUP" => "Восстанавливаем маршруты и DNS…",
                    _ => "Выполняем сетевую операцию…"
                };
                Publish(line == "STAGE_CLEANUP" ? VpnConnectionState.Disconnecting : VpnConnectionState.Connecting, message);
            }
            if (line.StartsWith("ERROR_STAGE_", StringComparison.Ordinal))
                error ??= $"Сбой настройки Windows на этапе {line[12..]}. Маршруты будут восстановлены.";
            if (line.StartsWith("ERROR_COMMAND_", StringComparison.Ordinal)) error += $" Команда: {line[14..]}.";
            if (line.StartsWith("ERROR_NATIVE_", StringComparison.Ordinal)) error += $" Код Windows: {line[13..]}.";
            switch (line)
            {
                case "STOPPED": rollbackConfirmed = true; break;
                case "CONNECTED": Publish(VpnConnectionState.Connected, "Туннель активен. HTTPS-запрос через TUN выполнен."); ready.TrySetResult(); break;
                case "ERROR_DNS_POLICY": error = "Обнаружены существующие правила DNS. Подключение отменено, чтобы не изменять их."; break;
                case "ERROR_OTHER_VPN": error = "Сначала отключите другой VPN: маршрут к серверу проходит через виртуальный адаптер."; break;
                case "ERROR_NETWORK_CHANGED": error = "Сетевой адаптер отключён. Подключитесь заново после восстановления сети."; break;
                case "ERROR_CLEANUP": cleanupFailed = true; error = "Не удалось полностью восстановить сеть. Запустите scripts/Repair-DiTunnelNetwork.ps1 от администратора."; break;
                case "ERROR_TUN": error ??= "Не удалось настроить TUN или проверить соединение с сервером."; break;
            }
        }
        await process.WaitForExitAsync();
        await AppendDiagnosticAsync(logPath, $"EXIT_{process.ExitCode}");
        await stderr; // Raw core/PowerShell errors can contain secrets; never show them in UI.
        if (!rollbackConfirmed)
        {
            cleanupFailed = true;
            error = "Сетевой модуль не подтвердил восстановление сети. Проверьте адаптер DiTunnel; может потребоваться перезагрузка Windows.";
        }
        ready.TrySetException(new InvalidOperationException(error ?? "Сетевой модуль завершился до подключения."));
        if (error is not null || !stopping) Publish(VpnConnectionState.Error, error ?? "Туннель завершился. Подключитесь заново.");
        else Publish(VpnConnectionState.Disconnected, "VPN отключён. Сетевой модуль подтвердил восстановление сети.");
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (host is not null) Publish(VpnConnectionState.Disconnecting, "Восстанавливаем маршруты и DNS…");
            await StopCoreAsync();
            if (cleanupFailed) throw new InvalidOperationException("Не удалось полностью восстановить сеть. Запустите scripts/Repair-DiTunnelNetwork.ps1 от администратора.");
            Publish(VpnConnectionState.Disconnected, "VPN отключён. Маршруты и DNS освобождены.");
        }
        finally { gate.Release(); }
    }
    private async Task StopCoreAsync()
    {
        stopping = true;
        if (host is not null)
        {
            if (!host.HasExited)
            {
                try { await File.WriteAllTextAsync(Path.Combine(sessionDirectory!, "config.json.stop"), "stop"); }
                catch (IOException) { }
            }
            if (monitor is not null)
            {
                try { await monitor.WaitAsync(TimeSpan.FromSeconds(20)); }
                catch (TimeoutException)
                {
                    Publish(VpnConnectionState.Error, "Восстановление сети продолжается в фоне. Окно можно закрыть.");
                    throw new TimeoutException("Сетевой модуль ещё восстанавливает сеть. Окно можно закрыть; дождитесь завершения очистки перед новым подключением.");
                }
            }
            host.Dispose(); host = null; monitor = null;
        }
        if (sessionDirectory is not null)
        {
            var config = Path.Combine(sessionDirectory, "config.json");
            if (File.Exists(config + ".stop")) File.Delete(config + ".stop");
            if (File.Exists(config)) File.Delete(config);
            if (Directory.Exists(sessionDirectory)) Directory.Delete(sessionDirectory);
            sessionDirectory = null;
        }
    }
    private void Publish(VpnConnectionState state, string message)
    {
        Status = new(state, message, state == VpnConnectionState.Connected ? DateTimeOffset.UtcNow : null,
            state == VpnConnectionState.Connected ? delayMilliseconds : null);
        StatusChanged?.Invoke(this, Status);
    }
    private static async Task AppendDiagnosticAsync(string path, string message)
    {
        try { await File.AppendAllTextAsync(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public async ValueTask DisposeAsync() { await DisconnectAsync(); gate.Dispose(); }
}
