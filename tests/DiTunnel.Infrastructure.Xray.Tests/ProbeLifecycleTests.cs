using System.Net;
using System.Net.Sockets;
using DiTunnel.Platform.Windows;

namespace DiTunnel.Infrastructure.Xray.Tests;

[Collection("Xray runtime")]
public sealed class ProbeLifecycleTests
{
    [Fact]
    public async Task WfpSlotCannotBeReplacedUntilPreviousProbeHasRemovedItsFilter()
    {
        var gate = new AsyncProbeLeaseGate();
        var installed = 0;
        var removalFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await gate.AcquireAsync(() => { installed = 1; return Task.CompletedTask; },
            async () => { await removalFinished.Task; installed = 0; }, default);
        var pending = gate.AcquireAsync(() => { Assert.Equal(0, installed); installed = 2; return Task.CompletedTask; },
            () => { installed = 0; return ValueTask.CompletedTask; }, default);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, installed);
        var disposing = first.DisposeAsync().AsTask();
        Assert.False(pending.IsCompleted);
        removalFinished.SetResult();
        await disposing;
        await using var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await first.DisposeAsync();
        Assert.Equal(2, installed);
    }

    [Fact]
    public async Task CancelledWaitAndFailedInstallDoNotLeakProbeSlot()
    {
        var gate = new AsyncProbeLeaseGate();
        var first = await gate.AcquireAsync(() => Task.CompletedTask, () => ValueTask.CompletedTask, default);
        using var cancellation = new CancellationTokenSource();
        var pending = gate.AcquireAsync(() => throw new Exception("Should not install"), () => ValueTask.CompletedTask, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await first.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.AcquireAsync(() => throw new InvalidOperationException(), () => ValueTask.CompletedTask, default));
        await using var recovered = await gate.AcquireAsync(() => Task.CompletedTask, () => ValueTask.CompletedTask, default).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualXrayProbeDoesNotNeedUdpInbound(bool enableUdp)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var runtime = Path.Combine(root.FullName, ".tools", "xray", "26.3.27", "windows-x64", "xray.exe");
        Assert.True(File.Exists(runtime));
        // Windows can assign UDP an ephemeral port reserved/excluded for TCP. Reserve
        // a usable TCP number first, then hold its UDP counterpart for the conflict test.
        using var udp = ReserveUdpWithUsableTcpPort();
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var path = Path.Combine(Path.GetTempPath(), "xray-loopback-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var configuration = XrayProfileConverter.Convert(new("Synthetic", "VLESS", "vless://11111111-1111-4111-8111-111111111111@192.0.2.1:443?security=none"));
            await File.WriteAllTextAsync(path, configuration.Build("192.0.2.1", false, port, enableSocksUdp: enableUdp));
            await using var manager = new XrayProcessManager(new XrayOptions
            { ExecutablePath = runtime, WorkingDirectory = Path.GetDirectoryName(runtime)!, ValidateConfigurationBeforeStart = false, ShutdownTimeout = TimeSpan.FromMilliseconds(100) });
            if (enableUdp)
            {
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var udpFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                manager.Exited += code => exited.TrySetResult(code);
                manager.LogReceived += entry =>
                {
                    if (entry.Message.Contains("UDP", StringComparison.OrdinalIgnoreCase)
                        && entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase))
                        udpFailure.TrySetResult();
                };
                // A loaded host can report the bind failure after StartupGracePeriod.
                // Assert the actual native failure, not the timing of StartAsync's return.
                try { await manager.StartAsync(path); }
                catch (InvalidOperationException) { }
                Assert.NotEqual(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                await udpFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else { await manager.StartAsync(path); Assert.True(manager.IsRunning); }
        }
        finally { File.Delete(path); }
    }
    private static UdpClient ReserveUdpWithUsableTcpPort()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            using var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var udp = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                udp.Client.Bind(tcp.LocalEndpoint);
                return udp;
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressAlreadyInUse)
            {
                udp.Dispose();
            }
        }
        throw new InvalidOperationException("No common loopback TCP/UDP test port is available.");
    }
}
