using System.Net;
using System.Net.Sockets;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class SocksListenerReadinessTests
{
    [Fact]
    public async Task Delayed_listener_requires_a_socks_reply_before_ready()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ready = SocksTcpProbe.WaitForListenerAsync(port, () => true, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        var request = new byte[3];
        await stream.ReadExactlyAsync(request, timeout.Token);
        Assert.Equal(new byte[] { 5, 1, 0 }, request);
        Assert.False(ready.IsCompleted);
        await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
        await ready;
    }

    [Fact]
    public async Task Stopped_runtime_does_not_wait_for_a_listener()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => SocksTcpProbe.WaitForListenerAsync(1, () => false, default));

    [Fact]
    public async Task Silent_listener_is_bounded_by_cancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource();
        var ready = SocksTcpProbe.WaitForListenerAsync(port, () => true, cancellation.Token);
        using var peer = await listener.AcceptTcpClientAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
    }
}
