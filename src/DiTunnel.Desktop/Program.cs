using Avalonia;

namespace DiTunnel.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--wfp-self-test", StringComparer.OrdinalIgnoreCase) || args.Contains("--cleanup-wfp", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Di-Tunnel.NetworkHost.exe"))
                { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
                start.ArgumentList.Add(args.Contains("--cleanup-wfp", StringComparer.OrdinalIgnoreCase) ? "--cleanup-wfp" : "--wfp-self-test");
                using var helper = System.Diagnostics.Process.Start(start)!;
                helper.WaitForExit();
                Environment.ExitCode = helper.ExitCode;
            }
            catch { Environment.ExitCode = 3; }
            return;
        }
        try { RunApplication(args); }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            try
            {
                Directory.CreateDirectory(App.UserSettings.DataDirectory);
                File.WriteAllText(Path.Combine(App.UserSettings.DataDirectory,
                    "Di-Tunnel-Startup-Error.txt"), error.ToString());
            }
            catch { }
        }
    }

    private static void RunApplication(string[] args)
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
        App.App.Platform = App.AppPlatform.Windows;
        App.App.UpdateInstaller = new WindowsUpdateInstaller();
        App.App.CreateMainViewModel = () =>
        {
            var engine = new Platform.Windows.WindowsNetworkClient(
                () => App.UserSettings.Current.GetSplitTunnelPolicy(),
                () => App.UserSettings.Current.GetConnectionPolicy(),
                () => App.UserSettings.Current.BlockAdsEnabled,
                () => App.UserSettings.Current.StrictAdBlockingEnabled);
            return new App.ViewModels.MainViewModel(engine,
                store: new Platform.Windows.WindowsProfileStore(),
                probe: engine, countryResolver: new Platform.Windows.WindowsServerCountryResolver(),
                installedApplicationProvider: new Platform.Windows.WindowsInstalledApplicationProvider());
        };
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
