using Android.Content;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using System.Text.Json;

namespace DiTunnel.Platform.Android;

internal static class AndroidVpnServiceBridge
{
    private const string ActionStarted = "com.divintyinteractive.ditunnel.status.STARTED";
    private const string ActionStartFailed = "com.divintyinteractive.ditunnel.status.START_FAILED";
    private const string ActionStopped = "com.divintyinteractive.ditunnel.status.STOPPED";
    private const string ExtraRequest = "request";
    private const string ExtraMessage = "message";
    private static readonly object Sync = new();
    private static TaskCompletionSource<VpnStatus>? startCompletion;
    private static TaskCompletionSource<VpnStatus>? stopCompletion;
    private static StatusReceiver? receiver;
    public static event EventHandler<VpnStatus>? StatusReceived;

    public static bool Register(Context context)
    {
        lock (Sync)
        {
            if (receiver is not null) return true;
            receiver = new StatusReceiver();
            var filter = new IntentFilter();
            filter.AddAction(ActionStarted);
            filter.AddAction(ActionStartFailed);
            filter.AddAction(ActionStopped);
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
            else
#pragma warning disable CA1422
                context.RegisterReceiver(receiver, filter);
#pragma warning restore CA1422
            return true;
        }
    }

    public static Intent CreateStartIntent(Context context, ImportedProfile profile, SplitTunnelPolicy policy)
    {
        var request = new ServiceRequest(profile, policy.Mode, policy.Domains.ToArray(), policy.Processes.ToArray());
        return new Intent(context, typeof(DiTunnelVpnService)).SetAction(DiTunnelVpnService.ActionStart)
            .PutExtra(ExtraRequest, JsonSerializer.Serialize(request, AndroidJsonContext.Default.ServiceRequest));
    }

    public static (ImportedProfile Profile, SplitTunnelPolicy SplitTunnelPolicy)? ReadRequest(Intent intent)
    {
        var json = intent.GetStringExtra(ExtraRequest);
        if (string.IsNullOrWhiteSpace(json)) return null;
        var request = JsonSerializer.Deserialize(json, AndroidJsonContext.Default.ServiceRequest);
        return request is null ? null : (request.Profile, new(request.Mode, request.Domains, request.Processes));
    }

    public static Task<VpnStatus> ExpectStart() { lock (Sync) return (startCompletion = NewCompletion()).Task; }
    public static Task<VpnStatus> ExpectStop() { lock (Sync) return (stopCompletion = NewCompletion()).Task; }
    public static void PublishStarted(Context context) => Publish(context, ActionStarted, "VPN подключён через Android VpnService.");
    public static void PublishStartFailed(Context context, string message) => Publish(context, ActionStartFailed, message);
    public static void PublishStopped(Context context) => Publish(context, ActionStopped, "VPN отключён.");

    private static TaskCompletionSource<VpnStatus> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Publish(Context context, string action, string message) =>
        context.SendBroadcast(new Intent(action).SetPackage(context.PackageName).PutExtra(ExtraMessage, message));

    private sealed class StatusReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action is not { } action) return;
            var message = intent.GetStringExtra(ExtraMessage);
            TaskCompletionSource<VpnStatus>? completion;
            VpnStatus status;
            lock (Sync)
            {
                if (action == ActionStopped)
                {
                    completion = stopCompletion; stopCompletion = null;
                    status = new(VpnConnectionState.Disconnected, message);
                }
                else
                {
                    completion = startCompletion; startCompletion = null;
                    status = action == ActionStarted
                        ? new(VpnConnectionState.Connected, message, DateTimeOffset.UtcNow)
                        : new(VpnConnectionState.Error, message);
                }
            }
            completion?.TrySetResult(status);
            StatusReceived?.Invoke(null, status);
        }
    }

    internal sealed record ServiceRequest(ImportedProfile Profile, SplitTunnelMode Mode, string[] Domains, string[] Processes);
}
