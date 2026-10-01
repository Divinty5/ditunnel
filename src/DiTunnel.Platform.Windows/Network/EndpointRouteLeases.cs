namespace DiTunnel.Platform.Windows.Network;

internal sealed record EndpointRoutePlan(NetworkRoute? OwnedRoute, string? SourceAddress);

/// <summary>A tunnel and concurrent probes keep a temporary route alive until its last user finishes.</summary>
internal sealed class EndpointRouteLeases(Action<NetworkRoute> removeRoute)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<System.Net.IPAddress, Entry> entries = [];
    internal async Task<Lease> AcquireAsync(System.Net.IPAddress address, Func<EndpointRoutePlan> create, CancellationToken token, bool requiresRoute = false)
    {
        await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(address, out var entry))
            {
                entry = new(create());
                entries.Add(address, entry);
            }
            else if (requiresRoute && entry.Plan.SourceAddress is null) entry.Plan = create();
            entry.Users++;
            return new(this, address, entry.Plan.SourceAddress);
        }
        finally { gate.Release(); }
    }
    private async ValueTask ReleaseAsync(System.Net.IPAddress address)
    {
        await gate.WaitAsync();
        try
        {
            var entry = entries[address];
            if (--entry.Users != 0) return;
            // Keep a failed cleanup record so a subsequent lease can retry removal.
            if (entry.Plan.OwnedRoute is not null) removeRoute(entry.Plan.OwnedRoute);
            entries.Remove(address);
        }
        finally { gate.Release(); }
    }
    private sealed class Entry(EndpointRoutePlan plan) { internal EndpointRoutePlan Plan = plan; internal int Users; }
    internal sealed class Lease(EndpointRouteLeases owner, System.Net.IPAddress address, string? source) : IAsyncDisposable
    {
        internal string? SourceAddress { get; } = source;
        private int disposed;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 ? owner.ReleaseAsync(address) : ValueTask.CompletedTask;
    }
}
