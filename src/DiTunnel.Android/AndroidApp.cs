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
    private MainViewModel? mainViewModel;
    public AndroidApp(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        // Application constructors run before Android attaches the Context. Profile paths,
        // Keystore and system services become available in OnCreate.
        AndroidLocalization.Translate = text => App.L.Translate(text,
            App.UserSettings.Load(Path.Combine(App.UserSettings.DataDirectory, "settings.json")).Language);
        // VpnService and probes have isolated processes. Only the UI process owns the
        // engine facade, widget controller and selected profile. VpnService owns
        // background restoration, including when this process is not running.
        if (global::Android.App.Application.ProcessName != PackageName) return;
        AndroidLocaleCoordinator.Initialize(this);
        App.App.Platform = App.AppPlatform.Android;
        App.QrScanner.ScanAsync = QrScannerCoordinator.Instance.ScanAsync;
        App.App.UpdateInstaller = new AndroidUpdateInstaller(this);
        var engine = new AndroidVpnEngine(
            this,
            VpnPermissionCoordinator.Instance,
            () => App.UserSettings.Current.GetSplitTunnelPolicy(),
            () => App.UserSettings.Current.BlockAdsEnabled,
            () => App.UserSettings.Current.StrictAdBlockingEnabled);
        var store = new AndroidProfileStore(this);
        VpnQuickControls.Initialize(this, engine, store);
        App.App.CreateMainViewModel = () =>
        {
            if (mainViewModel is not null) return mainViewModel;
            var model = new MainViewModel(engine, store: store,
            probe: new AndroidServerProbe(this),
            countryResolver: new AndroidServerCountryResolver(this),
            installedApplicationProvider: new AndroidInstalledApplicationProvider(this));
            var restoring = VpnQuickControls.Selected;
            if (restoring is not null)
            {
                model.SelectedGroup = model.Groups.FirstOrDefault(group => group.Id == restoring.SourceId) ?? model.SelectedGroup;
                model.SelectedProfile = model.Profiles.FirstOrDefault(row => VpnQuickControls.Key(row.Profile) == VpnQuickControls.Key(restoring)) ?? model.SelectedProfile;
            }
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(model.SelectedProfile)) VpnQuickControls.SelectFromApp(model.SelectedProfile?.Profile);
            };
            VpnQuickControls.SelectionChanged += profile => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                model.SelectedGroup = model.Groups.FirstOrDefault(group => group.Id == profile.SourceId) ?? model.SelectedGroup;
                model.SelectedProfile = model.Profiles.FirstOrDefault(row => VpnQuickControls.Key(row.Profile) == VpnQuickControls.Key(profile)) ?? model.SelectedProfile;
            });
            VpnQuickControls.SelectFromApp(model.SelectedProfile?.Profile);
            return mainViewModel = model;
        };
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .LogToTrace();
    }
    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (global::Android.App.Application.ProcessName == PackageName) VpnQuickControls.Refresh();
    }
}
