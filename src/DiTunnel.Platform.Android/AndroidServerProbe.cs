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
                var configurations = profiles.Select(XrayProfileConverter.Convert).ToArray();
                var tcpIndexes = Enumerable.Range(0, profiles.Count)
                    .Where(index => configurations[index].ServerTransport != KillSwitchTransportProtocol.Udp).ToArray();
                foreach (var chunk in tcpIndexes.Chunk(5))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(mode == ServerProbeMode.Fast ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(18));
                    if (mode == ServerProbeMode.Fast)
                    {
                        var measured = await Task.WhenAll(chunk.Select(index => Task.Run(() =>
                        {
                            var milliseconds = MeasureTcp(configurations[index].ServerHost, configurations[index].ServerPort, timeout.Token);
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
                foreach (var index in Enumerable.Range(0, profiles.Count).Except(tcpIndexes))
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
