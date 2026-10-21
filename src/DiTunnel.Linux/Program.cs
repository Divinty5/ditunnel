using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Avalonia;
using DiTunnel.App;
using SharedApp = DiTunnel.App.App;
using DiTunnel.App.ViewModels;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Desktop;
using DiTunnel.Platform.Linux.Storage;
using DiTunnel.Platform.Linux.Network;
using Tmds.DBus.Protocol;

namespace DiTunnel.Linux;

internal static partial class Program
{
    [LibraryImport("libc")] private static partial uint geteuid();
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux()) { Console.Error.WriteLine("Эта точка входа предназначена для Linux."); return 1; }
        try
        {
            if (args.SequenceEqual(new[] { "--version" }))
            {
                Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3));
                return 0;
            }
            if (geteuid() == 0) { Console.Error.WriteLine("Запускайте интерфейс Di-Tunnel обычным пользователем."); return 1; }
            var paths = LinuxApplicationPaths.Discover();
            if (args.Length == 1 && args[0] is "--enable-autostart" or "--disable-autostart")
            {
                LinuxAutostart.SetEnabled(Path.GetDirectoryName(paths.ConfigDirectory)!, args[0] == "--enable-autostart");
                return 0;
            }
            if (args.Any(value => value is not ("--autostart" or "--network-session-development"))) throw new ArgumentException();
            SharedApp.Platform = AppPlatform.Linux;
            AppPaths.Configure(paths.ConfigDirectory, paths.DataDirectory, paths.StateDirectory);
            // Serialize profile access even when launches use different state
            // directories but share the same XDG_DATA_HOME.
            using var instance = LinuxInstanceLock.TryAcquire(paths.DataDirectory);
            if (instance is null) { Console.Error.WriteLine("Di-Tunnel уже запущен. Откройте существующее окно."); return 0; }
            if (NormalizeProcessRules(UserSettings.Current))
                try { UserSettings.Current.Save(); } catch { /* Keep the corrected rules in memory if settings are read-only. */ }
            IProfileStore store;
            // No Avalonia dispatcher is running yet; keyring I/O is asynchronous
            // and bounded, before the synchronous IProfileStore is given to the UI.
            try
            {
                var key = new LinuxSecretService().GetProfileKeyAsync(paths.ProfileFile).GetAwaiter().GetResult();
                try { store = new LinuxProfileStore(paths.ProfileFile, key); }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            catch (ProfileStoreUnavailableException) { store = new UnavailableProfileStore(LinuxSecretService.UnavailableMessage); }
            using var ownedStore = store as IDisposable;
            using var network = new LinuxNetworkClient(args.Contains("--network-session-development") ? DBusAddress.Session : null,
                policy: () => { var settings = UserSettings.Current; var split = settings.GetSplitTunnelPolicy();
                    return new(split.Mode, split.Domains.ToArray(), split.Processes.Select(LinuxInstalledApplications.ResolveProcessPath).Distinct(StringComparer.Ordinal).ToArray(), settings.KillSwitchEnabled, settings.AllowLocalNetwork, settings.BlockAdsEnabled, settings.StrictAdBlockingEnabled, settings.Ipv4Only); });
            var canProbe = false;
            var canConnect = false;
            var platform = AppPlatform.Linux with { SupportsKillSwitch = false, SupportsAdvancedNetworkSettings = false };
            try
            {
                var capabilities = network.GetCapabilitiesAsync().GetAwaiter().GetResult();
                canProbe = capabilities.ProtocolVersion == LinuxNetworkProtocol.Version && capabilities.CanProbe;
                canConnect = capabilities.ProtocolVersion == LinuxNetworkProtocol.Version && capabilities.CanConnect;
                platform = AppPlatform.Linux with { SupportsKillSwitch = canConnect && capabilities.SupportsKillSwitch, SupportsAdvancedNetworkSettings = canConnect && capabilities.SupportsSplitTunnel };
                if (canConnect) network.RefreshStatusAsync().GetAwaiter().GetResult();
            }
            catch { /* The GUI remains usable without an installed system service. */ }
            SharedApp.UpdateInstaller = new LinuxUpdateInstaller();
            SharedApp.Platform = platform;
            SharedApp.CreateMainViewModel = () =>
            {
                var model = new MainViewModel(canConnect ? network : null, store: store, probe: canProbe ? network : null,
                    countryResolver: new LinuxServerCountryResolver("/usr/lib/ditunnel/runtime/geoip.dat"),
                    platform: platform, installedApplicationProvider: new LinuxInstalledApplications());
                if (model.CanImport) model.Notice = network.Status.Message ?? (canConnect
                    ? platform.SupportsAdvancedNetworkSettings ? "Linux VPN доступен. Раздельное туннелирование и kill switch настраиваются в настройках соединения."
                    : "Linux VPN доступен. Для раздельного туннелирования обновите Linux runtime."
                    : canProbe ? "Проверка серверов Linux доступна. Для VPN нужна привилегированная служба и DNS через systemd-resolved."
                    : "Linux-интерфейс готов. Установите сетевую службу для проверки серверов и VPN.");
                return model;
            };
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch
        {
            // Exception details may contain filesystem paths or imported secrets.
            Console.Error.WriteLine("Не удалось запустить Di-Tunnel для Linux. Проверьте графическую сессию и права на каталоги XDG.");
            return 1;
        }
    }

#pragma warning disable AVALONIA_X11_FORCE_CSD // Avalonia is pinned; forced decorations keep all Linux captions in the application theme.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SharedApp>()
        .UsePlatformDetect().With(new X11PlatformOptions { ForceDrawnDecorations = true })
        .WithInterFont().LogToTrace();
#pragma warning restore AVALONIA_X11_FORCE_CSD

    internal static bool NormalizeProcessRules(UserSettings settings)
    {
        var changed = false;
        foreach (var mode in new[] { DiTunnel.Core.Connection.SplitTunnelMode.BypassSelected, DiTunnel.Core.Connection.SplitTunnelMode.ProxySelected })
        {
            var rules = settings.GetSplitTunnelRules(mode);
            var paths = rules.Processes.Select(LinuxInstalledApplications.ResolveProcessPath).Distinct(StringComparer.Ordinal).ToArray();
            if (rules.Processes.SequenceEqual(paths, StringComparer.Ordinal)) continue;
            settings.SetSplitTunnelRules(mode, rules.Domains, paths);
            changed = true;
        }
        return changed;
    }
}
