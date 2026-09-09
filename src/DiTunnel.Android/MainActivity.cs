using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace DiTunnel.Android;

[Activity(
    Label = "Di-Tunnel",
    Theme = "@style/Theme.AppCompat.NoActionBar",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        VpnPermissionCoordinator.Instance.Attach(this);
    }

    protected override void OnDestroy()
    {
        VpnPermissionCoordinator.Instance.Detach(this);
        base.OnDestroy();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (!VpnPermissionCoordinator.Instance.TryHandleResult(requestCode, resultCode))
            base.OnActivityResult(requestCode, resultCode, data);
    }
}
