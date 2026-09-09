using System.Net;
using System.Net.Sockets;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;

namespace DiTunnel.Platform.Windows;

public sealed class WindowsServerProbe : IServerProbe
{
    private readonly WindowsKillSwitchController? killSwitch;

    public WindowsServerProbe(WindowsKillSwitchController? killSwitch = null) => this.killSwitch = killSwitch;

    public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        var directory = WindowsRuntime.CreateSession();
        var path = Path.Combine(directory, "config.json");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var configuration = XrayProfileConverter.Convert(profile);
            var address = (await Dns.GetHostAddressesAsync(configuration.ServerHost, timeout.Token)).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new NotSupportedException("Нет IPv4-адреса сервера.");
            await using var permit = killSwitch is null
                ? NoopAsyncDisposable.Instance
                : await killSwitch.PermitProbeEndpointAsync(address, configuration.ServerPort, configuration.ServerTransport, timeout.Token);
            await using var bypass = await WindowsProbeRouteBypass.CreateAsync(address, directory, timeout.Token);
            var milliseconds = await XrayServerProbe.MeasureAsync(configuration, address, WindowsRuntime.Find(), path, timeout.Token);
            return new(milliseconds, $"HTTPS · {milliseconds:F0} мс");
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
