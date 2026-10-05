using Android.Content;

namespace DiTunnel.Platform.Android;

// Only fixed stage identifiers are persisted. Never save profiles, endpoints or keys.
internal enum VpnStartupStage { Service, Profile, Endpoint, AwgTransport, AwgRuntime, AwgHandshake, Tun, Xray, Connected }
internal static class AndroidVpnStartupDiagnostics
{
    private static string PathFor(Context context) => Path.Combine(context.NoBackupFilesDir!.AbsolutePath, "vpn-startup-stage.txt");
    public static void Record(Context context, VpnStartupStage stage)
    {
        try { File.WriteAllText(PathFor(context), stage.ToString()); } catch { }
    }
    public static string Read(Context context)
    {
        try { if (Enum.TryParse<VpnStartupStage>(File.ReadAllText(PathFor(context)), out var stage)) return stage.ToString(); } catch { }
        return VpnStartupStage.Service.ToString();
    }
}
