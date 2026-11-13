using Android.Content;
using Android.Net;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using System.Diagnostics;

namespace DiTunnel.Platform.Android;

public sealed class AndroidServerProbe(Context context) : IServerBatchProbe, IActiveTunnelProbe
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
                        results[index] = await AndroidProbeFailureIsolation.RunAsync(
                            () => ProbeAmneziaWgAsync(profiles[index], mode, cancellationToken),
                            error => ProbeFailure(error, "AmneziaWG"), cancellationToken);
                    else
                    {
                        try { configurations[index] = XrayProfileConverter.Convert(profiles[index]); }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested)
                        { results[index] = ProbeFailure(new AndroidProbeException("Профиль", "Configuration"), "Профиль"); }
                    }
                }
                var tcpIndexes = Enumerable.Range(0, profiles.Count)
                    .Where(index => configurations[index] is not null &&
                        (mode == ServerProbeMode.Https || configurations[index]!.ServerTransport != KillSwitchTransportProtocol.Udp)).ToArray();
                foreach (var chunk in tcpIndexes.Chunk(5))
                {
                    var chunkWatch = Stopwatch.StartNew();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(mode == ServerProbeMode.Fast ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(25));
                    if (mode == ServerProbeMode.Fast)
                    {
                        var measured = await Task.WhenAll(chunk.Select(index => MeasureTcpProfileAsync(
                            configurations[index]!, timeout.Token, cancellationToken)));
                        for (var offset = 0; offset < chunk.Length; offset++)
                            results[chunk[offset]] = offset < measured.Length ? measured[offset] : new(null, "Проверка не выполнена");
                    }
                    else
                    {
                        var measured = await AndroidProbeFailureIsolation.RunAsync(
                            () => AndroidProbeServiceBridge.ProbeAsync(context,
                                chunk.Select(index => profiles[index]).ToArray(), mode, timeout.Token),
                            error => (IReadOnlyList<ServerProbeResult>)Enumerable.Repeat(
                                ProbeFailure(error, "HTTPS", timeout.IsCancellationRequested, duration: chunkWatch.Elapsed.TotalMilliseconds), chunk.Length).ToArray(), cancellationToken);
                        for (var offset = 0; offset < chunk.Length; offset++)
                            results[chunk[offset]] = offset < measured.Count ? measured[offset] : new(null, "Проверка не выполнена");
                    }
                }
                foreach (var index in Enumerable.Range(0, profiles.Count).Where(index => mode == ServerProbeMode.Fast && configurations[index]?.ServerTransport == KillSwitchTransportProtocol.Udp))
                {
                    results[index] = await AndroidProbeFailureIsolation.RunAsync(
                        () => ProbeUdpProfileAsync(profiles[index], mode, cancellationToken),
                        error => ProbeFailure(error, "Профиль"), cancellationToken);
                }
            }
            finally { XrayProbeGate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { FillMissing(results, "Таймаут проверки"); }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { FillMissing(results, "Таймаут проверки"); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or FormatException)
        {
            FillMissing(results, ProbeFailure(error, "Профиль"));
        }
        catch (Exception error)
        {
            FillMissing(results, ProbeFailure(error, "Проверка"));
        }
        FillMissing(results, "Проверка не выполнена");
        return results.Select(result => result!).ToArray();
    }

    private static ServerProbeResult ProbeFailure(Exception error, string stage, bool timedOut = false, string method = "HTTPS", double duration = 0)
        => AndroidProbePolicy.Failure(timedOut ? new TimeoutException() : error, stage, method, duration);

    private async Task<ServerProbeResult> ProbeAmneziaWgAsync(ImportedProfile profile, ServerProbeMode mode, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var stage = "Профиль";
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
                if (network is null) throw new AndroidProbeException("Подключение к серверу", "Connection");
                stage = "DNS сервера";
                var addresses = await Task.Run(() => network.GetAllByName(configuration.ServerHost), timeout.Token)
                    .WaitAsync(TimeSpan.FromSeconds(8), timeout.Token);
                var address = addresses?.OrderBy(item => item.HostAddress?.Contains(':') == true ? 1 : 0).FirstOrDefault()?.HostAddress;
                if (address is null) throw new AndroidProbeException(stage, "Dns");
                stage = "AmneziaWG";
                // A dedicated probe process cannot stop or replace the active AWG helper.
                // It owns no TUN: only this test traffic goes through its SOCKS/netstack.
                using var bridge = new AndroidAwgBridgeClient(context, configuration, address, _ => { }, probe: true);
                await bridge.StartAsync(timeout.Token);
                // An authenticated handshake alone does not verify HTTPS/data forwarding.
                stage = "HTTPS";
                var result = await bridge.ProbeHttpsAsync(timeout.Token);
                return result with { Diagnostics = result.Diagnostics! with { DurationMilliseconds = watch.Elapsed.TotalMilliseconds } };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { return AndroidProbePolicy.Failure(error, stage, "HTTPS", watch.Elapsed.TotalMilliseconds); }
        }
        return mode == ServerProbeMode.Https
            ? await AndroidProbePolicy.RetryTimeoutAsync(() => ProbeActiveTunnelAsync(cancellationToken))
            : await ProbeActiveTunnelAsync(cancellationToken);
    }

    // Bind explicitly to the VPN Network; widget latency must never use a physical network fallback.
    public async Task<ServerProbeResult> ProbeActiveTunnelAsync(CancellationToken cancellationToken = default)
    {
        Network? network = null;
        try
        {
            var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            // The STARTED broadcast may precede the system's VPN Network registration.
            for (var attempt = 0; attempt < 6 && network is null; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
#pragma warning disable CA1422
                network = manager?.GetAllNetworks().FirstOrDefault(candidate =>
                    manager.GetNetworkCapabilities(candidate)?.HasTransport(TransportType.Vpn) == true);
#pragma warning restore CA1422
                if (network is null && attempt < 5) await Task.Delay(200, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return ProbeFailure(error, "TUN"); }
        if (network is null) return ProbeFailure(new AndroidProbeException("TUN", "Connection"), "TUN");
        return await AndroidProbePolicy.CheckHttpsAsync((target, token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var url = new Java.Net.URL(target.AbsoluteUri);
            using var connection = network.OpenConnection(url) as Javax.Net.Ssl.HttpsURLConnection
                ?? throw new AndroidProbeException("HTTPS", "ControlService");
            using var registration = token.Register(connection.Disconnect);
            connection.ConnectTimeout = 2500;
            connection.ReadTimeout = 2500;
            connection.UseCaches = false;
            connection.InstanceFollowRedirects = false;
            try
            {
                AndroidProbePolicy.ValidateResponse(target, (int)connection.ResponseCode);
                token.ThrowIfCancellationRequested();
            }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            finally { connection.Disconnect(); }
        }, token), cancellationToken);
    }

    private static void FillMissing(ServerProbeResult?[] results, string message) =>
        FillMissing(results, AndroidProbePolicy.Failure(new AndroidProbeException("Проверка",
            message.Contains("Таймаут", StringComparison.Ordinal) ? "Timeout" : "Internal"), "Проверка", "HTTPS", 0));

    private static void FillMissing(ServerProbeResult?[] results, ServerProbeResult failure)
    {
        for (var index = 0; index < results.Length; index++) results[index] ??= failure;
    }

    private async Task<ServerProbeResult> ProbeUdpProfileAsync(ImportedProfile profile, ServerProbeMode mode, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return (await AndroidProbeServiceBridge.ProbeAsync(context, [profile], mode, timeout.Token))[0];
    }

    private async Task<ServerProbeResult> MeasureTcpProfileAsync(XrayProfileConfiguration configuration,
        CancellationToken timeout, CancellationToken caller)
    {
        var watch = Stopwatch.StartNew();
        return await AndroidProbeFailureIsolation.RunAsync(() => Task.Run(() =>
        {
            var milliseconds = MeasureTcp(configuration.ServerHost, configuration.ServerPort, timeout);
            return AndroidProbePolicy.Success(milliseconds, "TCP", "Подключение к серверу", false);
        }, timeout), error => AndroidProbePolicy.Failure(timeout.IsCancellationRequested ? new TimeoutException() : error,
            "Подключение к серверу", "TCP", watch.Elapsed.TotalMilliseconds), caller);
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
        var watch = Stopwatch.StartNew();
        Java.Net.InetAddress? address;
        try
        {
            address = System.Net.IPAddress.TryParse(host, out var parsed)
                ? Java.Net.InetAddress.GetByAddress(parsed.GetAddressBytes())
                : (network.GetAllByName(host) ?? []).FirstOrDefault();
        }
        catch (Exception error) { throw new AndroidProbeException("DNS сервера", AndroidProbePolicy.Classify(error)); }
        if (address is null) throw new AndroidProbeException("DNS сервера", "Dns");
        socket.Connect(new Java.Net.InetSocketAddress(address, port), 8000);
        return watch.Elapsed.TotalMilliseconds;
    }
}
