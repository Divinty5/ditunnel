using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DiTunnel.App.ViewModels;
using DiTunnel.App.Views;

namespace DiTunnel.App;

public partial class App : Application
{
    public static Func<MainViewModel> CreateMainViewModel { get; set; } = () => new MainViewModel();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        UserSettings.Current.ApplyTheme();
        L.Apply();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new MainWindow
            {
                DataContext = CreateMainViewModel()
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView
            {
                DataContext = CreateMainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
