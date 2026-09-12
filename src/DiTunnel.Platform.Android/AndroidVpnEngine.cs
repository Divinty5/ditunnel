using Android.Content;
using ConnectivityManager = Android.Net.ConnectivityManager;
using NetCapability = Android.Net.NetCapability;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Android;

public sealed class AndroidVpnEngine : IProfileVpnEngine
{
    private readonly Context context;
    private readonly IAndroidVpnPermissionRequester permissionRequester;
    private readonly Func<SplitTunnelPolicy> splitTunnelPolicy;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? healthCancellation;
    private CancellationTokenSource? networkChangeCancellation;
    private PhysicalNetworkCallback? networkCallback;
    private CancellationTokenSource? recoveryCancellation;
    private readonly object networkSignalLock = new();
    private TaskCompletionSource networkChanged = NewNetworkSignal();
    private ImportedProfile? activeProfile;
    private VpnStatus status;
    private bool disposed;

    public AndroidVpnEngine(Context context, IAndroidVpnPermissionRequester permissionRequester, Func<SplitTunnelPolicy>? splitTunnelPolicy = null)
    {
        this.context = context.ApplicationContext
            ?? throw new InvalidOperationException("Android ApplicationContext недоступен.");
        this.permissionRequester = permissionRequester;
        this.splitTunnelPolicy = splitTunnelPolicy ?? (() => SplitTunnelPolicy.Default);
        status = AndroidVpnRuntimeState.ReadStatus(this.context);
        activeProfile = AndroidVpnRuntimeState.ReadActiveProfile(this.context);
        AndroidVpnServiceBridge.Register(this.context);
        AndroidVpnServiceBridge.StatusReceived += OnServiceStatusReceived;
        RegisterNetworkMonitor();
        if (status.State == VpnConnectionState.Connected) StartHealthMonitor();
    }

    public VpnStatus Status => status;
    public ImportedProfile? ActiveProfile => activeProfile;
    public bool RequiresAdministrator => false;
    public bool IsNetworkProtectionActive => status.State is VpnConnectionState.Connected or VpnConnectionState.Reconnecting;
    public event EventHandler<VpnStatus>? StatusChanged;

