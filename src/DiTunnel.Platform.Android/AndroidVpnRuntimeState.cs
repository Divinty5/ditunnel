using Android.App;
using Android.Content;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Core;
using System.Text.Json;

namespace DiTunnel.Platform.Android;

internal static class AndroidVpnRuntimeState
{
    private const string PreferencesName = "ditunnel_vpn_runtime";
    private const string ActiveKey = "active";
    private const string ProcessIdKey = "pid";
    private const string ProfileKey = "profile";

    public static void SetConnected(Context context, ImportedProfile profile)
    {
        using var editor = Preferences(context).Edit();
        editor?.PutBoolean(ActiveKey, true);
        editor?.PutInt(ProcessIdKey, global::Android.OS.Process.MyPid());
        editor?.PutString(ProfileKey, JsonSerializer.Serialize(profile, DiTunnelJsonContext.Default.ImportedProfile));
        editor?.Commit();
    }

    public static void Clear(Context context)
    {
        using var editor = Preferences(context).Edit();
        editor?.PutBoolean(ActiveKey, false);
        editor?.Commit();
    }

    public static VpnStatus ReadStatus(Context context) => IsVpnProcessRunning(context)
        ? new(VpnConnectionState.Connected, "VPN подключён через Android VpnService.", DateTimeOffset.UtcNow)
        : VpnStatus.Disconnected;

    public static ImportedProfile? ReadActiveProfile(Context context)
    {
        if (!IsVpnProcessRunning(context)) return null;
        try { return JsonSerializer.Deserialize(Preferences(context).GetString(ProfileKey, null) ?? "", DiTunnelJsonContext.Default.ImportedProfile); }
        catch { return null; }
    }

    public static bool IsVpnProcessRunning(Context context)
        => Preferences(context).GetBoolean(ActiveKey, false);

    public static bool IsVpnProcessAlive(Context context)
    {
        var pid = GetVpnProcessId(context);
        var manager = context.GetSystemService(Context.ActivityService) as ActivityManager;
        var processName = context.PackageName + ":vpn";
        return pid > 0 && manager?.RunningAppProcesses?.Any(process =>
            process.Pid == pid && string.Equals(process.ProcessName, processName, StringComparison.Ordinal)) == true;
    }

    public static void TerminateVpnProcess(Context context)
    {
        var pid = GetVpnProcessId(context);
        if (pid > 0 && pid != global::Android.OS.Process.MyPid())
            global::Android.OS.Process.KillProcess(pid);
    }

    private static int GetVpnProcessId(Context context) => Preferences(context).GetInt(ProcessIdKey, 0);

    private static global::Android.Content.ISharedPreferences Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)
        ?? throw new InvalidOperationException("Хранилище состояния Android VPN недоступно.");
}
