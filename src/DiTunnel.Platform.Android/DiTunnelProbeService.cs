using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using DiTunnel.Core;
using DiTunnel.Core.Connection;
using System.Diagnostics;
using System.Text.Json;

namespace DiTunnel.Platform.Android;

[Service(Name = ServiceClassName, Exported = false, Process = ":probe")]
[Register(ServiceClassName)]
public sealed class DiTunnelProbeService : Service
{
    public const string ServiceClassName = "com.divintyinteractive.ditunnel.DiTunnelProbeService";
    private readonly CancellationTokenSource lifetime = new();
    private CommandHandler? handler;
    private Messenger? commands;

    public override void OnCreate()
    {
        base.OnCreate();
        handler = new CommandHandler(this);
        commands = new Messenger(handler);
    }

    public override IBinder? OnBind(Intent? intent) => commands?.Binder;

    public override void OnDestroy()
    {
        lifetime.Cancel();
        lifetime.Dispose();
        handler?.RemoveCallbacksAndMessages(null);
        commands?.Dispose();
        handler?.Dispose();
        base.OnDestroy();
    }

    private async Task RunAsync(string payload, Messenger reply, CancellationToken cancellationToken)
    {
        using (reply)
        {
            var watch = Stopwatch.StartNew();
            ServerProbeResult result;
            try
            {
                var profile = JsonSerializer.Deserialize(payload, DiTunnelJsonContext.Default.ImportedProfile)
                    ?? throw new JsonException();
                result = await AndroidXrayProbeRunner.MeasureAsync(this, profile, cancellationToken);
            }
            catch (Exception error)
            {
                result = AndroidProbePolicy.ServiceFailure(error, watch.Elapsed.TotalMilliseconds);
            }
            if (cancellationToken.IsCancellationRequested) return;
            using var message = Message.Obtain(null, AndroidProbeServiceBridge.Result);
            if (message is null) return;
            using var data = new Bundle();
            data.PutString(AndroidProbeServiceBridge.Payload, JsonSerializer.Serialize(result, DiTunnelJsonContext.Default.ServerProbeResult));
            message.Data = data;
            try { reply.Send(message); }
            catch (RemoteException) { }
        }
    }

    private sealed class CommandHandler(DiTunnelProbeService owner) : Handler(Looper.MainLooper!)
    {
        public override void HandleMessage(Message message)
        {
            if (message.What != AndroidProbeServiceBridge.Probe || message.ReplyTo is not { } reply ||
                message.Data?.GetString(AndroidProbeServiceBridge.Payload) is not { } payload) return;
            var client = new Messenger(reply.Binder);
            var token = owner.lifetime.Token;
            _ = Task.Run(() => owner.RunAsync(payload, client, token));
        }
    }
}