    public async Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        CancelRecovery();
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (status.State is not (VpnConnectionState.Disconnected or VpnConnectionState.Error))
                throw new InvalidOperationException("VPN уже подключается или подключён.");
            SetStatus(new(VpnConnectionState.Connecting, "Запрашиваем разрешение Android на VPN…"));
            if (!await HasValidatedPhysicalNetworkAsync(cancellationToken))
            {
                const string message = "Нет подключения к интернету. Включите Wi-Fi или мобильную сеть и повторите подключение.";
                SetStatus(new(VpnConnectionState.Error, message));
                throw new InvalidOperationException(message);
            }
            await ConnectCoreAsync(profile, cancellationToken, recovering: false);
        }
        finally { lifecycle.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelRecovery();
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (status.State == VpnConnectionState.Disconnected && !AndroidVpnRuntimeState.IsVpnProcessAlive(context)) return;
            SetStatus(new(VpnConnectionState.Disconnecting, "Останавливаем VPN…"));
            StopHealthMonitor();
            await StopCoreAsync(cancellationToken);
            activeProfile = null;
            SetStatus(new(VpnConnectionState.Disconnected, "VPN отключён."));
        }
        finally { lifecycle.Release(); }
    }

    public async Task SwitchAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        CancelRecovery();
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            SetStatus(new(VpnConnectionState.Reconnecting, $"Останавливаем текущий туннель перед переключением на {profile.Name}…"));
            StopHealthMonitor();
            await StopCoreAsync(cancellationToken);
            SetStatus(new(VpnConnectionState.Reconnecting, $"Запускаем Android VpnService и Xray-core для сервера {profile.Name}…"));
            await ConnectCoreAsync(profile, cancellationToken, recovering: true);
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        CancelRecovery();
        lifetime.Cancel();
        if (status.State != VpnConnectionState.Disconnected)
            await DisconnectAsync(CancellationToken.None);
        UnregisterNetworkMonitor();
        AndroidVpnServiceBridge.StatusReceived -= OnServiceStatusReceived;
        lifetime.Dispose();
        lifecycle.Dispose();
    }

    private async Task ConnectCoreAsync(ImportedProfile profile, CancellationToken cancellationToken, bool recovering)
    {
        if (!await permissionRequester.RequestAsync(cancellationToken))
        {
            SetStatus(new(VpnConnectionState.Disconnected, "Разрешение на VPN не предоставлено."));
            return;
        }

        SetStatus(new(recovering ? VpnConnectionState.Reconnecting : VpnConnectionState.Connecting,
            recovering ? $"Запускаем новый туннель через {profile.Name}…" : "Запускаем Android VpnService и Xray-core…"));
        var completion = AndroidVpnServiceBridge.ExpectStart();
        context.StartForegroundService(AndroidVpnServiceBridge.CreateStartIntent(context, profile, splitTunnelPolicy()));
        try
        {
            var started = await completion.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (started.State != VpnConnectionState.Connected)
                throw new InvalidOperationException(started.Message ?? "Xray не запустил VPN.");
            activeProfile = profile;
            SetStatus(new(VpnConnectionState.Connected,
                "Туннель активен. Android VpnService и Xray-core запущены; доступность сервера проверяется отдельно.",
                DateTimeOffset.UtcNow));
            StartHealthMonitor();
        }
        catch (Exception error)
        {
            if (!recovering) StopHealthMonitor();
            try { await StopCoreAsync(CancellationToken.None); } catch { }
            if (!recovering)
            {
                var message = error is InvalidOperationException or NotSupportedException or FormatException or TimeoutException
                    ? error.Message
                    : "Не удалось запустить Android VPN.";
                SetStatus(new(VpnConnectionState.Error, message));
            }
            throw;
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        // RunningAppProcesses is deliberately incomplete on current Android versions and
        // some vendor firmwares. It must never be used as a condition for sending STOP:
        // otherwise the UI becomes disconnected while VpnService keeps its TUN descriptor.
        var completion = AndroidVpnServiceBridge.ExpectStop();
        context.StartService(new Intent(context, typeof(DiTunnelVpnService)).SetAction(DiTunnelVpnService.ActionStop));
        try { _ = await completion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch
        {
            AndroidVpnRuntimeState.TerminateVpnProcess(context);
            throw;
        }
        // STOPPED has now been delivered to this process, so it is safe to terminate the
        // old isolated Go runtime without racing the acknowledgement broadcast.
        AndroidVpnRuntimeState.TerminateVpnProcess(context);
        // RunningAppProcesses is stale/incomplete on several vendor firmwares; polling it
        // added an unpredictable 2-5 seconds. SIGKILL plus a fixed teardown window is the
        // deterministic boundary before Android creates the fresh :vpn process.
        await Task.Delay(400, cancellationToken);
    }

    private void StartHealthMonitor()
    {
        StopHealthMonitor();
        healthCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ScheduleNetworkEvaluation();
    }

    private void RegisterNetworkMonitor()
    {
        var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
        if (manager is null || networkCallback is not null) return;
        var callback = new PhysicalNetworkCallback(ScheduleNetworkEvaluation);
        networkCallback = callback;
        using var builder = new global::Android.Net.NetworkRequest.Builder()
            ?? throw new InvalidOperationException("Android не создал запрос наблюдения за сетью.");
        _ = builder.AddCapability(NetCapability.Internet);
        _ = builder.AddCapability(NetCapability.NotVpn);
        var request = builder.Build() ?? throw new InvalidOperationException("Android не создал запрос наблюдения за сетью.");
        manager.RegisterNetworkCallback(request, callback);
    }

    private void StopHealthMonitor()
    {
        networkChangeCancellation?.Cancel();
        networkChangeCancellation?.Dispose();
        networkChangeCancellation = null;
        healthCancellation?.Cancel();
        healthCancellation?.Dispose();
        healthCancellation = null;
    }

    private void UnregisterNetworkMonitor()
    {
        if (networkCallback is not null && context.GetSystemService(Context.ConnectivityService) is ConnectivityManager manager)
        {
            try { manager.UnregisterNetworkCallback(networkCallback); } catch (ArgumentException) { }
        }
        networkCallback?.Dispose();
        networkCallback = null;
    }

    private void ScheduleNetworkEvaluation()
    {
        lock (networkSignalLock)
        {
            var previous = networkChanged;
            networkChanged = NewNetworkSignal();
            previous.TrySetResult();
        }
        var healthToken = healthCancellation?.Token;
        if (healthToken is null || healthToken.Value.IsCancellationRequested) return;
        networkChangeCancellation?.Cancel();
        networkChangeCancellation?.Dispose();
        networkChangeCancellation = CancellationTokenSource.CreateLinkedTokenSource(healthToken.Value);
        _ = EvaluateNetworkAfterChangeAsync(networkChangeCancellation.Token);
    }

    private async Task EvaluateNetworkAfterChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // ConnectivityManager callbacks are already the authoritative event source.
            // Keep only a short settling window for capability handover instead of the
            // previous three-second polling-style delay.
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            if (!HasValidatedPhysicalNetwork()) BeginRecovery();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private bool HasValidatedPhysicalNetwork()
    {
        return networkCallback?.HasValidatedNetwork == true;
    }

    private async Task<bool> HasValidatedPhysicalNetworkAsync(CancellationToken cancellationToken)
    {
        if (HasValidatedPhysicalNetwork()) return true;
        Task signal;
        lock (networkSignalLock) signal = networkChanged.Task;
        try { await signal.WaitAsync(TimeSpan.FromMilliseconds(350), cancellationToken); }
        catch (TimeoutException) { }
        // onAvailable is followed by onCapabilitiesChanged; allow that ordered callback
        // to update the set before judging a manual connection attempt.
        if (!HasValidatedPhysicalNetwork()) await Task.Delay(50, cancellationToken);
        return HasValidatedPhysicalNetwork();
    }

    private void BeginRecovery()
    {
        var profile = activeProfile;
        if (profile is null || recoveryCancellation is not null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        recoveryCancellation = cancellation;
        _ = RecoverAndClearAsync(profile, cancellation);
    }

    private async Task RecoverAndClearAsync(ImportedProfile profile, CancellationTokenSource cancellation)
    {
        try { await RecoverAsync(profile, cancellation.Token); }
        finally
        {
            if (ReferenceEquals(recoveryCancellation, cancellation)) recoveryCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task RecoverAsync(ImportedProfile profile, CancellationToken cancellationToken)
    {
        var failedAttempts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await WaitForValidatedPhysicalNetworkAsync(cancellationToken);
            var schedule = ReconnectSchedule.CreateForActiveTunnel(failedAttempts);
            if (schedule is null)
            {
                SetStatus(new(VpnConnectionState.Error, "Автоматическое восстановление остановлено после 10 попыток. Повторите подключение вручную."));
                return;
            }
            var delay = failedAttempts == 0 ? TimeSpan.Zero : schedule.Delay;
            SetStatus(new(VpnConnectionState.Reconnecting, delay == TimeSpan.Zero
                ? $"Сеть восстановлена. Переподключаем VPN, попытка {schedule.Attempt} из 10…"
                : $"Соединение потеряно. Попытка {schedule.Attempt} из 10 через {delay.TotalSeconds:0} с…"));
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                await lifecycle.WaitAsync(cancellationToken);
                try
                {
                    try { await StopCoreAsync(CancellationToken.None); } catch { AndroidVpnRuntimeState.TerminateVpnProcess(context); }
                    await ConnectCoreAsync(profile, cancellationToken, recovering: true);
                    return;
                }
                finally { lifecycle.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { failedAttempts++; }
        }
    }

    private async Task WaitForValidatedPhysicalNetworkAsync(CancellationToken cancellationToken)
    {
        while (!HasValidatedPhysicalNetwork())
        {
            SetStatus(new(VpnConnectionState.Reconnecting, "Нет подключения к интернету. Ожидаем восстановление сети…"));
            Task signal;
            lock (networkSignalLock) signal = networkChanged.Task;
            if (HasValidatedPhysicalNetwork()) return;
            await signal.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource NewNetworkSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void CancelRecovery()
    {
        recoveryCancellation?.Cancel();
        recoveryCancellation?.Dispose();
        recoveryCancellation = null;
    }

    private void OnServiceStatusReceived(object? sender, VpnStatus value)
    {
        if (status.State is VpnConnectionState.Connecting or VpnConnectionState.Disconnecting or VpnConnectionState.Reconnecting) return;
        if (value.State == VpnConnectionState.Disconnected)
        {
            CancelRecovery();
            StopHealthMonitor();
            activeProfile = null;
        }
        SetStatus(value);
    }

    private void SetStatus(VpnStatus value)
    {
        status = value;
        StatusChanged?.Invoke(this, value);
    }

    private sealed class PhysicalNetworkCallback(Action changed) : ConnectivityManager.NetworkCallback
    {
        private readonly object gate = new();
        private readonly HashSet<int> validatedNetworks = [];

        public bool HasValidatedNetwork
        {
            get { lock (gate) return validatedNetworks.Count > 0; }
        }

        public override void OnAvailable(global::Android.Net.Network network)
        {
            // On Android 8+ the ordered capabilities callback follows immediately and
            // is the authoritative source for validation state.
            changed();
        }

        public override void OnLost(global::Android.Net.Network network)
        {
            lock (gate) validatedNetworks.Remove(network.GetHashCode());
            changed();
        }

        public override void OnCapabilitiesChanged(global::Android.Net.Network network, global::Android.Net.NetworkCapabilities capabilities)
        {
            var validated = capabilities.HasCapability(NetCapability.NotVpn)
                && capabilities.HasCapability(NetCapability.Internet)
                && capabilities.HasCapability(NetCapability.Validated);
            lock (gate)
            {
                if (validated) validatedNetworks.Add(network.GetHashCode());
                else validatedNetworks.Remove(network.GetHashCode());
            }
            changed();
        }
    }
}
