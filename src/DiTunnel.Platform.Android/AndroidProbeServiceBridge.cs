using Android.Content;
using Android.OS;
using DiTunnel.Core;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using System.Text.Json;

namespace DiTunnel.Platform.Android;

internal static class AndroidProbeServiceBridge
{
    internal const int Probe = 1, Result = 2;
    internal const string Payload = "payload";

    internal static async Task<ServerProbeResult> ProbeAsync(Context context, ImportedProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handler = new ReplyHandler();
        using var replies = new Messenger(handler);
        using var connection = new Connection(handler.Completion);
        using var intent = new Intent(context, typeof(DiTunnelProbeService));
        if (!context.BindService(intent, connection, Bind.AutoCreate)) throw Unavailable();
        try
        {
            var binder = await connection.Binder.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var target = new Messenger(binder);
            using var message = Message.Obtain(null, Probe) ?? throw Unavailable();
            using var data = new Bundle();
            data.PutString(Payload, JsonSerializer.Serialize(profile, DiTunnelJsonContext.Default.ImportedProfile));
            message.Data = data;
            message.ReplyTo = replies;
            target.Send(message);
            return await handler.Completion.Task.WaitAsync(AndroidProbePolicy.XrayReplyBudget, cancellationToken);
        }
        finally
        {
            try { context.UnbindService(connection); }
            catch (ArgumentException) { }
            handler.RemoveCallbacksAndMessages(null);
        }
    }

    private static AndroidProbeException Unavailable() => new("Служба проверки", "Runtime");

    private sealed class Connection(TaskCompletionSource<ServerProbeResult> result) : Java.Lang.Object, IServiceConnection
    {
        public TaskCompletionSource<IBinder> Binder { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service is null) LostService();
            else Binder.TrySetResult(service);
        }

        public void OnServiceDisconnected(ComponentName? name) => LostService();
        public void OnBindingDied(ComponentName? name) => LostService();
        public void OnNullBinding(ComponentName? name) => LostService();

        private void LostService()
        {
            if (!Binder.TrySetException(Unavailable())) result.TrySetException(Unavailable());
        }
    }

    private sealed class ReplyHandler() : Handler(Looper.MainLooper!)
    {
        public TaskCompletionSource<ServerProbeResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void HandleMessage(Message message)
        {
            if (message.What != Result) return;
            try
            {
                var payload = message.Data?.GetString(Payload);
                var result = payload is null ? null : JsonSerializer.Deserialize(payload, DiTunnelJsonContext.Default.ServerProbeResult);
                if (result is null) throw Unavailable();
                Completion.TrySetResult(result);
            }
            catch (Exception error) { Completion.TrySetException(error); }
        }
    }
}
