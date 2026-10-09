using Android.App;
using Android.Content;
using Android.Service.QuickSettings;
using Android.Graphics.Drawables;
using Android.OS;

namespace DiTunnel.Android;

[Service(Name = "com.divintyinteractive.ditunnel.VpnTileService", Label = "Di-Tunnel", Icon = "@drawable/ic_vpn_tile",
    Exported = true, Permission = "android.permission.BIND_QUICK_SETTINGS_TILE")]
[IntentFilter(["android.service.quicksettings.action.QS_TILE"])]
[MetaData("android.service.quicksettings.ACTIVE_TILE", Value = "true")]
[MetaData("android.service.quicksettings.TOGGLEABLE_TILE", Value = "true")]
public sealed class VpnTileService : TileService
{
    private readonly Handler handler = new(Looper.MainLooper!);
    private bool listening;
    public override void OnStartListening()
    {
        base.OnStartListening();
        if (!listening) VpnQuickControls.Refreshed += OnRefreshed;
        listening = true;
        Update();
    }
    public override void OnStopListening()
    {
        listening = false;
        VpnQuickControls.Refreshed -= OnRefreshed;
        base.OnStopListening();
    }
    public override void OnDestroy()
    {
        listening = false;
        VpnQuickControls.Refreshed -= OnRefreshed;
        base.OnDestroy();
    }
    private void OnRefreshed() => handler.Post(() => { if (listening) Update(); });
    public override void OnClick()
    {
        base.OnClick();
        if (IsLocked) { UnlockAndRun(new Java.Lang.Runnable(HandleClick)); return; }
        HandleClick();
    }
    private void HandleClick()
    {
        if (VpnQuickControls.NeedsActivity)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(34)) StartActivityAndCollapse(VpnQuickControls.ActivityPendingIntent());
            else
#pragma warning disable CA1422
                StartActivityAndCollapse(VpnQuickControls.ActivityIntent());
#pragma warning restore CA1422
            return;
        }
        VpnControlService.Dispatch(this, VpnQuickControls.ToggleAction);
    }
    private void Update()
    {
        if (QsTile is not { } tile) return;
        tile.Label = "Di-Tunnel";
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            tile.Subtitle = VpnQuickControls.StatusText;
        tile.State = !VpnQuickControls.CanToggle ? TileState.Unavailable
            : VpnQuickControls.Status.State == DiTunnel.Core.Connection.VpnConnectionState.Connected ? TileState.Active : TileState.Inactive;
        tile.Icon = Icon.CreateWithResource(this, Resource.Drawable.ic_vpn_tile);
        tile.UpdateTile();
    }
}
