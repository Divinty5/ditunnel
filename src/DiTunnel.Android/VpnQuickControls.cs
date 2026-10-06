using Android.App;
using Android.Content;
using Android.OS;
using DiTunnel.App;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Android;
using System.Security.Cryptography;
using System.Text;

namespace DiTunnel.Android;

internal static class VpnQuickControls
{
    public const string ToggleAction = "com.divintyinteractive.ditunnel.control.TOGGLE";
    public const string SelectAction = "com.divintyinteractive.ditunnel.control.SELECT";
    public const string RecoverAction = "com.divintyinteractive.ditunnel.control.RECOVER";
    public static Task RecoverAsync() => engine.RecoverConnectionAsync();
    public const string ProfileExtra = "profile_key";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Context context = null!;
    private static AndroidVpnEngine engine = null!;
    private static AndroidProfileStore store = null!;
    private static string? selectedKey;
    private static IReadOnlyList<ImportedProfile>? profiles;
    private static readonly object LatencyGate = new();
    private static CancellationTokenSource? latencyCancellation;
    private static string? latencyProfileKey;
    private sealed record LatencyReading(string ProfileKey, double Milliseconds);
    private static LatencyReading? latencyReading;
    public static double? LatencyMilliseconds => Status.State == VpnConnectionState.Connected &&
        engine.ActiveProfile is { } profile && Volatile.Read(ref latencyReading) is { } reading && reading.ProfileKey == Key(profile)
            ? reading.Milliseconds : null;

