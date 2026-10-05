using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using DiTunnel.Infrastructure.Xray;
using Java.Interop;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiTunnel.Platform.Android;

[Service(
    Name = ServiceClassName,
    Exported = false,
    Process = ":vpn",
    Permission = global::Android.Manifest.Permission.BindVpnService,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[IntentFilter([global::Android.Net.VpnService.ServiceInterface])]
[MetaData(global::Android.Net.VpnService.ServiceMetaDataSupportsAlwaysOn, Value = "true")]
[Register(ServiceClassName)]
public sealed class DiTunnelVpnService : global::Android.Net.VpnService
{
    public const string ServiceClassName = "com.divintyinteractive.ditunnel.DiTunnelVpnService";
    public const string ActionStart = "com.divintyinteractive.ditunnel.action.START_VPN";
    public const string ActionStop = "com.divintyinteractive.ditunnel.action.STOP_VPN";
    public const string ActionRefreshNotification = "com.divintyinteractive.ditunnel.action.REFRESH_NOTIFICATION";
    private const string NotificationChannelId = "ditunnel_vpn";
    private const int NotificationId = 1107;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private ParcelFileDescriptor? tunnel;
    private XrayDialerController? dialerController;
    private bool xrayRunning;
    private AndroidAwgBridgeClient? amneziaRuntime;
    private Intent? startIntent;
    private CancellationTokenSource? starting;
    private bool stopRequested;
    private int restarting;
    private int physicalNetworkId;
    private PhysicalNetworkMonitor? networkMonitor;
    private CancellationTokenSource? networkEvaluation;
    private bool OwnsRecovery => !stopRequested;
    private VpnStartupStage startupStage;
    private void Stage(VpnStartupStage stage)
    {
        startupStage = stage;
        AndroidVpnStartupDiagnostics.Record(this, stage);
    }

    public override void OnCreate()
    {
        base.OnCreate();
        AndroidVpnRuntimeState.SetStarting(this, OwnsRecovery);
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionRefreshNotification)
        {
            CreateNotificationChannel();
            if (xrayRunning) UpdateNotification(amneziaRuntime is not null ? "VPN подключён · AmneziaWG" : "VPN подключён");
            else if (starting is not null && !stopRequested) UpdateNotification("Восстанавливаем VPN…");
            else { stopRequested = true; StopSelf(); }
            return OwnsRecovery ? StartCommandResult.Sticky : StartCommandResult.NotSticky;
        }
        StartInForeground();
        if (intent?.Action == ActionStop)
        {
            stopRequested = true;
            StopNetworkMonitor();
            starting?.Cancel();
            _ = StopTunnelAsync(stopService: true);
            return StartCommandResult.NotSticky;
        }

        if (intent?.Action == ActionStart || intent is null || intent.Action == global::Android.Net.VpnService.ServiceInterface)
        {
            stopRequested = false;
            startIntent = intent;
            _ = StartTunnelAsync();
            return OwnsRecovery ? StartCommandResult.Sticky : StartCommandResult.NotSticky;
        }
        stopRequested = true;
        StopSelf();
        return StartCommandResult.NotSticky;
    }

    public override void OnRevoke()
    {
        stopRequested = true;
        StopNetworkMonitor();
        starting?.Cancel();
        _ = StopTunnelAsync(stopService: true);
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        starting?.Cancel();
        StopNetworkMonitor();
        if (OwnsRecovery) AndroidVpnRuntimeState.SetRecovering(this);
        else AndroidVpnRuntimeState.Clear(this);
        CloseTunnel();
        StopForeground(StopForegroundFlags.Remove);
        if (OwnsRecovery) AndroidVpnServiceBridge.PublishRecovering(this);
        else AndroidVpnServiceBridge.PublishStopped(this);
        base.OnDestroy();
    }

    private async Task StartTunnelAsync()
    {
        await lifecycle.WaitAsync();
        var manualRequest = startIntent?.Action == ActionStart;
        try
        {
            if (xrayRunning || amneziaRuntime is not null)
            {
                AndroidVpnServiceBridge.PublishStarted(this);
                return;
            }
            var persisted = startIntent is null ? null : AndroidVpnServiceBridge.RequestFromIntent(startIntent);
            persisted ??= await Task.Run(() => new AndroidVpnResumeStore(this).Load());
            startIntent = null;
            var saved = persisted ?? throw new InvalidOperationException("Сначала подключите VPN вручную, чтобы сохранить профиль для постоянного VPN.");
            if (stopRequested) throw new System.OperationCanceledException();
            var request = (Profile: saved.Profile, SplitTunnelPolicy: new DiTunnel.Core.Connection.SplitTunnelPolicy(saved.Mode, saved.Domains, saved.Processes),
                saved.BlockAds, saved.StrictAdBlocking);
            starting?.Dispose();
            starting = new CancellationTokenSource();
            // Validate before replacing the saved request. Recovery must use the latest
            // selected profile, including its current routing and ad-blocking settings.
            if (AmneziaWgProfileConverter.IsAmneziaWg(request.Profile))
                _ = AmneziaWgProfileConverter.Convert(request.Profile).TunnelDnsServers();
            else _ = XrayProfileConverter.Convert(request.Profile);
            await Task.Run(() => new AndroidVpnResumeStore(this).Save(saved), starting.Token);
            if (OwnsRecovery && PhysicalNetwork() is null)
            {
                AndroidVpnRuntimeState.SetRecovering(this);
                AndroidVpnServiceBridge.PublishRecovering(this);
                while (PhysicalNetwork() is null) await Task.Delay(1000, starting.Token);
            }
            starting.Token.ThrowIfCancellationRequested();
            Stage(VpnStartupStage.Profile);
            var isAmneziaWg = AmneziaWgProfileConverter.IsAmneziaWg(request.Profile);
            var profileConfiguration = isAmneziaWg
                ? await StartAmneziaWgAsync(request.Profile, starting.Token)
                : XrayProfileConverter.Convert(request.Profile);
            starting.Token.ThrowIfCancellationRequested();
            if (!isAmneziaWg) Stage(VpnStartupStage.Endpoint);
            var addresses = profileConfiguration.IsLocalProxy ? [IPAddress.Loopback]
                : await Dns.GetHostAddressesAsync(profileConfiguration.ServerHost, starting.Token);
            var serverAddress = addresses
                .OrderBy(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("Не удалось определить IP-адрес VPN-сервера.");

            var awgConfiguration = isAmneziaWg ? AmneziaWgProfileConverter.Convert(request.Profile) : null;
            var supportsIpv4 = awgConfiguration?.SupportsAddressFamily(System.Net.Sockets.AddressFamily.InterNetwork) ?? true;
            var supportsIpv6 = awgConfiguration?.SupportsAddressFamily(System.Net.Sockets.AddressFamily.InterNetworkV6) ?? true;
            IReadOnlyList<string> dnsServers = awgConfiguration?.TunnelDnsServers() ?? ["1.1.1.1", "2606:4700:4700::1111"];
            starting.Token.ThrowIfCancellationRequested();
            Stage(VpnStartupStage.Tun);
            var builder = new Builder(this)
                .SetSession("Di-Tunnel")
                .SetMtu(1400)
                .SetBlocking(true);
            // Advertising an unsupported family makes browsers attempt connections that
            // the AWG netstack cannot carry. Android blocks omitted families by default;
            // do not allow them to escape through the physical network.
            if (supportsIpv4) builder.AddAddress("172.19.0.1", 30).AddRoute("0.0.0.0", 0);
            if (supportsIpv6) builder.AddAddress("fd00:19::1", 126).AddRoute("::", 0);
            foreach (var dns in dnsServers) builder.AddDnsServer(dns);
            ApplyApplicationRules(builder, request.SplitTunnelPolicy, PackageName);
            tunnel = builder.Establish()
                ?? throw new InvalidOperationException("Android не создал TUN-интерфейс.");

            dialerController = new XrayDialerController(this);
            global::LibXray.LibXray.RegisterDialerController(dialerController);
            global::LibXray.LibXray.RegisterListenerController(dialerController);
            global::LibXray.LibXray.SetDNS(dialerController, "1.1.1.1:53");

            var xrayPolicy = PolicyForXray(request.SplitTunnelPolicy);
            var assetDirectory = request.BlockAds ? EnsureXrayAssets() : null;
            var configuration = CreateAndroidConfiguration(
                profileConfiguration.Build(serverAddress.ToString(), tun: true, splitTunnel: xrayPolicy, blockAds: request.BlockAds, strictAdBlocking: request.StrictAdBlocking, dnsServers: dnsServers),
                tunnel.Fd, assetDirectory);
            starting.Token.ThrowIfCancellationRequested();
            Stage(VpnStartupStage.Xray);
            var response = await Task.Run(() => Invoke("runXrayFromJson", new JsonObject { ["configJSON"] = configuration }), starting.Token);
            if (!response.Success) throw new InvalidOperationException(SanitizeError(response.Error));
            xrayRunning = true;
            starting.Token.ThrowIfCancellationRequested();
            Stage(VpnStartupStage.Connected);
            await Task.Run(() => new AndroidVpnResumeStore(this).Save(saved), starting.Token);
            AndroidVpnRuntimeState.SetConnected(this, request.Profile, OwnsRecovery);
            UpdateNotification(isAmneziaWg ? "VPN подключён · AmneziaWG" : "VPN подключён");
            AndroidVpnServiceBridge.PublishStarted(this);
            StartNetworkMonitor();
        }
        catch (Exception error)
        {
            AndroidVpnRuntimeState.Clear(this);
            CloseTunnel();
            StopForeground(StopForegroundFlags.Remove);
            var message = error is InvalidOperationException or NotSupportedException or FormatException or TimeoutException
                ? error.Message
                : "Не удалось запустить Android VPN.";
            if (!manualRequest && OwnsRecovery && PersistedRequestAvailable()) _ = RestartForRecoveryAsync();
            else
            {
                var wasStopping = stopRequested;
                stopRequested = true;
                StopSelf();
                if (wasStopping) AndroidVpnServiceBridge.PublishStopped(this);
                else AndroidVpnServiceBridge.PublishStartFailed(this, $"{message} ({startupStage}; {error.GetType().Name})");
                TerminateVpnProcess();
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private bool PersistedRequestAvailable()
    {
        try { return new AndroidVpnResumeStore(this).Load() is not null; }
        catch { return false; }
    }

    private global::Android.Net.Network? PhysicalNetwork() => GetSystemService(ConnectivityService) is global::Android.Net.ConnectivityManager manager
        ? AndroidXrayProbeRunner.FindPhysicalNetwork(manager) : null;

    private void StartNetworkMonitor()
    {
        if (!OwnsRecovery || networkMonitor is not null) return;
        if (amneziaRuntime is null) physicalNetworkId = PhysicalNetwork()?.GetHashCode() ?? 0;
        var manager = (global::Android.Net.ConnectivityManager)GetSystemService(ConnectivityService)!;
        networkMonitor = new PhysicalNetworkMonitor(ScheduleNetworkEvaluation);
        using var builder = new global::Android.Net.NetworkRequest.Builder();
        manager.RegisterNetworkCallback(builder.AddCapability(global::Android.Net.NetCapability.Internet)!
            .AddCapability(global::Android.Net.NetCapability.NotVpn)!.Build()!, networkMonitor);
    }

    private void ScheduleNetworkEvaluation()
    {
        networkEvaluation?.Cancel();
        networkEvaluation = new CancellationTokenSource();
        _ = EvaluateNetworkAsync(networkEvaluation.Token);
    }

    private async Task EvaluateNetworkAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            if (!OwnsRecovery || !xrayRunning) return;
            var current = PhysicalNetwork();
            if ((current?.GetHashCode() ?? 0) == physicalNetworkId) return;
            if (amneziaRuntime is not null || current is null) await RestartForRecoveryAsync();
            else { SetUnderlyingNetworks([current]); physicalNetworkId = current.GetHashCode(); }
        }
        catch (System.OperationCanceledException) { }
        catch { if (OwnsRecovery) await RestartForRecoveryAsync(); }
    }

    private async Task RestartForRecoveryAsync()
    {
        if (!OwnsRecovery || Interlocked.Exchange(ref restarting, 1) != 0) return;
        AndroidVpnRuntimeState.SetRecovering(this);
        AndroidVpnServiceBridge.PublishRecovering(this);
        UpdateNotification("Восстанавливаем VPN…");
        // Keep this foreground process alive while offline. Killing it here relied on
        // vendor Sticky restart scheduling and could leave recovery waiting forever.
        // The independent foreground controller waits for physical network callbacks,
        // sends STOP, and starts a fresh native process without an Activity.
        try
        {
            StartForegroundService(new Intent().SetComponent(new ComponentName(PackageName!,
                "com.divintyinteractive.ditunnel.VpnControlService"))
                .SetAction("com.divintyinteractive.ditunnel.control.RECOVER"));
        }
        catch
        {
            Interlocked.Exchange(ref restarting, 0);
            await Task.Delay(2000);
            if (OwnsRecovery) _ = RestartForRecoveryAsync();
        }
    }

    private void StopNetworkMonitor()
    {
        networkEvaluation?.Cancel();
        if (networkMonitor is not null && GetSystemService(ConnectivityService) is global::Android.Net.ConnectivityManager manager)
        {
            try { manager.UnregisterNetworkCallback(networkMonitor); } catch (Java.Lang.IllegalArgumentException) { }
            networkMonitor.Dispose();
        }
        networkMonitor = null;
    }

    private sealed class PhysicalNetworkMonitor(Action changed) : global::Android.Net.ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(global::Android.Net.Network network) => changed();
        public override void OnLost(global::Android.Net.Network network) => changed();
        public override void OnCapabilitiesChanged(global::Android.Net.Network network, global::Android.Net.NetworkCapabilities capabilities) => changed();
    }

    private async Task<XrayProfileConfiguration> StartAmneziaWgAsync(DiTunnel.Core.Profiles.ImportedProfile profile, CancellationToken cancellationToken)
    {
        Stage(VpnStartupStage.Endpoint);
        var configuration = AmneziaWgProfileConverter.Convert(profile);
        var manager = GetSystemService(ConnectivityService) as global::Android.Net.ConnectivityManager
            ?? throw new InvalidOperationException("Сетевой сервис Android недоступен.");
        var network = AndroidXrayProbeRunner.FindPhysicalNetwork(manager)
            ?? throw new InvalidOperationException("Нет доступной физической сети.");
        var endpoint = IPAddress.TryParse(configuration.ServerHost, out var ip) ? ip : null;
        if (endpoint is null)
        {
            // Physical-network DNS may block. Never hold Android's main service thread
            // during a widget/tile command, and leave time for the handshake/start bridge.
            try
            {
                endpoint = await Task.Run(() => (network.GetAllByName(configuration.ServerHost) ?? [])
                    .Select(address => IPAddress.Parse(address.HostAddress!))
                    .OrderBy(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault(), cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
            catch (TimeoutException) { throw new InvalidOperationException("Не удалось определить IP-адрес VPN-сервера."); }
        }
        if (endpoint is null) throw new InvalidOperationException("Не удалось определить IP-адрес VPN-сервера.");
        cancellationToken.ThrowIfCancellationRequested();
        SetUnderlyingNetworks([network]);
        physicalNetworkId = network.GetHashCode();
        Stage(VpnStartupStage.AwgTransport);
        amneziaRuntime = new AndroidAwgBridgeClient(this, configuration, endpoint.ToString(), Stage);
        amneziaRuntime.Disconnected += () =>
        {
            if (stopRequested) return;
            starting?.Cancel();
            if (!xrayRunning) return; // StartAsync will report the failed startup to its caller.
            AndroidVpnRuntimeState.Clear(this);
            if (OwnsRecovery) _ = RestartForRecoveryAsync();
            else
            {
                StopSelf();
                AndroidVpnServiceBridge.PublishStartFailed(this, "Служебный процесс AmneziaWG завершился.");
                TerminateVpnProcess();
            }
        };
        return await amneziaRuntime.StartAsync(cancellationToken);
    }

    private static void ApplyApplicationRules(Builder builder, DiTunnel.Core.Connection.SplitTunnelPolicy policy, string? ownPackageName)
    {
        if (policy.Mode == DiTunnel.Core.Connection.SplitTunnelMode.ProxyAll) return;
        var packageNames = policy.Processes.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
        if (packageNames.Count == 0) return;
        // With an allow-list Android otherwise routes Di-Tunnel itself outside the VPN. libXray
        // still calls ProtectFd for its outbound sockets and treats Android's `false` as fatal.
        // Include the service package so ProtectFd can explicitly move those sockets outside TUN.
        if (policy.Mode == DiTunnel.Core.Connection.SplitTunnelMode.ProxySelected && !string.IsNullOrWhiteSpace(ownPackageName))
            packageNames.Add(ownPackageName);
        foreach (var packageName in packageNames.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (policy.Mode == DiTunnel.Core.Connection.SplitTunnelMode.BypassSelected)
                    builder.AddDisallowedApplication(packageName);
                else
                    builder.AddAllowedApplication(packageName);
            }
            catch (PackageManager.NameNotFoundException) { }
        }
    }

    private static DiTunnel.Core.Connection.SplitTunnelPolicy PolicyForXray(DiTunnel.Core.Connection.SplitTunnelPolicy policy)
    {
        // Android's allow-list already limits TUN to the selected applications. Those packets
        // must then use Xray's proxy by default; package names cannot be matched as desktop
        // process names inside a TUN inbound.
        if (policy.Mode == DiTunnel.Core.Connection.SplitTunnelMode.ProxySelected && policy.Processes.Count > 0)
            return DiTunnel.Core.Connection.SplitTunnelPolicy.Default;
        return new(policy.Mode, policy.Domains, []);
    }

    private async Task StopTunnelAsync(bool stopService)
    {
        await lifecycle.WaitAsync();
        try
        {
            // libXray does not reliably support a second runXrayFromJson after stopXray in the
            // same Android process. Notify the UI first, then terminate the isolated :vpn process;
            // Android will create a clean Go runtime for the next connection.
            if (xrayRunning)
            {
                try { _ = Invoke("stopXray", new JsonObject()); }
                catch { /* The isolated process is terminated below even if native shutdown fails. */ }
            }
            if (xrayRunning) { try { global::LibXray.LibXray.ResetDNS(); } catch { } }
            AndroidVpnRuntimeState.Clear(this);
            StopForeground(StopForegroundFlags.Remove);
            CloseTunnel();
            if (stopService) StopSelf();
            AndroidVpnServiceBridge.PublishStopped(this);
            // The UI process terminates this isolated process only after it has received
            // STOPPED. Killing here raced Android's asynchronous broadcast delivery and
            // caused intermittent ten-second stop timeouts during switch/recovery.
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private void StartInForeground()
    {
        var notification = BuildNotification("VPN готовится к запуску");

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);
    }

    private Notification.Action CreateStopAction()
    {
        var stopIntent = new Intent(this, typeof(DiTunnelVpnService)).SetAction(ActionStop);
        var stopPendingIntent = PendingIntent.GetService(
            this,
            0,
            stopIntent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)
            ?? throw new InvalidOperationException("Android не создал действие отключения VPN.");
        return new Notification.Action.Builder(
                Icon.CreateWithResource(this, Resource.Drawable.ic_vpn_status),
                AndroidLocalization.T("Отключить"),
                stopPendingIntent)
            .Build();
    }

    private Notification BuildNotification(string text)
    {
        var builder = new Notification.Builder(this, NotificationChannelId)
            .SetSmallIcon(Resource.Drawable.ic_vpn_status)
            .SetContentTitle("Di-Tunnel")
            .SetContentText(AndroidLocalization.T(text))
            .SetOngoing(true)
            .SetCategory(Notification.CategoryService);
        builder.AddAction(CreateStopAction());
        return builder.Build();
    }

    private void UpdateNotification(string text)
    {
        var manager = GetSystemService(NotificationService) as NotificationManager;
        manager?.Notify(NotificationId, BuildNotification(text));
    }

    private void CloseTunnel()
    {
        if (amneziaRuntime is not null)
        {
            try { amneziaRuntime.Dispose(); } catch { }
            amneziaRuntime = null;
        }
        xrayRunning = false;
        dialerController = null;
        tunnel?.Close();
        tunnel?.Dispose();
        tunnel = null;
    }

    private static void TerminateVpnProcess()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
        });
    }

    private string EnsureXrayAssets()
    {
        var directory = Path.Combine(FilesDir?.AbsolutePath ?? throw new InvalidOperationException("Каталог приложения Android недоступен."), "xray-assets");
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "geoip.dat", "geosite.dat" })
        {
            var destination = Path.Combine(directory, name);
            if (File.Exists(destination) && new FileInfo(destination).Length > 0) continue;
            using var source = Assets?.Open(name) ?? throw new InvalidOperationException($"Ресурс Xray {name} отсутствует.");
            using var target = File.Create(destination);
            source.CopyTo(target);
        }
        return directory;
    }

    private static string CreateAndroidConfiguration(string source, int tunFileDescriptor, string? assetDirectory)
    {
        var root = JsonNode.Parse(source)?.AsObject()
            ?? throw new FormatException("Xray вернул пустую конфигурацию.");
        var environment = new JsonObject
        {
            ["xray.tun.fd"] = tunFileDescriptor.ToString(CultureInfo.InvariantCulture)
        };
        if (assetDirectory is not null) environment["xray.location.asset"] = assetDirectory;
        root["env"] = environment;
        var settings = root["inbounds"]?[0]?["settings"]?.AsObject()
            ?? throw new FormatException("В конфигурации Xray отсутствует TUN inbound.");
        settings["name"] = "DiTunnel";
        settings.Remove("autoOutboundsInterface");
        return root.ToJsonString();
    }

    private static InvokeResponse Invoke(string method, JsonObject payload)
    {
        var request = new JsonObject
        {
            ["apiVersion"] = 1,
            ["method"] = method,
            ["payload"] = payload
        }.ToJsonString();
        var responseJson = global::LibXray.LibXray.Invoke(request)
            ?? throw new InvalidOperationException("libXray не вернул ответ.");
        using var response = JsonDocument.Parse(responseJson);
        var root = response.RootElement;
        return new(
            root.TryGetProperty("success", out var success) && success.GetBoolean(),
            root.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    private static string SanitizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "libXray не запустил VPN.";
        var message = error.Length <= 400 ? error : error[..400];
        return message.StartsWith("xray ", StringComparison.Ordinal)
            ? "Xray " + message[5..]
            : message;
    }

    private void CreateNotificationChannel()
    {
        var manager = GetSystemService(NotificationService) as NotificationManager
            ?? throw new InvalidOperationException("Android NotificationManager недоступен.");
        var channel = new NotificationChannel(
            NotificationChannelId,
            AndroidLocalization.T("VPN-подключение"),
            NotificationImportance.Low)
        {
            Description = AndroidLocalization.T("Состояние VPN и безопасное отключение Di-Tunnel")
        };
        manager.CreateNotificationChannel(channel);
    }

    private sealed record InvokeResponse(bool Success, string? Error);

    private sealed class XrayDialerController(DiTunnelVpnService service) : Java.Lang.Object, global::LibXray.IDialerController
    {
        public bool ProtectFd(long fileDescriptor)
        {
            return fileDescriptor is >= 0 and <= int.MaxValue && service.Protect((int)fileDescriptor);
        }
    }
}
