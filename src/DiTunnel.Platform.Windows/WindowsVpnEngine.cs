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
    private readonly Func<ConnectionPolicy> connectionPolicy;
    private readonly WindowsKillSwitchController killSwitch;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? host;
    private Task? monitor;
    private string? sessionDirectory;
    private bool stopping;
    private bool cleanupFailed;
    private double? delayMilliseconds;
    private ImportedProfile? reconnectProfile;
    private IPAddress? reconnectServerAddress;
    private CancellationTokenSource reconnectCancellation = new();
    private int reconnectFailures;
    public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
    public event EventHandler<VpnStatus>? StatusChanged;
    public WindowsVpnEngine(Func<SplitTunnelPolicy>? splitTunnelPolicy = null, Func<ConnectionPolicy>? connectionPolicy = null, WindowsKillSwitchController? killSwitch = null)
    {
        this.splitTunnelPolicy = splitTunnelPolicy ?? (() => SplitTunnelPolicy.Default);
        this.connectionPolicy = connectionPolicy ?? (() => ConnectionPolicy.Default);
        this.killSwitch = killSwitch ?? new WindowsKillSwitchController();
    }
    public bool RequiresAdministrator => !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public bool IsNetworkProtectionActive => killSwitch.Status.State == NetworkProtectionState.Active;

    public async Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        CancelReconnect();
        reconnectProfile = profile;
        var preserveProtection = connectionPolicy().Normalize().KillSwitchEnabled && IsNetworkProtectionActive;
        await ConnectCoreAsync(profile, cancellationToken, preserveProtection,
            preserveProtection ? reconnectServerAddress : null);
    }

    public async Task SwitchAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        if (!connectionPolicy().Normalize().KillSwitchEnabled || host is null || !IsNetworkProtectionActive)
        {
            await DisconnectAsync(cancellationToken);
            await ConnectAsync(profile, cancellationToken);
            return;
        }

        CancelReconnect();
        reconnectProfile = profile;
        IPAddress? transitionAddress = null;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = XrayProfileConverter.Convert(profile);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var address = (await Dns.GetHostAddressesAsync(configuration.ServerHost, timeout.Token)).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new NotSupportedException("Для сервера пока требуется IPv4-адрес.");
            transitionAddress = address;
            var policy = splitTunnelPolicy();
            var splitAddresses = await ResolveSplitAddressesAsync(policy, timeout.Token);
            var directAddresses = policy.Mode == SplitTunnelMode.BypassSelected ? splitAddresses : [];
            var protection = KillSwitchConfiguration.Create([address], configuration.ServerPort, configuration.ServerTransport, connectionPolicy().Normalize().AllowLocalNetwork, directAddresses);
            var preservePath = Path.Combine(sessionDirectory!, "preserve-kill-switch");
            await File.WriteAllTextAsync(preservePath, "READY", timeout.Token);
            try { await killSwitch.PrepareTransitionAsync(protection, timeout.Token); }
            catch
            {
                try { File.Delete(preservePath); } catch (IOException) { }
                throw;
            }
            await StopCoreAsync();
        }
        finally { gate.Release(); }

        reconnectServerAddress = transitionAddress;
        try { await ConnectCoreAsync(profile, cancellationToken, preserveKillSwitch: true, transitionAddress); }
        catch
        {
            try { await killSwitch.DeactivateAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private async Task ConnectCoreAsync(ImportedProfile profile, CancellationToken cancellationToken, bool preserveKillSwitch, IPAddress? knownServerAddress = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (host is { HasExited: true }) await StopCoreAsync();
            if (host is not null) throw new InvalidOperationException("Сначала отключите активный туннель.");
            if (RequiresAdministrator) throw new InvalidOperationException("Закройте Di-Tunnel и запустите его от имени администратора. Права нужны для TUN-адаптера, маршрутов и DNS.");
            // Clear deterministic stale objects left by an earlier crash before DNS/profile probing.
            if (!preserveKillSwitch) await killSwitch.DeactivateAsync(cancellationToken);
            var configuration = XrayProfileConverter.Convert(profile);
            var connection = connectionPolicy().Normalize();
            var policy = splitTunnelPolicy();
            if (policy.Mode == SplitTunnelMode.ProxySelected && policy.Domains.Count == 0)
                throw new InvalidOperationException("Для режима «Только выбранные через VPN» добавьте хотя бы один домен.");
            if (connection.KillSwitchEnabled && policy.Mode == SplitTunnelMode.BypassSelected && policy.Processes.Count > 0)
                throw new InvalidOperationException("Kill switch с режимом «Обход выбранных» пока поддерживает домены, но не приложения. Удалите приложения из списка или выберите другой режим.");
            var runtime = WindowsRuntime.Find();
            var script = Path.Combine(AppContext.BaseDirectory, "Network", "Run-Tunnel.ps1");
            if (!File.Exists(script)) throw new InvalidOperationException("Сетевой модуль не найден. Пересоберите Windows-клиент.");
            var cleanupExecutable = Path.Combine(AppContext.BaseDirectory, "Di-Tunnel.exe");
            if (!File.Exists(cleanupExecutable)) throw new InvalidOperationException("Модуль очистки WFP не найден. Пересоберите Windows-клиент.");
            Publish(VpnConnectionState.Connecting, "Запускаем Xray и настраиваем системный туннель…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(75));
            var address = knownServerAddress ?? (await Dns.GetHostAddressesAsync(configuration.ServerHost, timeout.Token)).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new NotSupportedException("Для сервера пока требуется IPv4-адрес.");
            reconnectServerAddress = address;
            var splitAddresses = await ResolveSplitAddressesAsync(policy, timeout.Token);
            sessionDirectory = WindowsRuntime.CreateSession();
            var configPath = Path.Combine(sessionDirectory, "config.json");
            var killSwitchReadyPath = Path.Combine(sessionDirectory, "kill-switch.ready");
            if (preserveKillSwitch)
                await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "preserve-kill-switch"), "READY", timeout.Token);
            var tunnelName = $"DiTunnel-{Guid.NewGuid():N}"[..17];
            if (!preserveKillSwitch)
            {
                Publish(VpnConnectionState.Connecting, "Проверяем сервер через Xray (до 12 секунд)…");
                delayMilliseconds = await XrayServerProbe.MeasureAsync(configuration, address, runtime, configPath, timeout.Token);
            }
            else delayMilliseconds = null;
            Publish(VpnConnectionState.Connecting, "Запускаем сетевой модуль Windows…");
            await File.WriteAllTextAsync(configPath, configuration.Build(address.ToString(), true, splitTunnel: policy, tunnelName: tunnelName), timeout.Token);
            // On Windows, `xray run -test` initializes the TUN inbound and therefore creates a
            // short-lived Wintun adapter. Starting the real host immediately afterwards can race
            // that adapter's removal. The SOCKS probe above already validates the profile and
            // outbound; let the single network host create and own the TUN adapter.
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var splitDomains = policy.Domains.Where(domain => !string.IsNullOrWhiteSpace(domain)).Select(SplitTunnelPolicy.NormalizeDomain).Where(domain => domain.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-RuntimePath", runtime, "-ConfigurationPath", configPath, "-ServerAddress", address.ToString(), "-OwnerProcessId", Environment.ProcessId.ToString(), "-TunnelName", tunnelName, "-SplitTunnelMode", policy.Mode.ToString(), "-SplitAddresses", string.Join(',', splitAddresses.Select(ip => ip.ToString())), "-SplitDomains", string.Join(';', splitDomains), "-KillSwitchReadyPath", connection.KillSwitchEnabled ? killSwitchReadyPath : "", "-CleanupExecutablePath", cleanupExecutable }) start.ArgumentList.Add(argument);
            stopping = false;
            cleanupFailed = false;
            host = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить сетевой модуль.");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var directAddresses = policy.Mode == SplitTunnelMode.BypassSelected ? splitAddresses : [];
            var protection = connection.KillSwitchEnabled ? KillSwitchConfiguration.Create([address], configuration.ServerPort, configuration.ServerTransport, connection.AllowLocalNetwork, directAddresses) : null;
            monitor = MonitorAsync(host, ready, protection, killSwitchReadyPath, policy.Mode == SplitTunnelMode.BypassSelected);
            await ready.Task.WaitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            await StopCoreAsync();
            var cancelled = error is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var recoveringFailClosed = preserveKillSwitch && !cancelled && IsNetworkProtectionActive;
            Publish(cancelled ? VpnConnectionState.Disconnected : recoveringFailClosed ? VpnConnectionState.Reconnecting : VpnConnectionState.Error,
                cancelled ? "Подключение отменено."
                : recoveringFailClosed ? "Попытка восстановления не удалась. Kill switch сохраняет блокировку…"
                : error is InvalidOperationException or NotSupportedException or FormatException or TimeoutException ? error.Message
                : "Подключение не установлено. Проверьте профиль и доступность сервера.");
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

    private async Task MonitorAsync(Process process, TaskCompletionSource ready, KillSwitchConfiguration? protection, string killSwitchReadyPath, bool permitDynamicDirectAddresses)
    {
        var stderr = process.StandardError.ReadToEndAsync();
        string? line;
        string? error = null;
        var rollbackConfirmed = false;
        var probeWarning = false;
        var wasConnected = false;
        var recoveryReason = VpnRecoveryReason.None;
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
            if (System.Text.RegularExpressions.Regex.IsMatch(line, "^(STAGE_[A-Z_]+|TUNNEL_INTERFACE_[0-9]+|SPLIT_ADDRESS_[0-9a-fA-F:.]+|ERROR_[A-Za-z0-9_-]+|PROBE_WARNING|CONNECTED|STOPPED|CANCELLED)$"))
                await AppendDiagnosticAsync(logPath, line);
            if (protection is not null && line.StartsWith("TUNNEL_INTERFACE_", StringComparison.Ordinal) && uint.TryParse(line[17..], out var interfaceIndex))
            {
                try
                {
                    await killSwitch.ActivateAsync(protection, interfaceIndex);
                    await File.WriteAllTextAsync(killSwitchReadyPath, "READY");
                    await AppendDiagnosticAsync(logPath, "KILL_SWITCH_ACTIVE");
                    Publish(VpnConnectionState.Connecting, "Kill switch активен. Настраиваем маршруты…");
                }
                catch (Exception activationError)
                {
                    error = $"Не удалось включить kill switch: {activationError.Message}";
                    ready.TrySetException(new InvalidOperationException(error, activationError));
                    try { await File.WriteAllTextAsync(Path.Combine(sessionDirectory!, "config.json.stop"), "stop"); } catch (IOException) { }
                }
            }
            if (permitDynamicDirectAddresses && protection is not null && line.StartsWith("SPLIT_ADDRESS_", StringComparison.Ordinal)
                && IPAddress.TryParse(line[14..], out var directAddress))
            {
                try { await killSwitch.AddDirectAddressesAsync([directAddress]); }
                catch (Exception updateError)
                {
                    error = $"Не удалось обновить исключения kill switch: {updateError.Message}";
                    try { await File.WriteAllTextAsync(Path.Combine(sessionDirectory!, "config.json.stop"), "stop"); } catch (IOException) { }
                }
            }
            if (line.StartsWith("STAGE_", StringComparison.Ordinal))
            {
                var message = line switch
                {
                    "STAGE_PRECHECK" => "Проверяем маршруты и настройки Windows…",
                    "STAGE_XRAY" => "Создаём TUN-адаптер (до 20 секунд)…",
                    "STAGE_ADDRESSES" => "Назначаем адреса TUN-адаптеру…",
                    "STAGE_ROUTES" => "Настраиваем маршруты IPv4 и IPv6…",
                    "STAGE_DNS_RULE" => "Создаём правило DNS для туннеля…",
                    "STAGE_DNS_CACHE" => "Обновляем кэш DNS…",
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
            if (line.StartsWith("ERROR_ROUTE_CODE_", StringComparison.Ordinal)) error += $" Код route.exe: {line[17..]}.";
            if (line.StartsWith("ERROR_XRAY_EXIT_", StringComparison.Ordinal)) error = $"Xray-core завершился во время работы туннеля. Код: {line[16..]}.";
            if (line.StartsWith("ERROR_ROUTE_", StringComparison.Ordinal))
                error += $" Маршрут: {line[12..] switch { "IPV4_LOW" => "IPv4 0.0.0.0/1", "IPV4_HIGH" => "IPv4 128.0.0.0/1", "IPV6_LOW" => "IPv6 ::/1", "IPV6_HIGH" => "IPv6 8000::/1", _ => "к адресу сервера" }}.";
            if (line.StartsWith("ERROR_ROUTE_REASON_", StringComparison.Ordinal))
                error += $" Причина: {line[19..] switch { "DUPLICATE" => "такой маршрут уже существует", "NOT_FOUND" => "Windows не нашла интерфейс или маршрут", "ACCESS_DENIED" => "недостаточно прав для изменения маршрута", "INVALID_PARAMETER" => "Windows отклонила параметры маршрута", _ => "Windows не расшифровала ответ route.exe" }}.";
            if (line.StartsWith("ERROR_ROUTE_DETAIL_", StringComparison.Ordinal))
            {
                try
                {
                    var encoded = line[19..].Replace('-', '+').Replace('_', '/');
                    encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
                    var detail = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim();
                    if (!string.IsNullOrWhiteSpace(detail)) error += $" Ответ route.exe: {detail}.";
                }
                catch (FormatException) { }
            }
            switch (line)
            {
                case "PROBE_WARNING": probeWarning = true; break;
                case "STOPPED": rollbackConfirmed = true; break;
                case "CONNECTED":
                    wasConnected = true; reconnectFailures = 0;
                    try { File.Delete(Path.Combine(sessionDirectory!, "preserve-kill-switch")); } catch (IOException) { }
                    Publish(VpnConnectionState.Connected, probeWarning ? "Туннель активен. Сервер доступен, но контрольный запрос через TUN не выполнен." : "Туннель активен. Контрольное соединение через TUN выполнено.");
                    ready.TrySetResult();
                    break;
                case "ERROR_DNS_POLICY": error = "Обнаружены существующие правила DNS. Подключение отменено, чтобы не изменять их."; break;
                case "ERROR_OTHER_VPN": error = "Сначала отключите другой VPN: он может конфликтовать с TUN-адаптером и маршрутами Di-Tunnel."; break;
                case "ERROR_NETWORK_CHANGED": recoveryReason = VpnRecoveryReason.NetworkChanged; error = "Сетевой адаптер отключён. Ожидаем восстановления сети."; break;
                case "ERROR_CLEANUP": cleanupFailed = true; error = "Не удалось полностью восстановить сеть. Запустите scripts/Repair-DiTunnelNetwork.ps1 от администратора."; break;
                case "ERROR_TUN": error ??= "Не удалось настроить TUN или проверить соединение с сервером."; break;
            }
        }
        await process.WaitForExitAsync();
        await AppendDiagnosticAsync(logPath, $"EXIT_{process.ExitCode}");
        await stderr; // Raw core/PowerShell errors can contain secrets; never show them in UI.
        if (!rollbackConfirmed)
        {
            if (recoveryReason != VpnRecoveryReason.None && IsNetworkProtectionActive)
            {
                cleanupFailed = false;
                error = "Физическая сеть потеряна. Kill switch сохраняет блокировку; ожидаем автоматического восстановления VPN.";
            }
            else
            {
                cleanupFailed = true;
                error = "Сетевой модуль не подтвердил восстановление сети. Проверьте адаптер DiTunnel; может потребоваться перезагрузка Windows.";
            }
        }
        var preserveKillSwitch = File.Exists(Path.Combine(Path.GetDirectoryName(killSwitchReadyPath)!, "preserve-kill-switch"));
        if (rollbackConfirmed && !preserveKillSwitch)
        {
            try
            {
                await killSwitch.DeactivateAsync();
                await AppendDiagnosticAsync(logPath, "KILL_SWITCH_INACTIVE");
            }
            catch (Exception deactivateError)
            {
                cleanupFailed = true;
                error = $"Не удалось удалить правила kill switch: {deactivateError.Message}";
            }
        }
        ready.TrySetException(new InvalidOperationException(error ?? "Сетевой модуль завершился до подключения."));
        if (wasConnected && !stopping && recoveryReason != VpnRecoveryReason.None)
            Publish(VpnConnectionState.Reconnecting, error ?? "Сеть изменилась. Восстанавливаем VPN…");
        else if (error is not null || !stopping)
            Publish(VpnConnectionState.Error, error ?? "Туннель завершился. Подключитесь заново.");
        else Publish(VpnConnectionState.Disconnected, "VPN отключён. Сетевой модуль подтвердил восстановление сети.");
        if (wasConnected && !stopping && (!cleanupFailed || recoveryReason != VpnRecoveryReason.None))
        {
            if (recoveryReason == VpnRecoveryReason.None && error?.StartsWith("Xray-core завершился", StringComparison.Ordinal) == true)
                recoveryReason = VpnRecoveryReason.TunnelProcessExited;
            QueueReconnect(recoveryReason);
        }
    }

    private void QueueReconnect(VpnRecoveryReason reason)
    {
        var profile = reconnectProfile;
        var policy = connectionPolicy().Normalize();
        var schedule = ReconnectSchedule.CreateForActiveTunnel(reconnectFailures);
        if (reason == VpnRecoveryReason.None || profile is null) return;
        if (schedule is null)
        {
            Publish(VpnConnectionState.Error, IsNetworkProtectionActive
                ? "Автоматическое восстановление остановлено после 10 попыток. Kill switch сохраняет блокировку; повторите подключение вручную."
                : "Автоматическое восстановление остановлено после 10 попыток. Повторите подключение вручную.");
            return;
        }
        var token = reconnectCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                Publish(VpnConnectionState.Reconnecting, $"Сеть изменилась. Повторное подключение через {schedule.Delay.TotalSeconds:0} с…");
                await Task.Delay(schedule.Delay, token);
                token.ThrowIfCancellationRequested();
                reconnectFailures++;
                await ConnectCoreAsync(profile, token,
                    preserveKillSwitch: policy.KillSwitchEnabled && IsNetworkProtectionActive,
                    knownServerAddress: reconnectServerAddress);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch
            {
                // Preserve the same intent after a failed reconnect; the bounded schedule prevents a tight loop.
                QueueReconnect(reason);
            }
        }, CancellationToken.None);
    }

    private void CancelReconnect()
    {
        reconnectCancellation.Cancel();
        reconnectCancellation.Dispose();
        reconnectCancellation = new CancellationTokenSource();
        reconnectFailures = 0;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelReconnect();
        reconnectProfile = null;
        reconnectServerAddress = null;
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
            var preserve = Path.Combine(sessionDirectory, "preserve-kill-switch");
            if (File.Exists(preserve)) File.Delete(preserve);
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
    public async ValueTask DisposeAsync() { await DisconnectAsync(); await killSwitch.DisposeAsync(); reconnectCancellation.Dispose(); gate.Dispose(); }
}
