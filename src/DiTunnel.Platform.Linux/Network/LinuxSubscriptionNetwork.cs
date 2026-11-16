using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Platform.Linux.Network;

[SupportedOSPlatform("linux")]
public static partial class LinuxSubscriptionNetwork
{
    public static HttpClient CreateClient() => DirectSubscriptionHttp.Create(ConnectAsync);

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken token)
    {
        var isLiteral = IPAddress.TryParse(endpoint.Host, out var literal);
        var family = literal?.AddressFamily == AddressFamily.InterNetworkV6 ? "-6" : "-4";
        var device = await FindDeviceAsync(family, token).ConfigureAwait(false);
        if (device is null && !isLiteral) device = await FindDeviceAsync("-6", token).ConfigureAwait(false);
        if (device is null) throw new SocketException((int)SocketError.NetworkUnreachable);
        var addresses = isLiteral ? [literal!] : await ResolveAsync(endpoint.Host, device, token).ConfigureAwait(false);
        var deviceName = Encoding.UTF8.GetBytes(device + "\0");
        return await DirectSubscriptionHttp.ConnectAsync(addresses, endpoint.Port, socket =>
        {
            const int solSocket = 1, bindToDevice = 25;
            if (setsockopt(socket.Handle.ToInt32(), solSocket, bindToDevice, deviceName, (uint)deviceName.Length) != 0)
                throw new SocketException(Marshal.GetLastPInvokeError());
        }, token).ConfigureAwait(false);
    }

    private static async Task<string?> FindDeviceAsync(string family, CancellationToken token)
    {
        var routes = await LinuxNetworkCommands.RunAsync("/usr/sbin/ip", [family, "-j", "route", "show", "table", "main", "default"], token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(routes);
        return json.RootElement.EnumerateArray()
            .Where(route => route.TryGetProperty("dev", out var device) && device.GetString() is { } name && name != "lo")
            .OrderBy(route => route.TryGetProperty("metric", out var metric) ? metric.GetInt32() : 0)
            .Select(route => route.GetProperty("dev").GetString()).FirstOrDefault();
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, string device, CancellationToken token)
    {
        var output = await LinuxNetworkCommands.RunAsync("/usr/bin/resolvectl",
            ["--legend=no", "--cache=no", "--synthesize=no", "--protocol=dns", "--interface=" + device, "query", "--", host], token).ConfigureAwait(false);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 4 && fields[^3] == "--" && fields[^2] == "link:" && fields[^1] == device)
            .Select(fields => IPAddress.TryParse(fields[^4], out var address) ? address : null)
            .OfType<IPAddress>().ToArray();
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsockopt(int fd, int level, int option, byte[] value, uint length);
}
