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
    internal const string ActionStarted = "com.divintyinteractive.ditunnel.status.PROBE_STARTED";
    internal const string ActionResult = "com.divintyinteractive.ditunnel.status.PROBE_RESULT";
    internal const string ActionProgress = "com.divintyinteractive.ditunnel.status.PROBE_PROGRESS";
    internal const string ExtraRequestId = "request_id";
    internal const string ExtraProfiles = "profiles";
    internal const string ExtraMode = "mode";
    internal const string ExtraLegacy = "legacy";
    internal const string ExtraResult = "result";
    internal const string ExtraIndex = "index";
    internal const string ExtraProcessId = "process_id";
    private sealed class ProbeRequest(int count, Action<int, ServerProbeResult>? report)
    {
        public TaskCompletionSource<IReadOnlyList<ServerProbeResult>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId;
        public void Report(int index, ServerProbeResult result)
        {
            if (!Completion.Task.IsCompleted && index >= 0 && index < count) report?.Invoke(index, result);
        }
    }
    private static readonly ConcurrentDictionary<string, ProbeRequest> Pending = new();
    private static readonly object Sync = new();
    private static ResultReceiver? receiver;

    public static async Task<IReadOnlyList<ServerProbeResult>> ProbeAsync(Context context, IReadOnlyList<ImportedProfile> profiles,
        ServerProbeMode mode, CancellationToken cancellationToken, bool legacy = false, Action<int, ServerProbeResult>? report = null)
    {
        Register(context);
        var requestId = Guid.NewGuid().ToString("N");
        var pending = new ProbeRequest(profiles.Count, report);
        var completion = pending.Completion;
        Pending[requestId] = pending;
        var started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            started = context.StartService(new Intent(context, typeof(DiTunnelProbeService)).SetAction(ActionProbe)
                .PutExtra(ExtraRequestId, requestId)
                .PutExtra(ExtraProfiles, JsonSerializer.Serialize(profiles.ToArray(), DiTunnelJsonContext.Default.ImportedProfileArray))
                .PutExtra(ExtraMode, (int)mode)
                .PutExtra(ExtraLegacy, legacy)) is not null;
            if (!started) throw new AndroidProbeException("Служба проверки", "Runtime");
            using var cancellation = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                try { context.StartService(new Intent(context, typeof(DiTunnelProbeService)).SetAction(ActionCancel).PutExtra(ExtraRequestId, requestId)); }
                catch (Java.Lang.IllegalStateException) { /* Android can reject service starts while backgrounded. */ }
                catch (Java.Lang.SecurityException) { /* Wait for the already-started helper below. */ }
            });
            return await completion.Task;
        }
        finally
        {
            try { if (started) await WaitForProbeProcessExitAsync(context, pending); }
            finally { Pending.TryRemove(requestId, out _); }
        }
    }

    private static void Register(Context context)
    {
        lock (Sync)
        {
            if (receiver is not null) return;
            receiver = new ResultReceiver();
            var filter = new IntentFilter(ActionResult);
            filter.AddAction(ActionStarted);
            filter.AddAction(ActionProgress);
            if (OperatingSystem.IsAndroidVersionAtLeast(33)) context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
            else
#pragma warning disable CA1422
                context.RegisterReceiver(receiver, filter);
#pragma warning restore CA1422
        }
    }

    private static async Task WaitForProbeProcessExitAsync(Context context, ProbeRequest pending)
    {
        // RunningAppProcesses can omit our process on vendor firmware. A successful
        // reply carries its PID, so the next profile must wait for that exact process.
        var processId = Volatile.Read(ref pending.ProcessId);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (processId <= 0) processId = Volatile.Read(ref pending.ProcessId);
            if (processId <= 0) processId = FindProbeProcess(context)?.Pid ?? 0;
            if (processId > 0 && !IsProbeProcess(context, processId)) return;
            await Task.Delay(100);
        }
        if (IsProbeProcess(context, processId))
        {
            global::Android.OS.Process.KillProcess(processId);
            for (var attempt = 0; attempt < 10 && IsProbeProcess(context, processId); attempt++)
                await Task.Delay(100);
            if (IsProbeProcess(context, processId)) throw new AndroidProbeException("Служба проверки", "Runtime");
        }
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
            if (requestId is null || !Pending.TryGetValue(requestId, out var pending)) return;
            if (intent!.Action == ActionProgress && json is not null)
            {
                try
                {
                    var result = JsonSerializer.Deserialize(json, DiTunnelJsonContext.Default.ServerProbeResult);
                    if (result is not null) pending.Report(intent.GetIntExtra(ExtraIndex, -1), result);
                }
                catch (JsonException) { }
                return;
            }
            Volatile.Write(ref pending.ProcessId, intent!.GetIntExtra(ExtraProcessId, 0));
            if (intent.Action != ActionResult || json is null) return;
            try { pending.Completion.TrySetResult(JsonSerializer.Deserialize(json, DiTunnelJsonContext.Default.ServerProbeResultArray) ?? []); }
            catch { pending.Completion.TrySetResult([]); }
        }
    }
}
