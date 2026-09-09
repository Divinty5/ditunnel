using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Android;

internal static class AndroidVpnServiceBridge
{
    private static readonly object Sync = new();
    private static ImportedProfile? pendingProfile;
    private static SplitTunnelPolicy pendingSplitTunnelPolicy = SplitTunnelPolicy.Default;
    private static TaskCompletionSource<VpnStatus>? startCompletion;
    private static TaskCompletionSource<VpnStatus>? stopCompletion;

    public static void SetPendingProfile(ImportedProfile profile, SplitTunnelPolicy splitTunnelPolicy)
    {
        lock (Sync)
        {
            pendingProfile = profile;
            pendingSplitTunnelPolicy = splitTunnelPolicy;
        }
    }

    public static (ImportedProfile Profile, SplitTunnelPolicy SplitTunnelPolicy)? TakePendingRequest()
    {
        lock (Sync)
        {
            var profile = pendingProfile;
            pendingProfile = null;
            if (profile is null) return null;
            var request = (profile, pendingSplitTunnelPolicy);
            pendingSplitTunnelPolicy = SplitTunnelPolicy.Default;
            return request;
        }
    }

    public static Task<VpnStatus> ExpectStart()
    {
        lock (Sync)
            return (startCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    public static Task<VpnStatus> ExpectStop()
    {
        lock (Sync)
            return (stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    }

    public static void Started() => CompleteStart(new(
        VpnConnectionState.Connected,
        "VPN подключён через Android VpnService.",
        DateTimeOffset.UtcNow));

    public static void StartFailed(string message) => CompleteStart(new(VpnConnectionState.Error, message));

    public static void Stopped()
    {
        TaskCompletionSource<VpnStatus>? completion;
        lock (Sync)
        {
            completion = stopCompletion;
            stopCompletion = null;
        }
        completion?.TrySetResult(VpnStatus.Disconnected);
    }

    private static void CompleteStart(VpnStatus result)
    {
        TaskCompletionSource<VpnStatus>? completion;
        lock (Sync)
        {
            completion = startCompletion;
            startCompletion = null;
        }
        completion?.TrySetResult(result);
    }
}
