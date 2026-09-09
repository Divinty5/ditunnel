using Android.Content;
using Android.Net;
using Android.Util;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using System.Diagnostics;

namespace DiTunnel.Platform.Android;

public sealed class AndroidServerProbe(Context context) : IServerProbe
{
    private readonly Context context = context.ApplicationContext ?? context;

    public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            var configuration = XrayProfileConverter.Convert(profile);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var milliseconds = await Task.Run(() => Measure(configuration.ServerHost, configuration.ServerPort, timeout.Token), timeout.Token);
            return new(milliseconds, $"TCP · {milliseconds:F0} мс");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, "Таймаут"); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or FormatException) { return new(null, error.Message); }
        catch (Exception error)
        {
            Log.Warn("DiTunnelProbe", error.ToString());
            return new(null, "Ошибка сети");
        }
    }

    private double Measure(string host, int port, CancellationToken cancellationToken)
    {
        var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager
            ?? throw new InvalidOperationException("Сетевой сервис Android недоступен.");
#pragma warning disable CA1422 // There is no synchronous replacement; the provider is queried only for a short user-initiated probe.
        var network = manager.GetAllNetworks()
            .FirstOrDefault(candidate =>
            {
                var capabilities = manager.GetNetworkCapabilities(candidate);
                return capabilities?.HasCapability(NetCapability.NotVpn) == true
                    && capabilities.HasCapability(NetCapability.Internet)
                    && capabilities.HasCapability(NetCapability.Validated);
            })
            ?? manager.ActiveNetwork
            ?? throw new InvalidOperationException("Нет доступной сети.");
#pragma warning restore CA1422
        var socketFactory = network.SocketFactory
            ?? throw new InvalidOperationException("Физическая сеть Android недоступна.");
        using var socket = socketFactory.CreateSocket()
            ?? throw new InvalidOperationException("Android не создал сетевой сокет.");
        using var registration = cancellationToken.Register(socket.Close);
        var address = System.Net.IPAddress.TryParse(host, out var parsedAddress)
            ? Java.Net.InetAddress.GetByAddress(parsedAddress.GetAddressBytes())
            : (network.GetAllByName(host) ?? []).FirstOrDefault();
        if (address is null) throw new InvalidOperationException("Не удалось определить адрес VPN-сервера.");
        var watch = Stopwatch.StartNew();
        socket.Connect(new Java.Net.InetSocketAddress(address, port), 8000);
        return watch.Elapsed.TotalMilliseconds;
    }
}
