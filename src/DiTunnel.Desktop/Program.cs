using Avalonia;

namespace DiTunnel.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new Mutex(true, "DiTunnel.Desktop", out var firstInstance);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, "DiTunnel.ShowWindow");
        if (!firstInstance) { show.Set(); return; }
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (show.WaitOne(0) && Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is { } window)
            {
                window.Show();
                if (window.WindowState == Avalonia.Controls.WindowState.Minimized) window.WindowState = Avalonia.Controls.WindowState.Normal;
                window.Activate();
            }
        };
        timer.Start();
        App.App.CreateMainViewModel = () => new App.ViewModels.MainViewModel(new Platform.Windows.WindowsVpnEngine(() => App.UserSettings.Current.GetSplitTunnelPolicy()), probe: new Platform.Windows.WindowsServerProbe(), countryResolver: new Platform.Windows.WindowsServerCountryResolver());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        timer.Stop();
        instance.ReleaseMutex();
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
