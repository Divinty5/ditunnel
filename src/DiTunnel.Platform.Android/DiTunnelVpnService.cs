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
[MetaData(global::Android.Net.VpnService.ServiceMetaDataSupportsAlwaysOn, Value = "false")]
[Register(ServiceClassName)]
public sealed class DiTunnelVpnService : global::Android.Net.VpnService
{
    public const string ServiceClassName = "com.divintyinteractive.ditunnel.DiTunnelVpnService";
    public const string ActionStart = "com.divintyinteractive.ditunnel.action.START_VPN";
    public const string ActionStop = "com.divintyinteractive.ditunnel.action.STOP_VPN";
    private const string NotificationChannelId = "ditunnel_vpn";
    private const int NotificationId = 1107;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private ParcelFileDescriptor? tunnel;
    private XrayDialerController? dialerController;
    private bool xrayRunning;
    private Intent? startIntent;

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartInForeground();
        if (intent?.Action == ActionStop)
        {
            _ = StopTunnelAsync(stopService: true);
            return StartCommandResult.NotSticky;
        }

        if (intent?.Action == ActionStart)
        {
            startIntent = intent;
            _ = StartTunnelAsync();
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnRevoke()
    {
        _ = StopTunnelAsync(stopService: true);
        base.OnRevoke();
    }

    public override void OnDestroy()
    {
        CloseTunnel();
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private async Task StartTunnelAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (xrayRunning) return;
            var pendingRequest = startIntent is null ? null : AndroidVpnServiceBridge.ReadRequest(startIntent);
            startIntent = null;
            var request = pendingRequest
                ?? throw new InvalidOperationException("Профиль VPN не был передан сервису.");
            var profileConfiguration = XrayProfileConverter.Convert(request.Profile);
            var addresses = await Dns.GetHostAddressesAsync(profileConfiguration.ServerHost);
            var serverAddress = addresses
                .OrderBy(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("Не удалось определить IP-адрес VPN-сервера.");

            var builder = new Builder(this)
                .SetSession("Di-Tunnel")
                .SetMtu(1400)
                .AddAddress("172.19.0.1", 30)
                .AddAddress("fd00:19::1", 126)
                .AddRoute("0.0.0.0", 0)
                .AddRoute("::", 0)
                .AddDnsServer("1.1.1.1")
                .AddDnsServer("2606:4700:4700::1111")
                .SetBlocking(true);
            ApplyApplicationRules(builder, request.SplitTunnelPolicy, PackageName);
            tunnel = builder.Establish()
                ?? throw new InvalidOperationException("Android не создал TUN-интерфейс.");

            dialerController = new XrayDialerController(this);
            global::LibXray.LibXray.RegisterDialerController(dialerController);
            global::LibXray.LibXray.RegisterListenerController(dialerController);
            global::LibXray.LibXray.SetDNS(dialerController, "1.1.1.1:53");

            var configuration = CreateAndroidConfiguration(
                profileConfiguration.Build(serverAddress.ToString(), tun: true, splitTunnel: request.SplitTunnelPolicy),
                tunnel.Fd);
            var response = Invoke("runXrayFromJson", new JsonObject { ["configJSON"] = configuration });
            if (!response.Success) throw new InvalidOperationException(SanitizeError(response.Error));
            xrayRunning = true;
            UpdateNotification("VPN подключён");
            AndroidVpnServiceBridge.PublishStarted(this);
        }
        catch (Exception error)
        {
            CloseTunnel();
            StopForeground(StopForegroundFlags.Remove);
            AndroidVpnServiceBridge.PublishStartFailed(this, error is InvalidOperationException or NotSupportedException or FormatException
                ? error.Message
                : "Не удалось запустить Android VPN.");
            TerminateVpnProcess();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private static void ApplyApplicationRules(Builder builder, DiTunnel.Core.Connection.SplitTunnelPolicy policy, string? ownPackageName)
    {
        if (policy.Mode == DiTunnel.Core.Connection.SplitTunnelMode.ProxyAll) return;
        var packageNames = policy.Processes.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
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

    private async Task StopTunnelAsync(bool stopService)
    {
        await lifecycle.WaitAsync();
        try
        {
            // libXray does not reliably support a second runXrayFromJson after stopXray in the
            // same Android process. Notify the UI first, then terminate the isolated :vpn process;
            // Android will create a clean Go runtime for the next connection.
            AndroidVpnServiceBridge.PublishStopped(this);
            StopForeground(StopForegroundFlags.Remove);
            CloseTunnel();
            if (stopService) TerminateVpnProcess();
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
                "Отключить",
                stopPendingIntent)
            .Build();
    }

    private Notification BuildNotification(string text)
    {
        var builder = new Notification.Builder(this, NotificationChannelId)
            .SetSmallIcon(Resource.Drawable.ic_vpn_status)
            .SetContentTitle("Di-Tunnel")
            .SetContentText(text)
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

    private static string CreateAndroidConfiguration(string source, int tunFileDescriptor)
    {
        var root = JsonNode.Parse(source)?.AsObject()
            ?? throw new FormatException("Xray вернул пустую конфигурацию.");
        root["env"] = new JsonObject
        {
            ["xray.tun.fd"] = tunFileDescriptor.ToString(CultureInfo.InvariantCulture)
        };
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
        return error.Length <= 400 ? error : error[..400];
    }

    private void CreateNotificationChannel()
    {
        var manager = GetSystemService(NotificationService) as NotificationManager
            ?? throw new InvalidOperationException("Android NotificationManager недоступен.");
        var channel = new NotificationChannel(
            NotificationChannelId,
            "VPN-подключение",
            NotificationImportance.Low)
        {
            Description = "Состояние VPN и безопасное отключение Di-Tunnel"
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
