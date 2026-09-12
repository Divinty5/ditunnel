using Android.Content;
using Android.Net;
using Android.OS;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiTunnel.Platform.Android;

internal static class AndroidXrayProbeRunner
{
    public static async Task<ServerProbeResult> MeasureAsync(Context context, ImportedProfile profile, ServerProbeMode mode, CancellationToken cancellationToken)
        => (await MeasureBatchAsync(context, [profile], mode, cancellationToken))[0];

    public static async Task<IReadOnlyList<ServerProbeResult>> MeasureBatchAsync(Context context, IReadOnlyList<ImportedProfile> profiles, ServerProbeMode mode, CancellationToken cancellationToken)
    {
        if (profiles.Count is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(profiles));
        var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager
            ?? throw new InvalidOperationException();
        var network = FindPhysicalNetwork(manager) ?? throw new InvalidOperationException();
        // The probe runs in its own process, so bind the whole process to the physical
        // network. QUIC opens UDP sockets in a phase where Network.BindSocket(fd) may be
        // rejected; that made Hysteria2 fail while TCP-based profiles kept working.
        if (!manager.BindProcessToNetwork(network)) throw new InvalidOperationException();
        var controller = new UnchangedSocketController();
        global::LibXray.LibXray.RegisterDialerController(controller);
        global::LibXray.LibXray.RegisterListenerController(controller);
        global::LibXray.LibXray.SetDNS(controller, "1.1.1.1:53");
        var directory = context.CacheDir?.AbsolutePath ?? throw new InvalidOperationException();
        var paths = new List<string>(profiles.Count);
        try
        {
            foreach (var profile in profiles)
            {
                var converted = XrayProfileConverter.Convert(profile);
                var serverAddress = (network.GetAllByName(converted.ServerHost) ?? []).FirstOrDefault()
                    ?? throw new InvalidOperationException();
                var path = Path.Combine(directory, $"probe-{Guid.NewGuid():N}.json");
                paths.Add(path);
                await File.WriteAllTextAsync(path, converted.Build(serverAddress.HostAddress ?? converted.ServerHost, false, 10808), cancellationToken);
            }
            // Native pingBatch creates one temporary Xray instance and tests up to five real
            // outbounds concurrently, matching the URL-test model used by Hiddify/sing-box.
            var protocol = mode == ServerProbeMode.Fast ? "HTTP" : "HTTPS";
            var configs = new JsonArray(paths.Select(path => (JsonNode)new JsonObject { ["configPath"] = path }).ToArray());
            using var response = Invoke("pingBatch", new JsonObject
            {
                ["configs"] = configs,
                ["timeout"] = mode == ServerProbeMode.Fast ? 8 : 12,
                ["url"] = mode == ServerProbeMode.Fast ? "http://cp.cloudflare.com/" : "https://cp.cloudflare.com/"
            });
            if (!response.Success || response.Data is null ||
                !response.Data.RootElement.TryGetProperty("results", out var resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(response.Error ?? "Сервер недоступен");
            var results = new List<ServerProbeResult>(profiles.Count);
            foreach (var item in resultsElement.EnumerateArray())
            {
                var success = item.TryGetProperty("success", out var successElement) && successElement.GetBoolean();
                var delay = item.TryGetProperty("delay", out var delayElement) ? delayElement.GetInt64() : 10000;
                results.Add(success && delay is not (10000 or 11000)
                    ? new(delay, $"{protocol} · {delay} мс")
                    : new(null, delay == 11000 ? "Таймаут" : "Недоступен"));
            }
            while (results.Count < profiles.Count) results.Add(new(null, "Недоступен"));
            return results;
        }
        finally
        {
            try { global::LibXray.LibXray.ResetDNS(); } catch { }
            try { _ = manager.BindProcessToNetwork(null); } catch { }
            controller.Dispose();
            foreach (var path in paths) try { File.Delete(path); } catch { }
        }
    }

    public static async Task<ServerProbeResult> MeasureViaProxyAsync(Context context, ImportedProfile profile, ServerProbeMode mode, CancellationToken cancellationToken)
    {
        var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager
            ?? throw new InvalidOperationException();
        var network = FindPhysicalNetwork(manager) ?? throw new InvalidOperationException();
        var converted = XrayProfileConverter.Convert(profile);
        var serverAddress = (network.GetAllByName(converted.ServerHost) ?? []).FirstOrDefault()
            ?? throw new InvalidOperationException();
        if (!manager.BindProcessToNetwork(network)) throw new InvalidOperationException();
        var controller = new UnchangedSocketController();
        using var listenerController = new UnchangedSocketController();
        var proxyPort = ReserveLoopbackPort();
        var running = false;
        try
        {
            global::LibXray.LibXray.RegisterDialerController(controller);
            global::LibXray.LibXray.RegisterListenerController(listenerController);
            global::LibXray.LibXray.SetDNS(controller, "1.1.1.1:53");
            using var started = Invoke("runXrayFromJson", new JsonObject
            {
                ["configJSON"] = converted.Build(serverAddress.HostAddress ?? converted.ServerHost, false, proxyPort)
            });
            if (!started.Success) throw new InvalidOperationException(started.Error ?? "Сервер недоступен");
            running = true;

            // runXrayFromJson returns before the managed SOCKS listener is necessarily
            // accepting connections. A first request made in that window fails with
            // ConnectionRefused and used to mark UDP/QUIC profiles as unavailable.
            await WaitForListenerAsync(proxyPort, cancellationToken);

            var watch = Stopwatch.StartNew();
            await MeasureThroughSocksAsync(proxyPort, mode == ServerProbeMode.Https, cancellationToken);
            watch.Stop();
            var milliseconds = watch.Elapsed.TotalMilliseconds;
            var protocol = mode == ServerProbeMode.Fast ? "HTTP" : "HTTPS";
            return new(milliseconds, $"{protocol} · {milliseconds:F0} мс");
        }
        finally
        {
            if (running)
            {
                try { using var _ = Invoke("stopXray", new JsonObject()); } catch { }
            }
            try { global::LibXray.LibXray.ResetDNS(); } catch { }
            try { _ = manager.BindProcessToNetwork(null); } catch { }
            controller.Dispose();
        }
    }

    private static async Task MeasureThroughSocksAsync(int proxyPort, bool useTls, CancellationToken cancellationToken)
    {
        const string host = "cp.cloudflare.com";
        using var socket = new TcpClient(AddressFamily.InterNetwork);
        await socket.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken);
        await using var network = socket.GetStream();
        await network.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken);
        var greeting = new byte[2];
        await network.ReadExactlyAsync(greeting, cancellationToken);
        if (greeting[0] != 5 || greeting[1] != 0) throw new IOException("SOCKS-сервер отклонил подключение.");

        var hostBytes = System.Text.Encoding.ASCII.GetBytes(host);
        var port = useTls ? 443 : 80;
        var connect = new byte[7 + hostBytes.Length];
        connect[0] = 5; connect[1] = 1; connect[2] = 0; connect[3] = 3; connect[4] = checked((byte)hostBytes.Length);
        hostBytes.CopyTo(connect, 5);
        connect[^2] = (byte)(port >> 8); connect[^1] = (byte)port;
        await network.WriteAsync(connect, cancellationToken);
        var reply = new byte[4];
        await network.ReadExactlyAsync(reply, cancellationToken);
        if (reply[0] != 5 || reply[1] != 0) throw new IOException($"SOCKS-соединение не установлено ({reply[1]}).");
        var addressLength = reply[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadDomainLengthAsync(network, cancellationToken),
            _ => throw new IOException("Некорректный ответ SOCKS-сервера.")
        };
        var remainder = new byte[addressLength + 2];
        await network.ReadExactlyAsync(remainder, cancellationToken);

        Stream requestStream = network;
        SslStream? tls = null;
        if (useTls)
        {
            tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken);
            requestStream = tls;
        }
        await using (tls)
        {
            var request = System.Text.Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
            await requestStream.WriteAsync(request, cancellationToken);
            var firstByte = new byte[1];
            if (await requestStream.ReadAsync(firstByte, cancellationToken) == 0)
                throw new IOException("Контрольный сервер не ответил.");
        }
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, cancellationToken);
        return length[0];
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForListenerAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            try
            {
                using var client = new TcpClient(AddressFamily.InterNetwork);
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return;
            }
            catch (SocketException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(40, timeout.Token);
            }
        }
    }

    public static Network? FindPhysicalNetwork(ConnectivityManager manager)
    {
#pragma warning disable CA1422
        return manager.GetAllNetworks().FirstOrDefault(candidate =>
        {
            var capabilities = manager.GetNetworkCapabilities(candidate);
            return capabilities?.HasCapability(NetCapability.NotVpn) == true
                && capabilities.HasCapability(NetCapability.Internet)
                && capabilities.HasCapability(NetCapability.Validated);
        });
#pragma warning restore CA1422
    }

    private static InvokeResponse Invoke(string method, JsonObject payload)
    {
        var request = new JsonObject { ["apiVersion"] = 1, ["method"] = method, ["payload"] = payload }.ToJsonString();
        var responseJson = global::LibXray.LibXray.Invoke(request) ?? throw new InvalidOperationException();
        using var response = JsonDocument.Parse(responseJson);
        var root = response.RootElement;
        JsonDocument? data = null;
        if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
            data = JsonDocument.Parse(dataElement.GetRawText());
        var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
            ? errorElement.GetString()
            : null;
        return new(root.TryGetProperty("success", out var success) && success.GetBoolean(), data, error);
    }

    private sealed class PhysicalNetworkDialerController(Network network) : Java.Lang.Object, global::LibXray.IDialerController
    {
        public bool ProtectFd(long fileDescriptor)
        {
            if (fileDescriptor is < 0 or > int.MaxValue) return false;
            ParcelFileDescriptor? descriptor = null;
            try
            {
                descriptor = ParcelFileDescriptor.AdoptFd((int)fileDescriptor) ?? throw new InvalidOperationException();
                if (descriptor.FileDescriptor is not { } rawDescriptor) { _ = descriptor.DetachFd(); return false; }
                network.BindSocket(rawDescriptor);
                _ = descriptor.DetachFd();
                return true;
            }
            catch { return false; }
            finally { descriptor?.Dispose(); }
        }
    }

    private sealed class UnchangedSocketController : Java.Lang.Object, global::LibXray.IDialerController
    {
        public bool ProtectFd(long fileDescriptor) => fileDescriptor >= 0;
    }

    private sealed record InvokeResponse(bool Success, JsonDocument? Data, string? Error) : IDisposable
    {
        public void Dispose() => Data?.Dispose();
    }
}
