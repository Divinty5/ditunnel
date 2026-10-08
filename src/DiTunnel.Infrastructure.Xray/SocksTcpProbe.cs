using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;

namespace DiTunnel.Infrastructure.Xray;

public static class SocksTcpProbe
{
    public static async Task WaitForListenerAsync(int proxyPort, Func<bool> isRunning, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isRunning()) throw new InvalidOperationException("Xray завершился до готовности SOCKS-прокси.");
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
            try
            {
                using var client = new TcpClient(AddressFamily.InterNetwork);
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, attempt.Token).ConfigureAwait(false);
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, attempt.Token).ConfigureAwait(false);
                var reply = new byte[2];
                await stream.ReadExactlyAsync(reply, attempt.Token).ConfigureAwait(false);
                if (reply[0] == 5 && reply[1] == 0) return;
                throw new IOException("SOCKS listener negotiation failed.");
            }
            catch (Exception error) when (error is SocketException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested) { }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    // Xray can acknowledge CONNECT before opening the remote outbound. Wait for
    // authenticated remote TLS traffic so the result includes the tunnel path.
    public static Task<double> MeasureAsync(int proxyPort, IPAddress destination, ushort destinationPort, CancellationToken cancellationToken)
        => MeasureAsync(proxyPort, destination, destinationPort, cancellationToken, new SslClientAuthenticationOptions { TargetHost = destination.ToString() });

    internal static async Task<double> MeasureAsync(int proxyPort, IPAddress destination, ushort destinationPort, CancellationToken cancellationToken, SslClientAuthenticationOptions tlsOptions)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        var stream = client.GetStream();
        var watch = Stopwatch.StartNew();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
        var negotiation = new byte[2];
        await stream.ReadExactlyAsync(negotiation, cancellationToken);
        if (negotiation[0] != 5 || negotiation[1] != 0) throw new IOException("SOCKS negotiation failed.");
        var address = destination.GetAddressBytes();
        var request = new byte[6 + address.Length];
        request[0] = 5; request[1] = 1; request[3] = destination.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        address.CopyTo(request, 4);
        request[^2] = (byte)(destinationPort >> 8); request[^1] = (byte)destinationPort;
        await stream.WriteAsync(request, cancellationToken);
        var reply = new byte[4];
        await stream.ReadExactlyAsync(reply, cancellationToken);
        if (reply[0] != 5 || reply[1] != 0 || reply[2] != 0) throw new IOException("SOCKS CONNECT failed.");
        var length = reply[3] switch { 1 => 4, 4 => 16, 3 => await ReadLengthAsync(stream, cancellationToken), _ => throw new IOException("Invalid SOCKS reply.") };
        await stream.ReadExactlyAsync(new byte[length + 2], cancellationToken);
        using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsClientAsync(tlsOptions, cancellationToken);
        return watch.Elapsed.TotalMilliseconds;
    }

    private static async Task<int> ReadLengthAsync(Stream stream, CancellationToken token)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, token);
        if (length[0] == 0) throw new IOException("Invalid SOCKS address.");
        return length[0];
    }
}
