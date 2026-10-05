using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using DiTunnel.App;

namespace DiTunnel.Android;

// Keep the UI process alive during user-initiated connect/switch commands. BroadcastReceiver
// GoAsync has a short deadline and cannot safely own a VPN handshake or a 30-second start.
[Service(Name = "com.divintyinteractive.ditunnel.VpnControlService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class VpnControlService : Service
{
    private const string Channel = "ditunnel_controls";
    private const int NotificationId = 1108;
    private int commands;

    public static void Dispatch(Context context, string action, string? key = null) =>
        context.StartForegroundService(new Intent(context, typeof(VpnControlService)).SetAction(action)
            .PutExtra(VpnQuickControls.ProfileExtra, key));

    public static PendingIntent Pending(Context context, int requestCode, string action, PendingIntentFlags flags) =>
        PendingIntent.GetForegroundService(context, requestCode,
            new Intent(context, typeof(VpnControlService)).SetAction(action), flags)!;

    public override IBinder? OnBind(Intent? intent) => null;
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = GetSystemService(NotificationService) as NotificationManager;
        manager?.CreateNotificationChannel(new NotificationChannel(Channel, L.T("Управление VPN"), NotificationImportance.Low));
        var notification = new Notification.Builder(this, Channel).SetSmallIcon(Resource.Drawable.ic_vpn_tile)
            .SetContentTitle("Di-Tunnel").SetContentText(L.T("Изменяем VPN-подключение…"))
            .SetContentIntent(VpnQuickControls.ActivityPendingIntent("android.intent.action.MAIN"))
            .SetOngoing(true).Build();
        if (OperatingSystem.IsAndroidVersionAtLeast(34)) StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else StartForeground(NotificationId, notification);
        Interlocked.Increment(ref commands);
        _ = ExecuteAsync(intent?.Action, intent?.GetStringExtra(VpnQuickControls.ProfileExtra));
        return StartCommandResult.NotSticky;
    }
    private async Task ExecuteAsync(string? action, string? key)
    {
        try
        {
            if (action == VpnQuickControls.RecoverAction) await VpnQuickControls.RecoverAsync();
            else await VpnQuickControls.ExecuteAsync(action, key);
        }
        finally
        {
            if (Interlocked.Decrement(ref commands) == 0)
            {
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            }
        }
    }
}
