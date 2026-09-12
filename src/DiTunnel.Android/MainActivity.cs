using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
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
        Window?.SetSoftInputMode(SoftInput.AdjustResize);
        VpnPermissionCoordinator.Instance.Attach(this);
        QrScannerCoordinator.Instance.Attach(this);
    }

    protected override void OnDestroy()
    {
        VpnPermissionCoordinator.Instance.Detach(this);
        QrScannerCoordinator.Instance.Detach(this);
        base.OnDestroy();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (!VpnPermissionCoordinator.Instance.TryHandleResult(requestCode, resultCode)
            && !QrScannerCoordinator.Instance.TryHandleResult(requestCode, resultCode, data))
            base.OnActivityResult(requestCode, resultCode, data);
    }

#pragma warning disable CS0672, CA1422
    public override void OnBackPressed()
    {
        if (!App.AppBackNavigation.TryGoBack()) base.OnBackPressed();
    }
#pragma warning restore CS0672, CA1422
}
