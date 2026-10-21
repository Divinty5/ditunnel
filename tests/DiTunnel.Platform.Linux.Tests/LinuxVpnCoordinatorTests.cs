using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Network;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class LinuxVpnCoordinatorTests
{
    private static readonly ImportedProfile Profile = new("Synthetic", "VLESS", "unused");
    private static Task<bool> Allow(CancellationToken _) => Task.FromResult(true);

    [Fact]
    public async Task Protected_switch_prepares_before_teardown_and_never_releases_old_firewall()
    {
        var old = new Session { Protected = true };
        var next = new Session { Protected = true };
        var sessions = new Queue<Session>([old, next]);
        await using var host = new LinuxVpnCoordinator(() => sessions.Dequeue());
        var id = Guid.NewGuid();
        await host.ConnectAsync(":1.1", id, Profile, Allow);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.SwitchAsync(":1.2", id, Profile, Allow, LinuxVpnPolicy.Default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.SwitchAsync(":1.1", id, Profile, _ => Task.FromResult(false), LinuxVpnPolicy.Default));
        Assert.Equal(0, old.Cleanups);
        await host.SwitchAsync(":1.1", id, Profile, Allow, LinuxVpnPolicy.Default);
        Assert.True(old.Prepared);
        Assert.True(old.Protected);
        Assert.Equal(0, old.Releases);
        Assert.True(host.ProtectionActive);
        Assert.Equal(VpnConnectionState.Connected, host.Status.State);
        await host.DisconnectAsync(":1.1", id);
        Assert.Equal(1, next.Releases);
        Assert.False(host.ProtectionActive);
    }

    [Fact]
    public async Task Retained_firewall_survives_denied_retry_and_allows_authorized_reconnect()
    {
        await using var host = new LinuxVpnCoordinator(() => new Session { Protected = true });
        host.RecordRetainedProtection();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ConnectAsync(":1.1", Guid.NewGuid(), Profile, _ => Task.FromResult(false)));
        Assert.True(host.ProtectionActive);
        var id = Guid.NewGuid();
        await host.ConnectAsync(":1.1", id, Profile, Allow);
        Assert.Equal(VpnConnectionState.Connected, host.Status.State);
        await host.DisconnectAsync(":1.1", id);
        Assert.False(host.ProtectionActive);
    }

    [Fact]
    public async Task Disconnect_waits_for_cleanup_and_rejects_other_sender()
    {
        var session = new Session { HoldCleanup = true };
        await using var host = new LinuxVpnCoordinator(() => session);
        var id = Guid.NewGuid();
        await host.ConnectAsync(":1.1", id, Profile, Allow);
        Assert.Equal(VpnConnectionState.Connected, host.Status.State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.DisconnectAsync(":1.2", id));
        var disconnect = host.DisconnectAsync(":1.1", id);
        await session.Cleaning.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disconnect.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ConnectAsync(":1.2", Guid.NewGuid(), Profile, Allow));
        session.AllowCleanup.SetResult();
        await disconnect;
        Assert.True(host.IsIdle);
        Assert.Equal(VpnConnectionState.Disconnected, host.Status.State);
    }

    [Fact]
    public async Task Cancel_during_authorization_is_reserved_before_first_await()
    {
        var created = false;
        await using var host = new LinuxVpnCoordinator(() => { created = true; return new Session(); });
        var id = Guid.NewGuid();
        var connect = host.ConnectAsync(":1.1", id, Profile, async token => { await Task.Delay(Timeout.Infinite, token); return true; });
        await host.DisconnectAsync(":1.1", id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.False(created);
    }

    [Fact]
    public async Task Denied_authorization_never_creates_session()
    {
        await using var host = new LinuxVpnCoordinator(() => throw new Exception("Must not create"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ConnectAsync(":1.1", Guid.NewGuid(), Profile, _ => Task.FromResult(false)));
        Assert.True(host.IsIdle);
    }

    [Fact]
    public async Task Failed_cleanup_retains_session_and_authorized_recovery_retries()
    {
        var session = new Session { FailCleanup = true };
        await using var host = new LinuxVpnCoordinator(() => session);
        var id = Guid.NewGuid();
        await host.ConnectAsync(":1.1", id, Profile, Allow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DisconnectAsync(":1.1", id));
        Assert.False(host.IsIdle);
        Assert.Equal(VpnConnectionState.Error, host.Status.State);
        session.FailCleanup = false;
        await host.RecoverAsync(":1.2", Allow, () => Task.CompletedTask);
        Assert.True(host.IsIdle);
        Assert.Equal(2, session.Cleanups);
    }

    [Fact]
    public async Task Authorized_recovery_does_not_cancel_another_live_owner()
    {
        await using var host = new LinuxVpnCoordinator(() => new Session());
        await host.ConnectAsync(":1.1", Guid.NewGuid(), Profile, Allow);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.RecoverAsync(":1.2", Allow, () => Task.CompletedTask));
        Assert.Equal(VpnConnectionState.Connected, host.Status.State);
        await host.SenderDisconnectedAsync(":1.1");
        Assert.True(host.IsIdle);
    }

    [Fact]
    public async Task Startup_failure_also_rolls_back()
    {
        var session = new Session { FailStart = true };
        await using var host = new LinuxVpnCoordinator(() => session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ConnectAsync(":1.1", Guid.NewGuid(), Profile, Allow));
        Assert.Equal(1, session.Cleanups);
        Assert.True(host.IsIdle);
    }

    private sealed class Session : ILinuxVpnSession
    {
        public bool HoldCleanup, FailCleanup, FailStart;
        public bool Protected, Prepared;
        public int Releases;
        public bool IsProtectionActive => Protected;
        public Task ReleaseProtectionAsync() { Releases++; Protected = false; return Task.CompletedTask; }
        public Task PrepareAsync(ImportedProfile profile, LinuxVpnPolicy policy, CancellationToken token)
        { Assert.Equal(0, Cleanups); Prepared = true; return Task.CompletedTask; }
        public int Cleanups;
        public TaskCompletionSource Cleaning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(ImportedProfile profile, CancellationToken token) => FailStart ? Task.FromException(new Exception()) : Task.CompletedTask;
        public Task WaitForExitAsync(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
        public async ValueTask DisposeAsync()
        {
            Cleanups++;
            Cleaning.TrySetResult();
            if (HoldCleanup) await AllowCleanup.Task;
            if (FailCleanup) throw new InvalidOperationException();
        }
    }
}
