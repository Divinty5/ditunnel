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
    internal const string ExtraProcessId = "process_id";
    private sealed class ProbeRequest
    {
        public TaskCompletionSource<IReadOnlyList<ServerProbeResult>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId;
    }
    private static readonly ConcurrentDictionary<string, ProbeRequest> Pending = new();
    private static readonly object Sync = new();
    private static ResultReceiver? receiver;

    public static async Task<IReadOnlyList<ServerProbeResult>> ProbeAsync(Context context, IReadOnlyList<ImportedProfile> profiles, ServerProbeMode mode, CancellationToken cancellationToken, bool legacy = false)
    {
        Register(context);
        var requestId = Guid.NewGuid().ToString("N");
        var pending = new ProbeRequest();
        var completion = pending.Completion;
        Pending[requestId] = pending;
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
            await WaitForProbeProcessExitAsync(context, pending.ProcessId);
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

    private static async Task WaitForProbeProcessExitAsync(Context context, int processId)
    {
        // RunningAppProcesses can omit our process on vendor firmware. A successful
        // reply carries its PID, so the next profile must wait for that exact process.
        if (processId <= 0) processId = FindProbeProcess(context)?.Pid ?? 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!IsProbeProcess(context, processId)) return;
            await Task.Delay(100);
        }
        if (IsProbeProcess(context, processId)) global::Android.OS.Process.KillProcess(processId);
    }

    private static bool IsProbeProcess(Context context, int processId)
    {
        if (processId <= 0 || processId == global::Android.OS.Process.MyPid()) return false;
        try { return File.ReadAllText($"/proc/{processId}/cmdline").Split('\0')[0] == context.PackageName + ":probe"; }
        catch { return FindProbeProcess(context)?.Pid == processId; }
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
            if (requestId is null || json is null || !Pending.TryGetValue(requestId, out var pending)) return;
            pending.ProcessId = intent!.GetIntExtra(ExtraProcessId, 0);
            try { pending.Completion.TrySetResult(JsonSerializer.Deserialize(json, DiTunnelJsonContext.Default.ServerProbeResultArray) ?? []); }
            catch { pending.Completion.TrySetResult([]); }
        }
    }
}
