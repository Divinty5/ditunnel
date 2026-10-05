using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows;

public sealed class WindowsVpnEngine : IProfileVpnEngine
{
    private readonly Func<SplitTunnelPolicy> splitTunnelPolicy;
    private readonly Func<ConnectionPolicy> connectionPolicy;
    private readonly Func<bool> blockAds;
    private readonly Func<bool> strictAdBlocking;
    private readonly WindowsKillSwitchController killSwitch;
    private readonly SemaphoreSlim gate = new(1, 1);
    private WindowsTunnelHost? host;
    private Task? monitor;
    private string? sessionDirectory;
    private bool stopping;
    private bool cleanupFailed;
    private double? delayMilliseconds;
    private ImportedProfile? reconnectProfile;
    private IPAddress? reconnectServerAddress;
    private CancellationTokenSource reconnectCancellation = new();
    private readonly SemaphoreSlim physicalNetworkAvailable = new(0, 1);
    private int? physicalInterfaceIndex;
    private int exhaustedRecoveryArmed;
    private int reconnectFailures;
    public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
    public event EventHandler<VpnStatus>? StatusChanged;
    public WindowsVpnEngine(Func<SplitTunnelPolicy>? splitTunnelPolicy = null, Func<ConnectionPolicy>? connectionPolicy = null, WindowsKillSwitchController? killSwitch = null, Func<bool>? blockAds = null, Func<bool>? strictAdBlocking = null)
    {
        this.splitTunnelPolicy = splitTunnelPolicy ?? (() => SplitTunnelPolicy.Default);
        this.connectionPolicy = connectionPolicy ?? (() => ConnectionPolicy.Default);
        this.killSwitch = killSwitch ?? new WindowsKillSwitchController();
        this.blockAds = blockAds ?? (() => false);
        this.strictAdBlocking = strictAdBlocking ?? (() => false);
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }
    public bool RequiresAdministrator => !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public bool IsNetworkProtectionActive => killSwitch.Status.State == NetworkProtectionState.Active;

