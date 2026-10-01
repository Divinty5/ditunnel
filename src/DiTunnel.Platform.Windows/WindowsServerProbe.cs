using System.Net;
using System.Net.Sockets;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Platform.Windows;

public sealed class WindowsServerProbe : IServerProbe
{
    private readonly WindowsKillSwitchController? killSwitch;
    private readonly WindowsVpnEngine? engine;

    public WindowsServerProbe(WindowsKillSwitchController? killSwitch = null, WindowsVpnEngine? engine = null)
    { this.killSwitch = killSwitch; this.engine = engine; }

    public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
    {
        var probeBudget = TimeSpan.FromSeconds(mode == ServerProbeMode.Fast ? 8 : 12);
        var directory = WindowsRuntime.CreateSession();
        var path = Path.Combine(directory, "config.json");
        try
        {
            var awg = AmneziaWgProfileConverter.IsAmneziaWg(profile) ? AmneziaWgProfileConverter.Convert(profile) : null;
            await using var active = awg is null || engine is null ? null : await engine.AcquireActiveAmneziaProbeAsync(profile, cancellationToken);
            if (active is not null)
            {
                var precise = mode == ServerProbeMode.Https;
                var measured = await XrayServerProbe.MeasureAsync(active.Proxy, IPAddress.Loopback, WindowsRuntime.Find(), path,
                    cancellationToken, useHttps: precise, tcpOnly: !precise, probeTimeout: probeBudget);
                return new(measured, $"{(precise ? "HTTPS" : "TLS")} · {measured:F0} мс");
            }
            var configuration = awg?.CreateProxyConfiguration(0, "pending", "pending") ?? XrayProfileConverter.Convert(profile);
            var address = (await Dns.GetHostAddressesAsync(configuration.ServerHost, cancellationToken).WaitAsync(TimeSpan.FromSeconds(mode == ServerProbeMode.Fast ? 4 : 15), cancellationToken)).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new NotSupportedException("Нет IPv4-адреса сервера.");
            await using var bypass = await WindowsProbeRouteBypass.CreateAsync(address, directory, cancellationToken, serializeEndpoint: awg is not null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using var permit = killSwitch is null
                ? NoopAsyncDisposable.Instance
                : await killSwitch.PermitProbeEndpointAsync(address, configuration.ServerPort, configuration.ServerTransport, cancellationToken);
            timeout.CancelAfter(probeBudget);
            // On Windows a raw TCP connect is not a reliable profile check while another
            // Di-Tunnel route is active. Use the same temporary Xray outbound for both modes:
            // AWG waits for a TLS handshake to a literal IP for its fast check. Other profiles
            // keep HTTP; HTTPS validates the full TLS request in the precise mode.
            var useHttps = mode == ServerProbeMode.Https;
            var tcpOnly = awg is not null && !useHttps;
            await using var awgRuntime = awg is null ? null : await WindowsAmneziaWgRuntime.StartAsync(awg, address.ToString(), directory, bypass.SourceAddress, timeout.Token);
            var milliseconds = await XrayServerProbe.MeasureAsync(awgRuntime?.Proxy ?? configuration, address, WindowsRuntime.Find(), path, timeout.Token, useHttps, bypass.SourceAddress, tcpOnly, probeBudget);
            return new(milliseconds, $"{(tcpOnly ? "TLS" : useHttps ? "HTTPS" : "HTTP")} · {milliseconds:F0} мс");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, "Таймаут"); }
        catch (TimeoutException) { return new(null, "Таймаут"); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or FormatException) { return new(null, error.Message); }
        catch (HttpRequestException) { return new(null, "Ошибка сети"); }
        catch (SocketException) { return new(null, "Не удалось определить адрес"); }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        internal static readonly NoopAsyncDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
