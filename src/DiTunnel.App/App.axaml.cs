using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DiTunnel.App.ViewModels;
using DiTunnel.App.Views;

namespace DiTunnel.App;

public partial class App : Application
{
    public static AppPlatform Platform { get; set; } = AppPlatform.Detect();
    public static Func<MainViewModel> CreateMainViewModel { get; set; } = () => new MainViewModel();
    public static IUpdateInstaller? UpdateInstaller { get; set; }
    public static Func<Task<string?>>? ReadClipboardTextAsync { get; set; }
    public static Action<bool>? SetWindowsAutostart { get; set; }
    public static bool IsAutostartLaunch { get; set; }
    internal static bool ShouldStartMinimized => OperatingSystem.IsWindows() && IsAutostartLaunch
        && UserSettings.Current.StartWithWindows && UserSettings.Current.StartMinimized;

    public override void Initialize()
    {
        if (OperatingSystem.IsWindows()) WindowsTaskbarIcon.InitializeProcess();
        AvaloniaXamlLoader.Load(this);
        UserSettings.Current.ApplyTheme();
        L.Apply();
        AppLocale.Apply();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new MainWindow
            {
                DataContext = CreateMainViewModel(),
                ShowActivated = !ShouldStartMinimized,
                WindowState = ShouldStartMinimized ? Avalonia.Controls.WindowState.Minimized : Avalonia.Controls.WindowState.Normal
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = CreateMainView;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = CreateMainView();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainView CreateMainView()
    {
        return new MainView
        {
            DataContext = CreateMainViewModel()
        };
    }
}
