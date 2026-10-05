using Android.Content;
using Android.Net;
using Android.Util;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using System.Diagnostics;

namespace DiTunnel.Platform.Android;

public sealed class AndroidServerProbe(Context context) : IServerBatchProbe
{
    private static readonly SemaphoreSlim XrayProbeGate = new(1, 1);
    private readonly Context context = context.ApplicationContext ?? context;

    public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
        => (await ProbeManyAsync([profile], cancellationToken, mode))[0];

    public async Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
    {
        var results = new ServerProbeResult?[profiles.Count];
        try
        {
            await XrayProbeGate.WaitAsync(cancellationToken);
            try
            {
                var configurations = new XrayProfileConfiguration?[profiles.Count];
                for (var index = 0; index < profiles.Count; index++)
                {
                    // AWG list probes use a separate helper with no system TUN.
                    // Only missing VPN permission defers an offline check.
                    if (AmneziaWgProfileConverter.IsAmneziaWg(profiles[index]))
                        results[index] = await ProbeAmneziaWgAsync(profiles[index], mode, cancellationToken);
                    else
                    {
                        try { configurations[index] = XrayProfileConverter.Convert(profiles[index]); }
                        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or FormatException)
                        { results[index] = new(null, ShortMessage(error.Message)); }
                    }
                }
                var tcpIndexes = Enumerable.Range(0, profiles.Count)
                    .Where(index => configurations[index] is { ServerTransport: not KillSwitchTransportProtocol.Udp }).ToArray();
                foreach (var chunk in tcpIndexes.Chunk(5))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(mode == ServerProbeMode.Fast ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(18));
                    if (mode == ServerProbeMode.Fast)
                    {
                        var measured = await Task.WhenAll(chunk.Select(index => Task.Run(() =>
                        {
                            var milliseconds = MeasureTcp(configurations[index]!.ServerHost, configurations[index]!.ServerPort, timeout.Token);
                            return new ServerProbeResult(milliseconds, $"TCP · {milliseconds:F0} мс");
                        }, timeout.Token)));
                        for (var offset = 0; offset < chunk.Length; offset++) results[chunk[offset]] = measured[offset];
                    }
                    else
                    {
                        var measured = await AndroidProbeServiceBridge.ProbeAsync(context,
                            chunk.Select(index => profiles[index]).ToArray(), mode, timeout.Token);
                        for (var offset = 0; offset < chunk.Length; offset++) results[chunk[offset]] = measured[offset];
                    }
                }
                foreach (var index in Enumerable.Range(0, profiles.Count).Where(index => configurations[index]?.ServerTransport == KillSwitchTransportProtocol.Udp))
                {
                    results[index] = await ProbeUdpProfileAsync(profiles[index], mode, cancellationToken);
                }
            }
            finally { XrayProbeGate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or FormatException)
        {
            FillMissing(results, ShortMessage(error.Message));
        }
        catch (Exception error)
        {
            Log.Warn("DiTunnelProbe", error.ToString());
        }
        FillMissing(results, "Таймаут");
        return results.Select(result => result!).ToArray();
    }

    private static string ShortMessage(string message) => message.Length <= 42 ? message : "Недоступен";
    private async Task<ServerProbeResult> ProbeAmneziaWgAsync(ImportedProfile profile, ServerProbeMode mode, CancellationToken cancellationToken)
    {
        if (AndroidVpnRuntimeState.ReadActiveProfile(context)?.Content != profile.Content)
        {
            using var permission = global::Android.Net.VpnService.Prepare(context);
            if (permission is not null) return new(null, "AmneziaWG: сначала предоставьте разрешение на VPN", IsDeferred: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(mode == ServerProbeMode.Fast ? 25 : 35));
            try
            {
                var configuration = AmneziaWgProfileConverter.Convert(profile);
                var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
                var network = manager is null ? null : AndroidXrayProbeRunner.FindPhysicalNetwork(manager);
                if (network is null) return new(null, "Нет доступной физической сети.");
                var addresses = await Task.Run(() => network.GetAllByName(configuration.ServerHost), timeout.Token)
                    .WaitAsync(TimeSpan.FromSeconds(8), timeout.Token);
                var address = addresses?.OrderBy(item => item.HostAddress?.Contains(':') == true ? 1 : 0).FirstOrDefault()?.HostAddress;
                if (address is null) return new(null, "Не удалось определить IP-адрес VPN-сервера.");
                // A dedicated probe process cannot stop or replace the active AWG helper.
                // It owns no TUN: only this test traffic goes through its SOCKS/netstack.
                using var bridge = new AndroidAwgBridgeClient(context, configuration, address, _ => { }, probe: true);
                await bridge.StartAsync(timeout.Token);
                var milliseconds = mode == ServerProbeMode.Fast ? bridge.HandshakeMilliseconds : await bridge.MeasureHttpsAsync(timeout.Token);
                return new(milliseconds, $"{(mode == ServerProbeMode.Fast ? "Handshake" : "HTTPS")} · {milliseconds:F0} мс");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return new(null, "Таймаут"); }
            catch (TimeoutException) { return new(null, "Таймаут"); }
            catch (Exception error) when (error is InvalidOperationException or FormatException or NotSupportedException) { return new(null, ShortMessage(error.Message)); }
            catch { return new(null, "Сервер недоступен"); }
        }
        return await Task.Run(() =>
        {
            var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
#pragma warning disable CA1422
            var network = manager?.GetAllNetworks().FirstOrDefault(candidate =>
                manager.GetNetworkCapabilities(candidate)?.HasTransport(TransportType.Vpn) == true);
#pragma warning restore CA1422
            if (network is null) return new ServerProbeResult(null, "Туннель VPN недоступен");
            using var url = new Java.Net.URL("https://www.gstatic.com/generate_204");
            using var connection = network.OpenConnection(url) as Javax.Net.Ssl.HttpsURLConnection;
            if (connection is null) return new ServerProbeResult(null, "Контрольный сервер не ответил.");
            using var registration = cancellationToken.Register(connection.Disconnect);
            connection.ConnectTimeout = 8000;
            connection.ReadTimeout = 8000;
            connection.UseCaches = false;
            connection.InstanceFollowRedirects = false;
            try
            {
                var watch = Stopwatch.StartNew();
                if ((int)connection.ResponseCode != 204) return new ServerProbeResult(null, "Контрольный сервер не ответил.");
                var elapsed = watch.Elapsed.TotalMilliseconds;
                return new ServerProbeResult(elapsed, $"HTTPS · {elapsed:F0} мс");
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { return new ServerProbeResult(null, "Сервер недоступен"); }
            finally { connection.Disconnect(); }
        }, cancellationToken);
    }
    private static void FillMissing(ServerProbeResult?[] results, string message)
    {
        for (var index = 0; index < results.Length; index++) results[index] ??= new(null, message);
    }

    private async Task<ServerProbeResult> ProbeUdpProfileAsync(ImportedProfile profile, ServerProbeMode mode, CancellationToken cancellationToken)
    {
        ServerProbeResult? lastResult = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(mode == ServerProbeMode.Fast ? TimeSpan.FromSeconds(14) : TimeSpan.FromSeconds(20));
            try
            {
                lastResult = (await AndroidProbeServiceBridge.ProbeAsync(context, [profile], mode, timeout.Token, legacy: true))[0];
                if (lastResult.Milliseconds is not null) return lastResult;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { lastResult = new(null, "Таймаут"); }
            catch (TimeoutException) { lastResult = new(null, "Таймаут"); }

            if (attempt == 0) await Task.Delay(250, cancellationToken);
        }
        return lastResult ?? new(null, "Недоступен");
    }

    private double MeasureTcp(string host, int port, CancellationToken cancellationToken)
    {
        var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager
            ?? throw new InvalidOperationException("Сетевой сервис Android недоступен.");
        var network = AndroidXrayProbeRunner.FindPhysicalNetwork(manager)
            ?? throw new InvalidOperationException("Нет доступной физической сети.");
        using var socket = network.SocketFactory?.CreateSocket()
            ?? throw new InvalidOperationException("Android не создал сетевой сокет.");
        using var registration = cancellationToken.Register(socket.Close);
        var address = System.Net.IPAddress.TryParse(host, out var parsed)
            ? Java.Net.InetAddress.GetByAddress(parsed.GetAddressBytes())
            : (network.GetAllByName(host) ?? []).FirstOrDefault();
        if (address is null) throw new InvalidOperationException("Не удалось определить адрес VPN-сервера.");
        var watch = Stopwatch.StartNew();
        socket.Connect(new Java.Net.InetSocketAddress(address, port), 8000);
        return watch.Elapsed.TotalMilliseconds;
    }
}
