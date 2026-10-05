using Android.App;
using Android.Content;
using Android.Net;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiTunnel.Platform.Android;

internal static class AndroidVpnRuntimeState
{
    // SharedPreferences caches are process-local. Fresh atomic reads prevent a tile
    // from restoring stale state. Credentials remain in encrypted profiles.dat.
    private static string StatePath(Context context) => Path.Combine(context.NoBackupFilesDir?.AbsolutePath
        ?? throw new InvalidOperationException("Хранилище состояния Android VPN недоступно."), "vpn-state.json");
    private static string Key(ImportedProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.SourceId + "\n" + profile.Content)));
    public static void SetStarting(Context context, bool serviceRecovery = false)
    {
        using var preferences = context.GetSharedPreferences("ditunnel_vpn_runtime", FileCreationMode.Private);
        using var editor = preferences?.Edit();
        editor?.Clear()?.Commit(); // Remove the legacy plaintext profile and cached flags.
        Write(context, false, global::Android.OS.Process.MyPid(), serviceRecovery ? Read(context).ProfileKey : null, serviceRecovery, serviceRecovery);
    }
    public static void SetConnected(Context context, ImportedProfile profile, bool serviceRecovery = false) =>
        Write(context, true, global::Android.OS.Process.MyPid(), Key(profile), serviceRecovery);
    public static void SetRecovering(Context context)
    {
        var state = Read(context);
        Write(context, false, global::Android.OS.Process.MyPid(), state.ProfileKey, true, true);
    }
    public static bool ServiceOwnsRecovery(Context context) => Read(context).ServiceRecovery;
    public static void Clear(Context context)
    {
        var state = Read(context);
        Write(context, false, state.Pid, state.ProfileKey);
    }
    public static VpnStatus ReadStatus(Context context) => IsVpnProcessRunning(context)
        ? new(VpnConnectionState.Connected, "VPN подключён через Android VpnService.", DateTimeOffset.UtcNow)
        : Read(context).Recovering ? new(VpnConnectionState.Reconnecting, "Восстанавливаем VPN…") : VpnStatus.Disconnected;
    public static ImportedProfile? ReadActiveProfile(Context context)
    {
        if (!IsVpnProcessRunning(context) && !Read(context).Recovering) return null;
        try
        {
            var key = Read(context).ProfileKey;
            return new AndroidProfileStore(context).Load().FirstOrDefault(profile => Key(profile) == key);
        }
        catch { return null; }
    }
    public static bool IsVpnProcessRunning(Context context) => Read(context).Active && IsVpnProcessAlive(context) && IsSystemVpnActive(context);
    public static bool IsVpnProcessAlive(Context context) => IsOurVpnProcess(context, Read(context).Pid);
    public static void TerminateVpnProcess(Context context)
    {
        var pid = Read(context).Pid;
        // Linux may reuse a PID after a crash. Validate its name before terminating.
        if (pid != global::Android.OS.Process.MyPid() && IsOurVpnProcess(context, pid))
            global::Android.OS.Process.KillProcess(pid);
    }
    private static bool IsOurVpnProcess(Context context, int pid)
    {
        if (pid <= 0) return false;
        var name = context.PackageName + ":vpn";
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Split('\0')[0] == name; }
        catch
        {
            var manager = context.GetSystemService(Context.ActivityService) as ActivityManager;
            return manager?.RunningAppProcesses?.Any(process => process.Pid == pid && process.ProcessName == name) == true;
        }
    }
    private static bool IsSystemVpnActive(Context context)
    {
        if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager) return false;
        try
        {
#pragma warning disable CA1422
            return manager.GetAllNetworks().Any(network => manager.GetNetworkCapabilities(network)?.HasTransport(TransportType.Vpn) == true);
#pragma warning restore CA1422
        }
        catch { return false; }
    }
    private static (bool Active, int Pid, string? ProfileKey, bool ServiceRecovery, bool Recovering) Read(Context context)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(StatePath(context)));
            var root = json.RootElement;
            return (root.GetProperty("active").GetBoolean(), root.GetProperty("pid").GetInt32(), root.GetProperty("profileKey").GetString(),
                root.TryGetProperty("serviceRecovery", out var owner) && owner.GetBoolean(),
                root.TryGetProperty("recovering", out var recovering) && recovering.GetBoolean());
        }
        catch { return (false, 0, null, false, false); }
    }
    private static void Write(Context context, bool active, int pid, string? key, bool serviceRecovery = false, bool recovering = false)
    {
        var path = StatePath(context);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, new JsonObject { ["active"] = active, ["pid"] = pid, ["profileKey"] = key,
                ["serviceRecovery"] = serviceRecovery, ["recovering"] = recovering }.ToJsonString());
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
