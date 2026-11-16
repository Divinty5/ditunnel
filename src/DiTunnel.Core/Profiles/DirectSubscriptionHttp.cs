using System.Net;
using System.Net.Sockets;

namespace DiTunnel.Core.Profiles;

public static class DirectSubscriptionHttp
{
    public static HttpClient Create(Func<DnsEndPoint, CancellationToken, ValueTask<Stream>> connect) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, token) =>
            {
                var endpoint = context.DnsEndPoint;
                if (IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address))
                    return ConnectAsync([address], endpoint.Port, static _ => { }, token);
                if (endpoint.Host.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    return ConnectAsync([IPAddress.Loopback, IPAddress.IPv6Loopback], endpoint.Port, static _ => { }, token);
                return connect(endpoint, token);
            }
        });

    public static async ValueTask<Stream> ConnectAsync(IEnumerable<IPAddress> addresses, int port, Action<Socket> bind, CancellationToken token)
    {
        SocketException? failure = null;
        foreach (var address in addresses)
        {
            token.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(2));
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                bind(socket);
                await socket.ConnectAsync(new IPEndPoint(address, port), attempt.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error) { socket.Dispose(); failure = error; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { socket.Dispose(); failure = new SocketException((int)SocketError.TimedOut); }
            catch { socket.Dispose(); throw; }
        }
        throw failure ?? new SocketException((int)SocketError.HostNotFound);
    }
}