    public static void EnsureLatencyMonitoring()
    {
        lock (LatencyGate)
        {
            var profile = Status.State == VpnConnectionState.Connected ? engine.ActiveProfile : null;
            var key = profile is null ? null : Key(profile);
            if (key is not null && key == latencyProfileKey && latencyCancellation is not null) return;
            latencyCancellation?.Cancel();
            latencyCancellation?.Dispose();
            latencyCancellation = null;
            latencyProfileKey = key;
            Volatile.Write(ref latencyReading, null);
            if (key is null || !VpnWidgets.HasLargeWidgets(context)) return;
            latencyCancellation = new CancellationTokenSource();
            _ = MeasureLatencyAsync(key, latencyCancellation.Token);
        }
    }
    private static async Task MeasureLatencyAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1000, cancellationToken);
            var probe = new AndroidServerProbe(context);
            while (!cancellationToken.IsCancellationRequested && VpnWidgets.HasLargeWidgets(context))
            {
                ServerProbeResult result;
                try { result = await probe.ProbeActiveTunnelAsync(cancellationToken); }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    global::Android.Util.Log.Warn("DiTunnelWidgetLatency", error.GetType().Name);
                    result = new(null, "Сервер недоступен");
                }
                cancellationToken.ThrowIfCancellationRequested();
                lock (LatencyGate)
                {
                    if (latencyCancellation?.Token != cancellationToken || Status.State != VpnConnectionState.Connected ||
                        engine.ActiveProfile is not { } profile || Key(profile) != key) return;
                    Volatile.Write(ref latencyReading, result.Milliseconds is { } delay ? new LatencyReading(key, delay) : null);
                }
                Refresh();
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
        catch (System.OperationCanceledException) { }
        catch (Exception error) { global::Android.Util.Log.Warn("DiTunnelWidgetLatency", error.GetType().Name); }
        finally
        {
            lock (LatencyGate)
                if (latencyCancellation?.Token == cancellationToken)
                {
                    latencyCancellation.Dispose();
                    latencyCancellation = null;
                    Volatile.Write(ref latencyReading, null);
                }
        }
    }

    public static event Action<ImportedProfile>? SelectionChanged;
    public static event Action? Refreshed;
    public static VpnStatus Status => engine.Status;
    public static bool Busy => Status.State is VpnConnectionState.Connecting or VpnConnectionState.Disconnecting or VpnConnectionState.Reconnecting;
    public static bool Active => Status.State is VpnConnectionState.Connected or VpnConnectionState.Reconnecting;
    public static bool CanToggle => Status.State is not (VpnConnectionState.Connecting or VpnConnectionState.Disconnecting);
    public static string StatusText => L.T(Status.State switch
    {
        VpnConnectionState.Connecting => "Подключаем VPN…",
        VpnConnectionState.Disconnecting => "Отключаем VPN…",
        VpnConnectionState.Reconnecting => "Восстанавливаем VPN…",
        VpnConnectionState.Connected => "VPN подключён",
        VpnConnectionState.Error => "Не удалось подключиться",
        _ => "VPN отключён"
    });
    public static string Key(ImportedProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.SourceId + "\n" + profile.Content)));

    public static void Initialize(Context owner, AndroidVpnEngine vpnEngine, AndroidProfileStore profileStore)
    {
        context = owner; engine = vpnEngine; store = profileStore;
        selectedKey = owner.GetSharedPreferences("ditunnel_controls", FileCreationMode.Private)?.GetString("selected", null);
        engine.StatusChanged += (_, _) =>
        {
            try { EnsureLatencyMonitoring(); }
            catch (Exception error) { global::Android.Util.Log.Warn("DiTunnelWidgetLatency", error.GetType().Name); }
            Refresh();
        };
        UserSettings.ThemeChanged += Refresh;
        AndroidProfileStore.ProfilesChanged += () => { profiles = null; Refresh(); };
        L.Changed += () =>
        {
            Refresh();
            try { engine.RefreshNotification(); }
            catch (Exception error) { global::Android.Util.Log.Warn("DiTunnelControls", error.GetType().Name); }
        };
    }
    public static IReadOnlyList<ImportedProfile> Profiles()
    {
        try { return profiles ??= store.Load(); }
        catch { return []; } // Never overwrite unreadable encrypted profiles.
    }
    public static ImportedProfile? Selected => Profiles().FirstOrDefault(p => Key(p) == selectedKey)
        ?? Profiles().FirstOrDefault(p => p.SourceId == UserSettings.Current.SelectedSourceId) ?? Profiles().FirstOrDefault();
    public static IReadOnlyList<ImportedProfile> Servers => Profiles().Where(p => p.SourceId == Selected?.SourceId)
        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static void SelectFromApp(ImportedProfile? profile)
    {
        if (profile is null) return;
        selectedKey = Key(profile);
        using var preferences = context.GetSharedPreferences("ditunnel_controls", FileCreationMode.Private);
        using var editor = preferences?.Edit();
        editor?.PutString("selected", selectedKey)?.Apply();
        Refresh();
    }
    public static bool NeedsActivity => !Active && (Selected is null || global::Android.Net.VpnService.Prepare(context) is not null);
    public static Intent ActivityIntent(string action = ToggleAction) => new Intent(context, typeof(MainActivity))
        .SetAction(action).AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
    public static PendingIntent ActivityPendingIntent(string action = ToggleAction) => PendingIntent.GetActivity(context, 1701,
        ActivityIntent(action), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    public static async Task ExecuteAsync(string? action, string? profileKey = null)
    {
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            if (action == SelectAction || action == ToggleAction && !Active)
                await UserSettings.Current.InitializeSplitTunnelDefaultsAsync(() =>
                    new AndroidInstalledApplicationProvider(context).GetInstalledApplicationsAsync());
            if (action == SelectAction)
            {
                // PendingIntent carries an identifier, never credentials. Validate against the encrypted store.
                var profile = Servers.FirstOrDefault(p => Key(p) == profileKey);
                if (profile is null || Busy) return;
                SelectFromApp(profile);
                UserSettings.Current.SelectedSourceId = profile.SourceId;
                UserSettings.Current.Save();
                SelectionChanged?.Invoke(profile);
                if (Active && engine.ActiveProfile?.Content != profile.Content) await engine.SwitchAsync(profile);
            }
            else if (action == ToggleAction)
            {
                if (!CanToggle) return;
                if (Active) await engine.DisconnectAsync();
                else if (Selected is { } profile) await engine.ConnectAsync(profile);
                else ShowMessage("Сначала выберите сервер.");
            }
        }
        catch (Exception error)
        {
            ShowMessage(error is InvalidOperationException or NotSupportedException or FormatException or TimeoutException
                ? error.Message : "Не удалось изменить VPN-подключение.");
        }
        finally { Gate.Release(); Refresh(); }
    }
    private static void ShowMessage(string text) => new Handler(Looper.MainLooper!).Post(() =>
        global::Android.Widget.Toast.MakeText(context, L.T(text), global::Android.Widget.ToastLength.Long)?.Show());
    public static void Refresh()
    {
        try { Refreshed?.Invoke(); }
        catch (Exception error) { global::Android.Util.Log.Warn("DiTunnelControls", error.GetType().Name); }
        // Launcher/system UI failures must never break profile saving or VPN state transitions.
        try { VpnWidgets.Refresh(context); }
        catch (Exception error) { global::Android.Util.Log.Warn("DiTunnelControls", error.GetType().Name); }
        try { global::Android.Service.QuickSettings.TileService.RequestListeningState(context, new ComponentName(context, Java.Lang.Class.FromType(typeof(VpnTileService)))); }
        catch (Exception error) { global::Android.Util.Log.Warn("DiTunnelControls", error.GetType().Name); }
    }
}

[BroadcastReceiver(Name = "com.divintyinteractive.ditunnel.VpnControlReceiver", Exported = false)]
public sealed class VpnControlReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action is not (VpnQuickControls.ToggleAction or VpnQuickControls.SelectAction)) return;
        if (context is not null) VpnControlService.Dispatch(context, intent.Action, intent.GetStringExtra(VpnQuickControls.ProfileExtra));
    }
}
