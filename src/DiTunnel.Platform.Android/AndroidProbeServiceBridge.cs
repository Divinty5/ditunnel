using Android.App;
using Android.Content;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Core;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DiTunnel.Platform.Android;

internal static class AndroidProbeServiceBridge
{
    internal const string ActionProbe = "com.divintyinteractive.ditunnel.action.PROBE";
    internal const string ActionCancel = "com.divintyinteractive.ditunnel.action.CANCEL_PROBE";
    internal const string ActionResult = "com.divintyinteractive.ditunnel.status.PROBE_RESULT";
    internal const string ExtraRequestId = "request_id";
    internal const string ExtraProfiles = "profiles";
    internal const string ExtraMode = "mode";
    internal const string ExtraLegacy = "legacy";
    internal const string ExtraResult = "result";
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<ServerProbeResult>>> Pending = new();
    private static readonly object Sync = new();
    private static ResultReceiver? receiver;

    public static async Task<IReadOnlyList<ServerProbeResult>> ProbeAsync(Context context, IReadOnlyList<ImportedProfile> profiles, ServerProbeMode mode, CancellationToken cancellationToken, bool legacy = false)
    {
        Register(context);
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<IReadOnlyList<ServerProbeResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[requestId] = completion;
        context.StartService(new Intent(context, typeof(DiTunnelProbeService)).SetAction(ActionProbe)
            .PutExtra(ExtraRequestId, requestId)
            .PutExtra(ExtraProfiles, JsonSerializer.Serialize(profiles.ToArray(), DiTunnelJsonContext.Default.ImportedProfileArray))
            .PutExtra(ExtraMode, (int)mode)
            .PutExtra(ExtraLegacy, legacy));
        using var cancellation = cancellationToken.Register(() =>
        {
            context.StartService(new Intent(context, typeof(DiTunnelProbeService)).SetAction(ActionCancel).PutExtra(ExtraRequestId, requestId));
            completion.TrySetCanceled(cancellationToken);
        });
        try { return await completion.Task; }
        finally
        {
            Pending.TryRemove(requestId, out _);
            await WaitForProbeProcessExitAsync(context);
        }
    }

    private static void Register(Context context)
    {
        lock (Sync)
        {
            if (receiver is not null) return;
            receiver = new ResultReceiver();
            var filter = new IntentFilter(ActionResult);
            if (OperatingSystem.IsAndroidVersionAtLeast(33)) context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
            else
#pragma warning disable CA1422
                context.RegisterReceiver(receiver, filter);
#pragma warning restore CA1422
        }
    }

    private static async Task WaitForProbeProcessExitAsync(Context context)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (FindProbeProcess(context) is null) return;
            await Task.Delay(100);
        }
        var remaining = FindProbeProcess(context);
        if (remaining is not null && remaining.Pid != global::Android.OS.Process.MyPid())
            global::Android.OS.Process.KillProcess(remaining.Pid);
    }

    private static ActivityManager.RunningAppProcessInfo? FindProbeProcess(Context context)
    {
        var manager = context.GetSystemService(Context.ActivityService) as ActivityManager;
        var name = context.PackageName + ":probe";
        return manager?.RunningAppProcesses?.FirstOrDefault(process => string.Equals(process.ProcessName, name, StringComparison.Ordinal));
    }

    private sealed class ResultReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            var requestId = intent?.GetStringExtra(ExtraRequestId);
            var json = intent?.GetStringExtra(ExtraResult);
            if (requestId is null || json is null || !Pending.TryGetValue(requestId, out var completion)) return;
            try { completion.TrySetResult(JsonSerializer.Deserialize(json, DiTunnelJsonContext.Default.ServerProbeResultArray) ?? []); }
            catch { completion.TrySetResult([]); }
        }
    }
}
