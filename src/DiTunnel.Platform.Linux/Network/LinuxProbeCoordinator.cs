using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Linux.Network;

// One lease per bus connection, with a global bound. Cancellation is acknowledged
// only after the backend has disposed its runtime and private configuration.
public sealed class LinuxProbeCoordinator(IServerProbe backend) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<(string Sender, Guid Id), Lease> leases = [];
    private bool stopping;
    private const int MaximumConcurrentProbes = 4;

    private sealed class Lease(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<ServerProbeResult> ProbeAsync(string sender, Guid id, ImportedProfile profile, ServerProbeMode mode, CancellationToken token,
        Func<CancellationToken, Task<bool>>? authorize = null)
    {
        if (string.IsNullOrEmpty(sender) || id == Guid.Empty) throw new ArgumentException("Некорректный идентификатор проверки.");
        var key = (sender, id);
        Lease lease;
        lock (gate)
        {
            if (stopping) throw new ObjectDisposedException(nameof(LinuxProbeCoordinator));
            if (leases.Count >= MaximumConcurrentProbes || leases.Keys.Any(item => item.Sender == sender))
                return new(null, "Сетевая служба занята. Повторите проверку.", IsDeferred: true);
            lease = new(CancellationTokenSource.CreateLinkedTokenSource(token));
            lease.Cancellation.CancelAfter(LinuxNetworkProtocol.ProbeTimeout);
            leases.Add(key, lease);
        }
        try
        {
            if (authorize is not null && !await authorize(lease.Cancellation.Token).ConfigureAwait(false))
                return new(null, "Проверка серверов не разрешена для этой сессии.");
            lease.Cancellation.Token.ThrowIfCancellationRequested();
            return await backend.ProbeAsync(profile, lease.Cancellation.Token, mode).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                leases.Remove(key);
                lease.Cancellation.Dispose();
                lease.Completed.TrySetResult();
            }
        }
    }

    public Task CancelAsync(string sender, Guid id)
    {
        lock (gate)
        {
            if (!leases.TryGetValue((sender, id), out var lease)) return Task.CompletedTask;
            lease.Cancellation.Cancel();
            return lease.Completed.Task;
        }
    }

    public Task SenderDisconnectedAsync(string sender)
    {
        lock (gate)
        {
            var owned = leases.Where(item => item.Key.Sender == sender).Select(item => item.Value).ToArray();
            foreach (var lease in owned) lease.Cancellation.Cancel();
            return Task.WhenAll(owned.Select(lease => lease.Completed.Task));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task completion;
        lock (gate)
        {
            stopping = true;
            var active = leases.Values.ToArray();
            completion = Task.WhenAll(active.Select(lease => lease.Completed.Task));
            foreach (var lease in active) lease.Cancellation.Cancel();
        }
        await completion.ConfigureAwait(false);
    }
}
