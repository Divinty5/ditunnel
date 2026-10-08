using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Linux.Network;

public sealed class LinuxVpnCoordinator(Func<ILinuxVpnSession> createSession) : IAsyncDisposable
{
    private readonly object gate = new();
    private Operation? active;
    private bool stopping;
    private bool recovering;
    private bool retainedProtection;
    public bool ProtectionActive { get { lock (gate) return active?.Session?.IsProtectionActive ?? retainedProtection; } }
    public void RecordRetainedProtection()
    {
        lock (gate) { retainedProtection = true; status = new(VpnConnectionState.Error, "Kill switch сохраняет блокировку. Нажмите «Восстановить сеть» для её снятия."); }
    }
    private VpnStatus status = VpnStatus.Disconnected;
    public VpnStatus Status { get { lock (gate) return status; } }
    public VpnStatus StatusFor(string sender)
    {
        lock (gate) return active is not null && active.Sender != sender
            ? new(VpnConnectionState.Error, "VPN принадлежит другому сеансу приложения.") : status;
    }
    public bool IsIdle { get { lock (gate) return active is null; } }
    public void RecordRecoveryFailure()
    {
        lock (gate) status = new(VpnConnectionState.Error, "Очистка предыдущей сессии не завершена. Нажмите «Восстановить сеть».");
    }
    private sealed class Operation(string sender, Guid id)
    {
        public string Sender { get; } = sender;
        public Guid Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILinuxVpnSession? Session { get; set; }
        public bool ReleaseProtection { get; set; }
    }

    public Task ConnectAsync(string sender, Guid id, ImportedProfile profile, Func<CancellationToken, Task<bool>> authorize, LinuxVpnPolicy? policy = null)
    {
        Operation operation;
        lock (gate)
        {
            if (retainedProtection) throw new InvalidOperationException("Kill switch сохраняет блокировку. Сначала восстановите сеть.");
            if (stopping || recovering || active is not null) throw new InvalidOperationException("Сетевая служба уже обслуживает сессию VPN.");
            active = operation = new(sender, id);
            status = new(VpnConnectionState.Connecting);
        }
        // Reserve ownership synchronously, before authorization can yield.
        _ = RunAsync(operation, profile, authorize, policy ?? LinuxVpnPolicy.Default);
        return operation.Ready.Task;
    }

    private async Task RunAsync(Operation operation, ImportedProfile profile, Func<CancellationToken, Task<bool>> authorize, LinuxVpnPolicy policy)
    {
        Exception? failure = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation.Cancellation.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            if (!await authorize(deadline.Token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
            deadline.Token.ThrowIfCancellationRequested();
            operation.Session = createSession();
            await operation.Session.StartAsync(profile, policy, deadline.Token).ConfigureAwait(false);
            lock (gate) status = new(VpnConnectionState.Connected, ConnectedAt: DateTimeOffset.UtcNow);
            operation.Ready.TrySetResult();
            await operation.Session.WaitForExitAsync(operation.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            var cleanupFailed = false;
            lock (gate) status = new(VpnConnectionState.Disconnecting);
            try
            {
                if (operation.Session is not null)
                {
                    await operation.Session.DisposeAsync().ConfigureAwait(false);
                    if (operation.ReleaseProtection) await operation.Session.ReleaseProtectionAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                lock (gate) status = new(VpnConnectionState.Error, "Очистка сети не завершена. Нажмите «Восстановить сеть».");
                // Retain ownership and session for retry; never acknowledge successful cleanup.
                operation.Ready.TrySetException(new InvalidOperationException("Не удалось завершить подключение и очистить сеть."));
                operation.Completed.TrySetException(new InvalidOperationException("Очистка сети не завершена."));
                _ = operation.Completed.Task.Exception;
                cleanupFailed = true;
            }
            if (!cleanupFailed)
            {
            lock (gate)
            {
                active = null;
                retainedProtection = operation.Session?.IsProtectionActive == true;
                status = retainedProtection ? new(VpnConnectionState.Error, "Kill switch сохраняет блокировку. Нажмите «Восстановить сеть» для её снятия.")
                    : failure is null ? VpnStatus.Disconnected : new(VpnConnectionState.Error, "Подключение VPN завершилось с ошибкой; собственные сетевые объекты очищены.");
            }
            if (failure is null) operation.Ready.TrySetCanceled();
            else operation.Ready.TrySetException(new InvalidOperationException("Не удалось подключить VPN. Проверьте профиль, права и доступность службы."));
            operation.Completed.TrySetResult();
            operation.Cancellation.Dispose();
            }
        }
    }

    public async Task DisconnectAsync(string sender, Guid id)
    {
        Operation? operation;
        lock (gate)
        {
            operation = active;
            if (operation is null) return;
            if (operation.Sender != sender || operation.Id != id) throw new UnauthorizedAccessException();
            // A failed Connect reply must not let automatic client cleanup release a retained KS.
            operation.ReleaseProtection = operation.Ready.Task.IsCompletedSuccessfully;
            status = new(VpnConnectionState.Disconnecting);
            operation.Cancellation.Cancel();
        }
        await operation.Completed.Task.ConfigureAwait(false);
    }

    public async Task RestoreAsync(string sender, bool authorizedRecovery = false)
    {
        Operation? operation;
        lock (gate)
        {
            operation = active;
            if (operation is null) { status = VpnStatus.Disconnected; return; }
            if (operation.Sender != sender && !(authorizedRecovery && operation.Completed.Task.IsCompleted)) throw new UnauthorizedAccessException();
            operation.Cancellation.Cancel();
        }
        try { await operation.Completed.Task.ConfigureAwait(false); } catch { }
        lock (gate) if (active != operation) return;
        if (operation.Session is not null)
        {
            await operation.Session.DisposeAsync().ConfigureAwait(false);
            await operation.Session.ReleaseProtectionAsync().ConfigureAwait(false);
        }
        lock (gate) { if (active == operation) active = null; retainedProtection = false; status = VpnStatus.Disconnected; }
        operation.Cancellation.Dispose();
    }

    public async Task SenderDisconnectedAsync(string sender)
    {
        Operation? operation;
        lock (gate) { operation = active; if (operation?.Sender != sender) return; }
        try { operation.Cancellation.Cancel(); await operation.Completed.Task.ConfigureAwait(false); }
        catch { /* Retained error state allows an authorized recovery on the next UI launch. */ }
    }

    public async Task RecoverAsync(string sender, Func<CancellationToken, Task<bool>> authorize, Func<Task> recover)
    {
        lock (gate)
        {
            if (stopping || recovering) throw new InvalidOperationException();
            recovering = true;
        }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            if (!await authorize(deadline.Token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
            await RestoreAsync(sender, authorizedRecovery: true).ConfigureAwait(false);
            await recover().ConfigureAwait(false);
            lock (gate) { retainedProtection = false; status = VpnStatus.Disconnected; }
        }
        finally { lock (gate) recovering = false; }
    }

    public async ValueTask DisposeAsync()
    {
        Operation? operation;
        lock (gate) { stopping = true; operation = active; }
        if (operation is not null) { operation.Cancellation.Cancel(); try { await operation.Completed.Task.ConfigureAwait(false); } catch { } }
    }
}
