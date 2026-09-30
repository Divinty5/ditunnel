namespace DiTunnel.Platform.Windows;

// The deterministic WFP probe slot must remain owned until its filter is removed.
internal sealed class AsyncProbeLeaseGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal async Task<IAsyncDisposable> AcquireAsync(Func<Task> install, Func<ValueTask> remove, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await install(); return new Lease(gate, remove); }
        catch { gate.Release(); throw; }
    }
    private sealed class Lease(SemaphoreSlim gate, Func<ValueTask> remove) : IAsyncDisposable
    {
        private int disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await remove(); }
            finally { gate.Release(); }
        }
    }
}
