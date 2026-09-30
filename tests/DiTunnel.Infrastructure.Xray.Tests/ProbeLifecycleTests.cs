using System.Net;
using System.Net.Sockets;
using DiTunnel.Platform.Windows;

namespace DiTunnel.Infrastructure.Xray.Tests;

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
        // Bind UDP first, then find a free TCP port with that same number. No outbound traffic.
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start(); tcp.Stop();
        var path = Path.Combine(Path.GetTempPath(), "xray-loopback-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var configuration = XrayProfileConverter.Convert(new("Synthetic", "VLESS", "vless://11111111-1111-4111-8111-111111111111@192.0.2.1:443?security=none"));
            await File.WriteAllTextAsync(path, configuration.Build("192.0.2.1", false, port, enableSocksUdp: enableUdp));
            await using var manager = new XrayProcessManager(new XrayOptions
            { ExecutablePath = runtime, WorkingDirectory = Path.GetDirectoryName(runtime)!, ValidateConfigurationBeforeStart = false, ShutdownTimeout = TimeSpan.FromMilliseconds(100) });
            if (enableUdp) await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(path));
            else { await manager.StartAsync(path); Assert.True(manager.IsRunning); }
        }
        finally { File.Delete(path); }
    }
}
