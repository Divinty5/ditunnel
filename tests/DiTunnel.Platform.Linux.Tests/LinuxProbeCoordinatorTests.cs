using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Network;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class LinuxProbeCoordinatorTests
{
    private static readonly ImportedProfile Profile = new("Synthetic", "VLESS", "unused");

    [Fact]
    public async Task Cancel_is_scoped_to_sender_and_waits_for_cleanup()
    {
        var backend = new BlockingProbe();
        await using var host = new LinuxProbeCoordinator(backend);
        var id = Guid.NewGuid();
        var operation = host.ProbeAsync(":1.1", id, Profile, ServerProbeMode.Fast, default);
        await backend.Started.Task;
        await host.CancelAsync(":1.2", id);
        Assert.False(operation.IsCompleted);
        var cancelled = host.CancelAsync(":1.1", id);
        await backend.Cleaning.Task;
        Assert.False(cancelled.IsCompleted);
        backend.CleanupAllowed.SetResult();
        await cancelled.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Duplicate_sender_is_deferred_without_starting_another_runtime()
    {
        var backend = new BlockingProbe();
        await using var host = new LinuxProbeCoordinator(backend);
        var operation = host.ProbeAsync(":1.1", Guid.NewGuid(), Profile, ServerProbeMode.Fast, default);
        await backend.Started.Task;
        var busy = await host.ProbeAsync(":1.1", Guid.NewGuid(), Profile, ServerProbeMode.Fast, default);
        Assert.True(busy.IsDeferred);
        Assert.Equal(1, backend.Calls);
        backend.CleanupAllowed.SetResult();
        await host.SenderDisconnectedAsync(":1.1");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Disconnect_cancels_only_owned_lease()
    {
        var backend = new BlockingProbe();
        await using var host = new LinuxProbeCoordinator(backend);
        var operation = host.ProbeAsync(":1.1", Guid.NewGuid(), Profile, ServerProbeMode.Fast, default);
        await backend.Started.Task;
        await host.SenderDisconnectedAsync(":1.2");
        Assert.False(operation.IsCompleted);
        backend.CleanupAllowed.SetResult();
        await host.SenderDisconnectedAsync(":1.1");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Cancellation_during_authorization_never_starts_backend()
    {
        var backend = new BlockingProbe();
        await using var host = new LinuxProbeCoordinator(backend);
        var id = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = host.ProbeAsync(":1.1", id, Profile, ServerProbeMode.Fast, default, async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });
        await entered.Task;
        await host.CancelAsync(":1.1", id).WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task Denied_authorization_releases_lease_without_backend()
    {
        var backend = new BlockingProbe();
        await using var host = new LinuxProbeCoordinator(backend);
        var result = await host.ProbeAsync(":1.1", Guid.NewGuid(), Profile, ServerProbeMode.Fast, default, _ => Task.FromResult(false));
        Assert.Null(result.Milliseconds);
        Assert.Equal(0, backend.Calls);
        Assert.False(result.IsDeferred);
    }

    [Fact]
    public async Task Global_bound_defers_fifth_sender()
    {
        var backend = new BlockingProbe();
        await using var host = new LinuxProbeCoordinator(backend);
        var active = Enumerable.Range(1, 4).Select(index => host.ProbeAsync($":1.{index}", Guid.NewGuid(), Profile, ServerProbeMode.Fast, default)).ToArray();
        var result = await host.ProbeAsync(":1.5", Guid.NewGuid(), Profile, ServerProbeMode.Fast, default);
        Assert.True(result.IsDeferred);
        Assert.Equal(4, backend.Calls);
        backend.CleanupAllowed.SetResult();
        await host.DisposeAsync();
        foreach (var operation in active) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Stop_rejects_new_operations()
    {
        var host = new LinuxProbeCoordinator(new BlockingProbe());
        await host.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.ProbeAsync(":1.1", Guid.NewGuid(), Profile, ServerProbeMode.Fast, default));
    }

    private sealed class BlockingProbe : IServerProbe
    {
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleaning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupAllowed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return new(1, "Synthetic"); }
            finally { Cleaning.TrySetResult(); await CleanupAllowed.Task; }
        }
    }
}
