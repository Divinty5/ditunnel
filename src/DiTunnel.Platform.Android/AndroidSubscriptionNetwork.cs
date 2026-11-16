using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Android.Content;
using Android.Net;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Android;

public static class AndroidSubscriptionNetwork
{
    public static HttpClient CreateClient(Context context) => DirectSubscriptionHttp.Create(async (endpoint, token) =>
    {
        if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager ||
            AndroidXrayProbeRunner.FindPhysicalNetwork(manager) is not { } network)
            throw new SocketException((int)SocketError.NetworkUnreachable);
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? [literal]
            : (await Task.Run(() => network.GetAllByName(endpoint.Host), token).WaitAsync(token).ConfigureAwait(false) ?? [])
                .Select(address => IPAddress.TryParse(address.HostAddress, out var ip) ? ip : null).OfType<IPAddress>().ToArray();
        return await DirectSubscriptionHttp.ConnectAsync(addresses, endpoint.Port, socket =>
        {
            if (android_setsocknetwork(unchecked((ulong)network.NetworkHandle), socket.Handle.ToInt32()) != 0)
                throw new SocketException(Marshal.GetLastPInvokeError());
        }, token).ConfigureAwait(false);
    });

    [DllImport("libandroid.so", ExactSpelling = true, SetLastError = true)]
    private static extern int android_setsocknetwork(ulong network, int fd);
}
