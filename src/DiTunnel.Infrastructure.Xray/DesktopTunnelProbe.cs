using System.Net;
using System.Net.Sockets;
using DiTunnel.Core.Connection;

namespace DiTunnel.Infrastructure.Xray;

public static class DesktopTunnelProbe
{
    private static readonly Uri Cloudflare = new("https://one.one.one.one/cdn-cgi/trace");
    private static readonly Uri Google = new("https://dns.google/resolve?name=example.com&type=A");

    private sealed record Target(Uri Url, IPAddress Ipv4, IPAddress Ipv6);
    private static readonly Target[] Targets =
    [
        new(Cloudflare, IPAddress.Parse("1.1.1.1"), IPAddress.Parse("2606:4700:4700::1111")),
        new(Google, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2001:4860:4860::8888"))
    ];

    public static async Task<ServerProbeResult> CheckAsync(string platform, SplitTunnelPolicy split, bool ipv4, bool ipv6,
        IReadOnlyList<string> forcedProxyAddresses, Func<IPAddress, bool> usesTunnel, Action<Socket> bind,
        CancellationToken token)
    {
        var addresses = new Dictionary<Uri, IPAddress>();
        foreach (var target in Targets)
        {
            token.ThrowIfCancellationRequested();
            if (ipv4 && IsProxied(split, target.Url, target.Ipv4, forcedProxyAddresses) && usesTunnel(target.Ipv4))
                addresses.Add(target.Url, target.Ipv4);
            else if (ipv6 && IsProxied(split, target.Url, target.Ipv6, forcedProxyAddresses) && usesTunnel(target.Ipv6))
                addresses.Add(target.Url, target.Ipv6);
        }
        if (addresses.Count == 0) return ProbePolicy.ActiveTunnelDeferred(platform);

        async Task RequestAsync(Uri target, CancellationToken cancellation)
        {
            var address = addresses[target];
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = async (_, connectToken) =>
                {
                    using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation, connectToken);
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        if (!usesTunnel(address)) throw new ProbeFailureException("TUN", "Unconfirmed");
                        try { bind(socket); }
                        catch (SocketException) { throw new ProbeFailureException("TUN", "Unconfirmed"); }
                        await socket.ConnectAsync(address, target.Port, connect.Token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            ProbePolicy.ValidateResponse(target, (int)response.StatusCode);
        }

        // A single routable site can confirm connectivity, but cannot establish
        // a transport failure on its own.
        var result = await ProbePolicy.CheckHttpsAsync(RequestAsync, token, ProbePolicy.ActiveHttpsAttemptBudget, platform, addresses.Keys.ToArray())
            .ConfigureAwait(false);
        return result.Milliseconds is null && (addresses.Count == 1 || result.Diagnostics?.ErrorType == "Unconfirmed")
            ? result with { IsDeferred = true } : result;
    }

    private static bool IsProxied(SplitTunnelPolicy split, Uri target, IPAddress address,
        IReadOnlyList<string> forcedProxyAddresses)
    {
        if (split.Mode == SplitTunnelMode.ProxyAll) return true;
        if (forcedProxyAddresses.Any(value => IPAddress.TryParse(value, out var ip) && ip.Equals(address))) return true;
        var destinations = split.Domains.Select(SplitTunnelPolicy.NormalizeDomain);
        if (split.Mode == SplitTunnelMode.ProxySelected)
            // An IP rule works even if the ClientHello could not be sniffed.
            return destinations.Any(value => IPAddress.TryParse(value, out var ip) && ip.Equals(address));
        return !MatchesHostProcess(split.Processes) && !destinations.Any(value => value.Length > 0 &&
            (IPAddress.TryParse(value, out var ip) ? ip.Equals(address) :
                target.Host.Equals(value, StringComparison.OrdinalIgnoreCase) ||
                target.Host.EndsWith("." + value, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool MatchesHostProcess(IReadOnlyList<string> processes)
    {
        var path = Environment.ProcessPath?.Replace('\\', '/');
        if (path is null) return processes.Count > 0;
        // Conservatively match the executable, its basename, or a configured folder.
        var name = Path.GetFileName(path);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return processes.Select(value => value.Trim().Replace('\\', '/')).Any(value => value.EndsWith('/')
            ? path.StartsWith(value, StringComparison.OrdinalIgnoreCase)
            : value.Contains('/') ? path.Equals(value, StringComparison.OrdinalIgnoreCase)
            : name.Equals(value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value,
                StringComparison.OrdinalIgnoreCase));
    }
}
