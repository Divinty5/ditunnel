using Android.Content;
using Android.OS;
using DiTunnel.Infrastructure.Xray;
using System.Security.Cryptography;

namespace DiTunnel.Platform.Android;

// Bound Messenger IPC keeps credentials inside our UID. No broadcasts carry secrets.
// :vpn loads libXray only; :awg loads the AWG Go runtime only.
internal sealed class AndroidAwgBridgeClient : IDisposable
{
    internal const int StartMessage = 1, ProgressMessage = 2, ReadyMessage = 3, ErrorMessage = 4;
    private readonly Context context;
    private readonly AmneziaWgProfileConfiguration configuration;
    private readonly string endpoint;
    private readonly string username = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly Action<VpnStartupStage> progress;
    private readonly bool probe;
    private readonly TaskCompletionSource<int> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Connection connection;
    private readonly ReplyHandler handler;
    private readonly Messenger replies;
    private bool bound, disposed;
    public event Action? Disconnected;
    public XrayProfileConfiguration? Proxy { get; private set; }
    public double HandshakeMilliseconds { get; private set; }

    public AndroidAwgBridgeClient(Context context, AmneziaWgProfileConfiguration configuration, string endpoint, Action<VpnStartupStage> progress, bool probe = false)
    {
        this.context = context;
        this.configuration = configuration;
        this.endpoint = endpoint;
        this.progress = progress;
        this.probe = probe;
        connection = new Connection(this);
        handler = new ReplyHandler(this);
        replies = new Messenger(handler);
    }
    public async Task<XrayProfileConfiguration> StartAsync(CancellationToken cancellationToken)
    {
        bound = context.BindService(new Intent(context, probe ? typeof(DiTunnelAwgProbeService) : typeof(DiTunnelAwgService)), connection, Bind.AutoCreate);
        if (!bound) throw new InvalidOperationException("Не удалось запустить служебный процесс AmneziaWG.");
        var port = await started.Task.WaitAsync(cancellationToken);
        return Proxy = configuration.CreateProxyConfiguration(port, username, password);
    }
    public async Task<double> MeasureHttpsAsync(CancellationToken cancellationToken)
    {
        var port = await started.Task.WaitAsync(cancellationToken);
        using var handler = new SocketsHttpHandler
        {
            Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{port}") { Credentials = new System.Net.NetworkCredential(username, password) },
            UseProxy = true,
            AllowAutoRedirect = false
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await http.GetAsync("https://cp.cloudflare.com/", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return watch.Elapsed.TotalMilliseconds;
    }
    private void SendStart(IBinder binder)
    {
        if (disposed) return;
        try
        {
            using var target = new Messenger(binder);
            using var message = Message.Obtain(null, StartMessage);
            message.ReplyTo = replies;
            var data = new Bundle();
            data.PutString("configuration", configuration.BuildProxyRuntimeConfiguration(endpoint, username, password));
            data.PutString("endpoint", endpoint);
            data.PutString("probe", configuration.HandshakeProbeAddress().ToString());
            message.Data = data;
            target.Send(message);
        }
        catch { started.TrySetException(new InvalidOperationException("Не удалось передать настройки служебному процессу AmneziaWG.")); }
    }
    private void LostService()
    {
        if (disposed) return;
        var ready = started.Task.IsCompletedSuccessfully;
        started.TrySetException(new InvalidOperationException("Служебный процесс AmneziaWG завершился."));
        if (ready) Disconnected?.Invoke();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (bound) { try { context.UnbindService(connection); } catch (ArgumentException) { } bound = false; }
        DiTunnelAwgService.TerminateOwnProcess(context, probe);
        handler.RemoveCallbacksAndMessages(null);
        replies.Dispose(); handler.Dispose(); connection.Dispose();
    }
    private sealed class Connection(AndroidAwgBridgeClient owner) : Java.Lang.Object, IServiceConnection
    {
        public void OnServiceConnected(ComponentName? name, IBinder? service)
        { if (service is not null) owner.SendStart(service); else owner.LostService(); }
        public void OnServiceDisconnected(ComponentName? name) => owner.LostService();
        public void OnBindingDied(ComponentName? name) => owner.LostService();
        public void OnNullBinding(ComponentName? name) => owner.LostService();
    }
    private sealed class ReplyHandler(AndroidAwgBridgeClient owner) : Handler(Looper.MainLooper!)
    {
        public override void HandleMessage(Message message)
        {
            if (owner.disposed) return;
            var data = message.Data;
            if (message.What == ProgressMessage && Enum.TryParse<VpnStartupStage>(data.GetString("stage"), out var stage)) owner.progress(stage);
            else if (message.What == ReadyMessage && data.GetInt("port") is > 0 and <= 65535)
            {
                owner.HandshakeMilliseconds = data.GetDouble("delay");
                owner.started.TrySetResult(data.GetInt("port"));
            }
            else if (message.What == ErrorMessage) owner.started.TrySetException(new InvalidOperationException(data.GetString("error") ?? "Не удалось запустить служебный процесс AmneziaWG."));
        }
    }
}
