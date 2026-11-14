using Android.App;
using Android.Content;
using Android.Runtime;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using System.Text.Json;
using DiTunnel.Core;

namespace DiTunnel.Platform.Android;

[Service(Name = ServiceClassName, Exported = false, Process = ":probe")]
[Register(ServiceClassName)]
public sealed class DiTunnelProbeService : Service
{
    public const string ServiceClassName = "com.divintyinteractive.ditunnel.DiTunnelProbeService";
    private CancellationTokenSource? running;
    private string? runningRequestId;

    public override global::Android.OS.IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == AndroidProbeServiceBridge.ActionCancel)
        {
            if (intent.GetStringExtra(AndroidProbeServiceBridge.ExtraRequestId) == runningRequestId) running?.Cancel();
            return StartCommandResult.NotSticky;
        }
        if (intent?.Action != AndroidProbeServiceBridge.ActionProbe) return StartCommandResult.NotSticky;
        var requestId = intent.GetStringExtra(AndroidProbeServiceBridge.ExtraRequestId);
        var profilesJson = intent.GetStringExtra(AndroidProbeServiceBridge.ExtraProfiles);
        if (requestId is null || profilesJson is null) return StartCommandResult.NotSticky;
        var profiles = JsonSerializer.Deserialize(profilesJson, DiTunnelJsonContext.Default.ImportedProfileArray);
        if (profiles is null or { Length: 0 }) return StartCommandResult.NotSticky;
        runningRequestId = requestId;
        running = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        var mode = (ServerProbeMode)intent.GetIntExtra(AndroidProbeServiceBridge.ExtraMode, 0);
        var legacy = intent.GetBooleanExtra(AndroidProbeServiceBridge.ExtraLegacy, false);
        var token = running.Token;
        SendBroadcast(new Intent(AndroidProbeServiceBridge.ActionStarted).SetPackage(PackageName)
            .PutExtra(AndroidProbeServiceBridge.ExtraRequestId, requestId)
            .PutExtra(AndroidProbeServiceBridge.ExtraProcessId, global::Android.OS.Process.MyPid()));
        _ = Task.Run(() => RunAsync(requestId, profiles, mode, legacy, token));
        return StartCommandResult.NotSticky;
    }

    private async Task RunAsync(string requestId, IReadOnlyList<ImportedProfile> profiles, ServerProbeMode mode, bool legacy, CancellationToken cancellationToken)
    {
        IReadOnlyList<ServerProbeResult> result;
        try
        {
            result = legacy && profiles.Count == 1
                ? [await AndroidXrayProbeRunner.MeasureViaProxyAsync(this, profiles[0], mode, cancellationToken)]
                : await AndroidXrayProbeRunner.MeasureBatchAsync(this, profiles, mode, cancellationToken, (index, item) =>
                    SendBroadcast(new Intent(AndroidProbeServiceBridge.ActionProgress).SetPackage(PackageName)
                        .PutExtra(AndroidProbeServiceBridge.ExtraRequestId, requestId)
                        .PutExtra(AndroidProbeServiceBridge.ExtraIndex, index)
                        .PutExtra(AndroidProbeServiceBridge.ExtraResult, JsonSerializer.Serialize(item, DiTunnelJsonContext.Default.ServerProbeResult))));
        }
        catch (Exception error) { result = profiles.Select(_ => AndroidProbePolicy.Failure(error, "Проверка", "HTTPS", 0)).ToArray(); }
        SendBroadcast(new Intent(AndroidProbeServiceBridge.ActionResult).SetPackage(PackageName)
            .PutExtra(AndroidProbeServiceBridge.ExtraRequestId, requestId)
            .PutExtra(AndroidProbeServiceBridge.ExtraProcessId, global::Android.OS.Process.MyPid())
            .PutExtra(AndroidProbeServiceBridge.ExtraResult, JsonSerializer.Serialize(result.ToArray(), DiTunnelJsonContext.Default.ServerProbeResultArray)));
        StopSelf();
        await Task.Delay(200);
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    public override void OnDestroy()
    {
        running?.Cancel();
        running?.Dispose();
        base.OnDestroy();
    }
}
