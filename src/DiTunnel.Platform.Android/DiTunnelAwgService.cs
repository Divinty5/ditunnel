using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;

namespace DiTunnel.Platform.Android;

[Service(Name = ServiceClassName, Exported = false, Process = ":awg", Permission = global::Android.Manifest.Permission.BindVpnService)]
[Register(ServiceClassName)]
public class DiTunnelAwgService : global::Android.Net.VpnService
{
    public const string ServiceClassName = "com.divintyinteractive.ditunnel.DiTunnelAwgService";
    private Messenger? commands;
    private CommandHandler? handler;
    private AndroidAmneziaWgRuntime? runtime;
    private readonly CancellationTokenSource lifetime = new();
    private bool requested;
    private static string PidPath(Context context, bool probe) => Path.Combine(context.NoBackupFilesDir!.AbsolutePath, probe ? "awgprobe-process.pid" : "awg-process.pid");

    public override void OnCreate()
    {
        base.OnCreate();
        // Only a bound helper, with no OS TUN and no second VPN registration.
        File.WriteAllText(PidPath(this, AndroidProcessIdentity.CurrentName == PackageName + ":awgprobe"), global::Android.OS.Process.MyPid().ToString(System.Globalization.CultureInfo.InvariantCulture));
        handler = new CommandHandler(this);
        commands = new Messenger(handler);
    }
    public override IBinder? OnBind(Intent? intent) => commands?.Binder;
    public override void OnDestroy()
    {
        lifetime.Cancel();
        base.OnDestroy();
        // Fresh Go process per session. OS teardown closes all native sockets even
        // if a failed runtime cannot return from a native shutdown call.
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }
    internal static void TerminateOwnProcess(Context context, bool probe)
    {
        try
        {
            if (int.TryParse(File.ReadAllText(PidPath(context, probe)), out var pid) && pid > 0 && pid != global::Android.OS.Process.MyPid()
                && File.ReadAllText($"/proc/{pid}/cmdline").Split('\0')[0] == context.PackageName + (probe ? ":awgprobe" : ":awg"))
                global::Android.OS.Process.KillProcess(pid);
        }
        catch { }
    }
    private async Task StartAsync(Bundle data, Messenger reply)
    {
        var stage = VpnStartupStage.AwgTransport;
        void Progress(VpnStartupStage value)
        {
            stage = value;
            Send(reply, AndroidAwgBridgeClient.ProgressMessage, stage: value);
        }
        try
        {
            var manager = GetSystemService(ConnectivityService) as global::Android.Net.ConnectivityManager
                ?? throw new InvalidOperationException("Сетевой сервис Android недоступен.");
            var network = AndroidXrayProbeRunner.FindPhysicalNetwork(manager)
                ?? throw new InvalidOperationException("Нет доступной физической сети.");
            Progress(VpnStartupStage.AwgTransport);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            runtime = await Task.Run(() => new AndroidAmneziaWgRuntime(data.GetString("configuration")!, data.GetString("probe")!, data.GetString("endpoint")!, fd =>
            {
                if (!Protect(fd)) return false;
                using var descriptor = ParcelFileDescriptor.FromFd(fd);
                network.BindSocket(descriptor.FileDescriptor);
                Progress(VpnStartupStage.AwgRuntime);
                return true;
            }), lifetime.Token);
            Progress(VpnStartupStage.AwgHandshake);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(18));
            while (!runtime.HasHandshake)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested) throw new TimeoutException("AmneziaWG: сервер не подтвердил handshake за 18 секунд.");
                await Task.Delay(200, lifetime.Token);
            }
            lifetime.Token.ThrowIfCancellationRequested();
            Send(reply, AndroidAwgBridgeClient.ReadyMessage, port: runtime.Port, delay: watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception error)
        {
            var message = error is InvalidOperationException or FormatException or NotSupportedException or TimeoutException
                ? error.Message : "Не удалось запустить служебный процесс AmneziaWG.";
            Send(reply, AndroidAwgBridgeClient.ErrorMessage, error: $"{message} ({stage}; {error.GetType().Name})");
        }
        finally { data.Dispose(); reply.Dispose(); }
    }
    private static void Send(Messenger reply, int code, VpnStartupStage? stage = null, int port = 0, string? error = null, double delay = 0)
    {
        try
        {
            using var message = Message.Obtain(null, code);
            var data = new Bundle();
            if (stage is { } value) data.PutString("stage", value.ToString());
            if (port > 0) data.PutInt("port", port);
            data.PutDouble("delay", delay);
            if (error is not null) data.PutString("error", error);
            message.Data = data;
            reply.Send(message);
        }
        catch (RemoteException) { }
    }
    private sealed class CommandHandler(DiTunnelAwgService owner) : Handler(Looper.MainLooper!)
    {
        public override void HandleMessage(Message message)
        {
            if (message.What != AndroidAwgBridgeClient.StartMessage || message.ReplyTo is not { } reply || owner.requested) return;
            owner.requested = true;
            _ = owner.StartAsync(new Bundle(message.Data), new Messenger(reply.Binder));
        }
    }
}

[Service(Name = "com.divintyinteractive.ditunnel.DiTunnelAwgProbeService", Exported = false, Process = ":awgprobe", Permission = global::Android.Manifest.Permission.BindVpnService)]
[Register("com.divintyinteractive.ditunnel.DiTunnelAwgProbeService")]
public sealed class DiTunnelAwgProbeService : DiTunnelAwgService;
