using Android.App;
using Android.Content;

namespace DiTunnel.Android;

// An explicit, same-UID broadcast also refreshes controls when Android has evicted the
// UI process. Restoring the tunnel itself remains owned by the isolated VPN service.
[BroadcastReceiver(Name = "com.divintyinteractive.ditunnel.VpnStatusReceiver", Exported = false)]
public sealed class VpnStatusReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent) => VpnQuickControls.Refresh();
}
