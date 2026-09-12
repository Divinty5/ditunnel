using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using DiTunnel.App.ViewModels;
using DiTunnel.Platform.Android;

namespace DiTunnel.Android;

[Application]
public sealed class AndroidApp : AvaloniaAndroidApplication<App.App>
{
    public AndroidApp(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
        AndroidLocaleCoordinator.Initialize(this);
        App.QrScanner.ScanAsync = QrScannerCoordinator.Instance.ScanAsync;
        var engine = new AndroidVpnEngine(
            this,
            VpnPermissionCoordinator.Instance,
            () => App.UserSettings.Current.GetSplitTunnelPolicy());
        App.App.CreateMainViewModel = () => new MainViewModel(
            engine,
            store: new AndroidProfileStore(this),
            probe: new AndroidServerProbe(this),
            countryResolver: new AndroidServerCountryResolver(this),
            installedApplicationProvider: new AndroidInstalledApplicationProvider(this));
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .LogToTrace();
    }
}