    internal async Task<ActiveAmneziaProbe?> AcquireActiveAmneziaProbeAsync(ImportedProfile profile, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        var leased = false;
        try
        {
            if (Status.State != VpnConnectionState.Connected || host is not { HasExited: false } ||
                sessionDirectory is null || reconnectProfile?.Content != profile.Content ||
                !AmneziaWgProfileConverter.IsAmneziaWg(profile)) return null;
            var port = int.Parse(await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "amneziawg.ready"), cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            using var config = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "amneziawg.json"), cancellationToken));
            var proxy = AmneziaWgProfileConverter.Convert(profile).CreateProxyConfiguration(port,
                config.RootElement.GetProperty("username").GetString()!, config.RootElement.GetProperty("password").GetString()!);
            leased = true;
            return new(proxy, gate);
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or FormatException)
        {
            throw new InvalidOperationException("Активное ядро AmneziaWG восстанавливается. Повторите проверку.");
        }
        finally { if (!leased) gate.Release(); }
    }

    internal sealed class ActiveAmneziaProbe(XrayProfileConfiguration proxy, SemaphoreSlim gate) : IAsyncDisposable
    {
        internal XrayProfileConfiguration Proxy { get; } = proxy;
        private int disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    public async Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        await ValidateProfileAsync(profile, cancellationToken);
        CancelReconnect();
        reconnectProfile = profile;
        var preserveProtection = connectionPolicy().Normalize().KillSwitchEnabled && IsNetworkProtectionActive;
        await ConnectCoreAsync(profile, cancellationToken, preserveProtection,
            preserveProtection ? reconnectServerAddress : null);
    }

    public async Task SwitchAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        await ValidateProfileAsync(profile, cancellationToken);
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
            var configuration = ConvertProfile(profile);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var address = (await Dns.GetHostAddressesAsync(configuration.ServerHost, timeout.Token)).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new NotSupportedException("Для сервера пока требуется IPv4-адрес.");
            transitionAddress = address;
            var policy = splitTunnelPolicy();
            var splitAddresses = await ResolveSplitAddressesAsync(policy, timeout.Token);
            var directAddresses = policy.Mode == SplitTunnelMode.BypassSelected ? splitAddresses : [];
            var protection = KillSwitchConfiguration.Create([address], configuration.ServerPort, configuration.ServerTransport, connectionPolicy().Normalize().AllowLocalNetwork, directAddresses);
            if (policy.Mode != SplitTunnelMode.ProxyAll && policy.Processes.Count > 0)
                protection = protection with { DirectProxyApplicationPath = WindowsRuntime.Find() };
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
        // A failed replacement must preserve already armed protection. Explicit
        // Disconnect (including the UI cancel command) owns filter removal.
        await ConnectCoreAsync(profile, cancellationToken, preserveKillSwitch: true, transitionAddress);
    }

    private async Task ConnectCoreAsync(ImportedProfile profile, CancellationToken cancellationToken, bool preserveKillSwitch, IPAddress? knownServerAddress = null, bool recoveryAttempt = false)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (host is { HasExited: true }) await StopCoreAsync();
            if (host is not null) throw new InvalidOperationException("Сначала отключите активный туннель.");
            if (RequiresAdministrator) throw new InvalidOperationException("Закройте Di-Tunnel и запустите его от имени администратора. Права нужны для TUN-адаптера, маршрутов и DNS.");
            // Clear deterministic stale objects left by an earlier crash before DNS/profile probing.
            if (!preserveKillSwitch) await killSwitch.DeactivateAsync(cancellationToken);
            var awg = AmneziaWgProfileConverter.IsAmneziaWg(profile) ? AmneziaWgProfileConverter.Convert(profile) : null;
            var configuration = ConvertProfile(profile);
            var connection = connectionPolicy().Normalize();
            var policy = splitTunnelPolicy();
            if (policy.Mode == SplitTunnelMode.ProxySelected && policy.Domains.Count == 0 && policy.Processes.Count == 0)
                throw new InvalidOperationException("Для режима «Только выбранные через VPN» добавьте хотя бы один домен или приложение.");
            var runtime = WindowsRuntime.Find();
            Publish(VpnConnectionState.Connecting, awg is null ? "Запускаем Xray и настраиваем системный туннель…" : "Запускаем AmneziaWG и настраиваем системный туннель…");
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
                Publish(VpnConnectionState.Connecting, awg is null ? "Проверяем сервер через Xray (до 12 секунд)…" : "Проверяем сервер через AmneziaWG (до 12 секунд)…");
                await using var bypass = awg is null ? null : await WindowsProbeRouteBypass.CreateAsync(address, sessionDirectory, timeout.Token);
                await using var awgProbe = awg is null ? null : await WindowsAmneziaWgRuntime.StartAsync(awg, address.ToString(), sessionDirectory, bypass?.SourceAddress, timeout.Token);
                delayMilliseconds = await XrayServerProbe.MeasureAsync(awgProbe?.Proxy ?? configuration, address, runtime, configPath, timeout.Token, outboundSourceAddress: bypass?.SourceAddress);
            }
            else delayMilliseconds = null;
            Publish(VpnConnectionState.Connecting, "Запускаем сетевой модуль Windows…");
            if (awg is not null)
            {
                var credentials = WindowsAmneziaWgRuntime.CreateCredentials();
                var readyPath = Path.Combine(sessionDirectory, "amneziawg.ready");
                await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "amneziawg.json"), awg.BuildRuntimeConfiguration(address.ToString(), credentials.Username, credentials.Password, readyPath), timeout.Token);
                configuration = awg.CreateProxyConfiguration(0, credentials.Username, credentials.Password);
            }
            var dnsServers = awg?.TunnelDnsServers().ToArray() ?? ["1.1.1.1", "1.0.0.1"];
            await File.WriteAllTextAsync(configPath, configuration.Build(address.ToString(), true, splitTunnel: policy, tunnelName: tunnelName, blockAds: blockAds(), strictAdBlocking: strictAdBlocking(), dnsServers: dnsServers), timeout.Token);
            // On Windows, `xray run -test` initializes the TUN inbound and therefore creates a
            // short-lived Wintun adapter. Starting the real host immediately afterwards can race
            // that adapter's removal. The SOCKS probe above already validates the profile and
            // outbound; let the single network host create and own the TUN adapter.
            var splitDomains = policy.Domains.Where(domain => !string.IsNullOrWhiteSpace(domain)).Select(SplitTunnelPolicy.NormalizeDomain).Where(domain => domain.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            stopping = false;
            cleanupFailed = false;
            host = new WindowsTunnelHost(new(runtime, configPath, address, tunnelName, policy.Mode, splitAddresses,
                splitDomains, connection.KillSwitchEnabled ? killSwitchReadyPath : null,
                awg is null ? null : WindowsAmneziaWgRuntime.Find(), dnsServers,
                configuration.SupportsIpv4, configuration.SupportsIpv6, policy.Processes.Count > 0));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var directAddresses = policy.Mode == SplitTunnelMode.BypassSelected ? splitAddresses : [];
            var protection = connection.KillSwitchEnabled ? KillSwitchConfiguration.Create([address], configuration.ServerPort, configuration.ServerTransport, connection.AllowLocalNetwork, directAddresses) : null;
            if (protection is not null && policy.Mode != SplitTunnelMode.ProxyAll && policy.Processes.Count > 0)
                protection = protection with { DirectProxyApplicationPath = runtime };
            monitor = MonitorAsync(host, ready, protection, killSwitchReadyPath, policy.Mode == SplitTunnelMode.BypassSelected);
            await ready.Task.WaitAsync(timeout.Token);
        }
        catch (Exception error)
        {
            await StopCoreAsync();
            var cancelled = error is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var recovering = recoveryAttempt && !cancelled;
            Publish(cancelled ? VpnConnectionState.Disconnected : recovering ? VpnConnectionState.Reconnecting : VpnConnectionState.Error,
                cancelled ? "Подключение отменено."
                : recovering ? (IsNetworkProtectionActive
                    ? "Попытка восстановления не удалась. Kill switch сохраняет блокировку…"
                    : "Сеть ещё недоступна. Ожидаем следующую попытку восстановления…")
                : error is InvalidOperationException or NotSupportedException or FormatException or TimeoutException ? error.Message
                : "Подключение не установлено. Проверьте профиль и доступность сервера.");
            throw;
        }
        finally { gate.Release(); }
    }

    private static XrayProfileConfiguration ConvertProfile(ImportedProfile profile) => AmneziaWgProfileConverter.IsAmneziaWg(profile)
        ? AmneziaWgProfileConverter.Convert(profile).CreateProxyConfiguration(0, "pending", "pending")
        : XrayProfileConverter.Convert(profile);

    private static async Task ValidateProfileAsync(ImportedProfile profile, CancellationToken cancellationToken)
    {
        if (AmneziaWgProfileConverter.IsAmneziaWg(profile))
            await WindowsAmneziaWgRuntime.ValidateAsync(AmneziaWgProfileConverter.Convert(profile), cancellationToken);
        else _ = XrayProfileConverter.Convert(profile);
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

    private async Task MonitorAsync(WindowsTunnelHost process, TaskCompletionSource ready, KillSwitchConfiguration? protection, string killSwitchReadyPath, bool permitDynamicDirectAddresses)
    {
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
        while ((line = await process.ReadLineAsync()) is not null)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(line, "^(STAGE_[A-Z_]+|TUNNEL_INTERFACE_[0-9]+|PHYSICAL_INTERFACE_[0-9]+|SPLIT_ADDRESS_[0-9a-fA-F:.]+|ERROR_[A-Za-z0-9_-]+|TAKEOVER_OTHER_VPN|PROBE_WARNING|PROBE_EXCEPTION_[A-Za-z0-9_]+|PROBE_SOCKET_[0-9]+|CONNECTED|STOPPED|CANCELLED)$"))
                await AppendDiagnosticAsync(logPath, line);
            if (line.StartsWith("PHYSICAL_INTERFACE_", StringComparison.Ordinal) && int.TryParse(line[19..], out var physicalIndex))
                physicalInterfaceIndex = physicalIndex;
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
                    host?.RequestStop();
                }
            }
            if (permitDynamicDirectAddresses && protection is not null && line.StartsWith("SPLIT_ADDRESS_", StringComparison.Ordinal)
                && IPAddress.TryParse(line[14..], out var directAddress))
            {
                try { await killSwitch.AddDirectAddressesAsync([directAddress]); }
                catch (Exception updateError)
                {
                    error = $"Не удалось обновить исключения kill switch: {updateError.Message}";
                    host?.RequestStop();
                }
            }
            if (line.StartsWith("STAGE_", StringComparison.Ordinal))
            {
                var message = line switch
                {
                    "STAGE_PRECHECK" => "Проверяем маршруты и настройки Windows…",
                    "STAGE_XRAY" => "Создаём TUN-адаптер (до 20 секунд)…",
                    "STAGE_AMNEZIAWG" => "Запускаем ядро AmneziaWG…",
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
            if (line.StartsWith("ERROR_NATIVE_", StringComparison.Ordinal)) error += $" Код Windows: {line[13..]}.";
            if (line.StartsWith("ERROR_HRESULT_", StringComparison.Ordinal)) error += $" HRESULT Windows: 0x{line[14..]}.";
            if (line.StartsWith("ERROR_XRAY_EXIT_", StringComparison.Ordinal)) error = $"Xray-core завершился во время работы туннеля. Код: {line[16..]}.";
            if (line.StartsWith("ERROR_AWG_EXIT_", StringComparison.Ordinal))
            {
                error = "Ядро AmneziaWG завершилось во время работы туннеля.";
                recoveryReason = VpnRecoveryReason.TunnelProcessExited;
            }
            switch (line)
            {
                case "ERROR_PRECHECK_DNS": error = "Не удалось выполнить проверку DNS Windows."; break;
                case "ERROR_PRECHECK_UPLINK": error = "Не удалось определить физическое подключение к интернету."; break;
                case "ERROR_PRECHECK_SERVER_ROUTE": error = "Не удалось подготовить физический маршрут к VPN-серверу."; break;
                case "ERROR_PRECHECK_OUTBOUND": error = "Не удалось подготовить исходящее соединение VPN."; break;
                case "TAKEOVER_OTHER_VPN": Publish(VpnConnectionState.Connecting, "Обнаружен другой VPN. Переключаем маршруты на Di-Tunnel…"); break;
                case "PROBE_WARNING": probeWarning = true; break;
                case "STOPPED": rollbackConfirmed = true; break;
                case "CONNECTED":
                    wasConnected = true; reconnectFailures = 0;
                    try { File.Delete(Path.Combine(sessionDirectory!, "preserve-kill-switch")); } catch (IOException) { }
                    Publish(VpnConnectionState.Connected, probeWarning ? "Туннель активен. Сервер доступен, но контрольный запрос через TUN не выполнен." : "Туннель активен. Контрольное соединение через TUN выполнено.");
                    ready.TrySetResult();
                    break;
                case "ERROR_DNS_POLICY": error = "Обнаружены существующие правила DNS. Подключение отменено, чтобы не изменять их."; break;
                case "ERROR_VPN_TAKEOVER": error = "Другой VPN блокирует прямой маршрут к серверу. Отключите в нём kill switch или режим блокировки соединений вне VPN."; break;
                case "ERROR_NETWORK_ACCESS_DENIED": error = "Windows блокирует трафик через Di-Tunnel (код 10013). Проверьте kill switch другого VPN и правила сетевой защиты. Подключение отменено; маршруты будут восстановлены."; break;
                case "ERROR_PROBE_ROUTE": error = "Контрольный маршрут проходит вне Di-Tunnel. Подключение отменено; маршруты будут восстановлены."; break;
                case "ERROR_NO_PHYSICAL_UPSTREAM": error = "Не найден активный физический интернет-интерфейс для переключения с другого VPN."; break;
                case "ERROR_NETWORK_CHANGED": recoveryReason = VpnRecoveryReason.NetworkChanged; error = "Сетевой адаптер отключён. Ожидаем восстановления сети."; break;
                case "ERROR_CLEANUP": cleanupFailed = true; error = "Не удалось полностью восстановить сеть. Запустите scripts/Repair-DiTunnelNetwork.ps1 от администратора."; break;
                case "ERROR_TUN": error ??= "Не удалось настроить TUN или проверить соединение с сервером."; break;
            }
        }
        await process.WaitForExitAsync();
        await AppendDiagnosticAsync(logPath, $"EXIT_{process.ExitCode}");
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
        var preserveKillSwitch = File.Exists(Path.Combine(Path.GetDirectoryName(killSwitchReadyPath)!, "preserve-kill-switch"))
            || (wasConnected && !stopping && connectionPolicy().Normalize().KillSwitchEnabled);
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
                ? "Автоматические попытки приостановлены после 10 сбоев. Kill switch сохраняет блокировку; VPN возобновится при восстановлении сети."
                : "Автоматические попытки приостановлены после 10 сбоев. VPN возобновится при восстановлении сети.");
            ArmRecoveryAfterNetworkReturns(profile, reason);
            return;
        }
        var token = reconnectCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (reason is VpnRecoveryReason.NetworkChanged or VpnRecoveryReason.NetworkLost)
                {
                    Publish(VpnConnectionState.Reconnecting, HasPhysicalNetwork()
                        ? "Физическая сеть доступна. Переподключаем VPN…"
                        : "Нет подключения к интернету. Ожидаем восстановления сети…");
                    await WaitForPhysicalNetworkAsync(token);
                }
                else
                {
                    Publish(VpnConnectionState.Reconnecting, $"Сеть изменилась. Повторное подключение через {schedule.Delay.TotalSeconds:0} с…");
                    await Task.Delay(schedule.Delay, token);
                }
                token.ThrowIfCancellationRequested();
                reconnectFailures++;
                await ConnectCoreAsync(profile, token,
                    preserveKillSwitch: policy.KillSwitchEnabled && IsNetworkProtectionActive,
                    knownServerAddress: reconnectServerAddress,
                    recoveryAttempt: true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch
            {
                // Preserve the same intent after a failed reconnect; the bounded schedule prevents a tight loop.
                QueueReconnect(reason);
            }
        }, CancellationToken.None);
    }

    private void ArmRecoveryAfterNetworkReturns(ImportedProfile profile, VpnRecoveryReason reason)
    {
        if (reason is not (VpnRecoveryReason.NetworkChanged or VpnRecoveryReason.NetworkLost)
            || Interlocked.Exchange(ref exhaustedRecoveryArmed, 1) != 0) return;
        var token = reconnectCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (physicalNetworkAvailable.Wait(0)) { }
                while (!HasPhysicalNetwork()) await physicalNetworkAvailable.WaitAsync(token);
                await Task.Delay(TimeSpan.FromMilliseconds(150), token);
                reconnectFailures = 0;
                Interlocked.Exchange(ref exhaustedRecoveryArmed, 0);
                await ConnectCoreAsync(profile, token,
                    preserveKillSwitch: connectionPolicy().Normalize().KillSwitchEnabled && IsNetworkProtectionActive,
                    knownServerAddress: reconnectServerAddress,
                    recoveryAttempt: true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch
            {
                Interlocked.Exchange(ref exhaustedRecoveryArmed, 0);
                QueueReconnect(reason);
            }
        }, CancellationToken.None);
    }

    private void OnNetworkChanged(object? sender, EventArgs args)
    {
        if (!HasPhysicalNetwork() || physicalNetworkAvailable.CurrentCount != 0) return;
        try { physicalNetworkAvailable.Release(); } catch (ObjectDisposedException) { }
    }

    private async Task WaitForPhysicalNetworkAsync(CancellationToken cancellationToken)
    {
        while (!HasPhysicalNetwork()) await physicalNetworkAvailable.WaitAsync(cancellationToken);
        // Let Windows finish assigning the gateway/address announced by NetworkChange.
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
    }

    private bool HasPhysicalNetwork()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(network =>
                network.OperationalStatus == OperationalStatus.Up
                && network.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && (physicalInterfaceIndex is null || network.GetIPProperties().GetIPv4Properties()?.Index == physicalInterfaceIndex)
                && network.GetIPProperties().GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork
                    && !gateway.Address.Equals(IPAddress.Any)));
        }
        catch (NetworkInformationException) { return false; }
    }

    private void CancelReconnect()
    {
        reconnectCancellation.Cancel();
        reconnectCancellation.Dispose();
        reconnectCancellation = new CancellationTokenSource();
        reconnectFailures = 0;
        Interlocked.Exchange(ref exhaustedRecoveryArmed, 0);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelReconnect();
        reconnectProfile = null;
        reconnectServerAddress = null;
        physicalInterfaceIndex = null;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (host is not null) Publish(VpnConnectionState.Disconnecting, "Восстанавливаем маршруты и DNS…");
            await StopCoreAsync();
            await killSwitch.DeactivateAsync(CancellationToken.None);
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
                host.RequestStop();
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
            foreach (var file in new[] { "amneziawg.json", "amneziawg.ready", "amneziawg.ready.tmp" })
            {
                var path = Path.Combine(sessionDirectory, file);
                if (File.Exists(path)) File.Delete(path);
            }
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
    public ValueTask DisposeAsync() => DisposeAsync(preserveProtection: false);

    internal async ValueTask DisposeAsync(bool preserveProtection)
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        try
        {
            if (preserveProtection && IsNetworkProtectionActive)
            {
                CancelReconnect();
                await gate.WaitAsync();
                try
                {
                    if (sessionDirectory is not null)
                        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "preserve-kill-switch"), "READY");
                    await StopCoreAsync();
                }
                finally { gate.Release(); }
            }
            else await DisconnectAsync();
        }
        catch (TimeoutException)
        {
            // This engine lives in the independent broker. Exiting it would close the job
            // and stop the cores before a slow rollback finishes, so keep waiting here.
            if (monitor is not null) await monitor;
            if (!preserveProtection) await DisconnectAsync();
        }
        if (!preserveProtection || !IsNetworkProtectionActive) await killSwitch.DisposeAsync();
        reconnectCancellation.Dispose();
        physicalNetworkAvailable.Dispose();
        gate.Dispose();
    }
}
