using Avalonia;
using System.Text.Json;

namespace DiTunnel.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--wfp-self-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = RunWfpSelfTest();
            return;
        }
        if (args.Contains("--cleanup-wfp", StringComparer.OrdinalIgnoreCase))
        {
            try { Platform.Windows.WindowsKillSwitchController.CleanupStaleFilters(); }
            catch { Environment.ExitCode = 3; }
            return;
        }
        try { RunApplication(args); }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            try
            {
                File.WriteAllText(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
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
        App.App.CreateMainViewModel = () =>
        {
            var killSwitch = new Platform.Windows.WindowsKillSwitchController();
            return new App.ViewModels.MainViewModel(new Platform.Windows.WindowsVpnEngine(
                () => App.UserSettings.Current.GetSplitTunnelPolicy(),
                () => App.UserSettings.Current.GetConnectionPolicy(), killSwitch),
                probe: new Platform.Windows.WindowsServerProbe(killSwitch), countryResolver: new Platform.Windows.WindowsServerCountryResolver());
        };
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        timer.Stop();
        instance.ReleaseMutex();
    }

    private static int RunWfpSelfTest()
    {
        var reportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "Di-Tunnel-Wfp-Self-Test.json");
        using var output = new StringWriter();
        int exitCode;
        try
        {
            exitCode = Platform.Windows.WindowsKillSwitchSelfTest.RunAsync(output).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            exitCode = 1;
            output.WriteLine(JsonSerializer.Serialize(new
            {
                passed = false,
                error = error.ToString()
            }));
        }

        File.WriteAllText(reportPath, output.ToString());
        return exitCode;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
