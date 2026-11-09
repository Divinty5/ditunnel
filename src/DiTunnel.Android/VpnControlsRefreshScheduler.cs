using Android.App;
using Android.Content;
using Android.OS;

namespace DiTunnel.Android;

internal static class VpnControlsRefreshScheduler
{
    private const string Action = "com.divintyinteractive.ditunnel.control.REFRESH";
    internal static void Update(Context context)
    {
        var manager = context.GetSystemService(Context.AlarmService) as AlarmManager;
        if (manager is null) return;
        var intent = new Intent(context, typeof(VpnControlsRefreshReceiver)).SetAction(Action);
        var existing = PendingIntent.GetBroadcast(context, 1704, intent, PendingIntentFlags.NoCreate | PendingIntentFlags.Immutable);
        var needed = VpnWidgets.HasWidgets(context) && (VpnQuickControls.Active || VpnQuickControls.CanRecoverAfterSystemStop);
        if (!needed)
        {
            if (existing is not null) { manager.Cancel(existing); existing.Cancel(); existing.Dispose(); }
            return;
        }
        if (existing is not null) { existing.Dispose(); return; }
        using var refresh = PendingIntent.GetBroadcast(context, 1704, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
        // An inexact, non-wakeup alarm survives UI process eviction. It does not wake
        // a sleeping tablet; a deferred refresh is delivered when Android next permits it.
        manager.SetInexactRepeating(AlarmType.ElapsedRealtime, SystemClock.ElapsedRealtime() + 60000, 60000, refresh);
    }
}

[BroadcastReceiver(Name = "com.divintyinteractive.ditunnel.VpnControlsRefreshReceiver", Exported = false)]
public sealed class VpnControlsRefreshReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        VpnQuickControls.Refresh();
        if (VpnQuickControls.CanRecoverAfterSystemStop) _ = VpnQuickControls.RecoverAsync();
    }
}
