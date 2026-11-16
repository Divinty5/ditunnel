using Android.Content;
using Android.Net;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiTunnel.Platform.Android;

internal static class AndroidXrayProbeRunner
{
    private static readonly SemaphoreSlim RuntimeGate = new(1, 1);

    public static async Task<ServerProbeResult> MeasureAsync(Context context, ImportedProfile profile, CancellationToken cancellationToken)
    {
        await RuntimeGate.WaitAsync(cancellationToken);
        try { return await MeasureProfileAsync(context, profile, cancellationToken); }
        finally { RuntimeGate.Release(); }
    }

    private static async Task<ServerProbeResult> MeasureProfileAsync(Context context, ImportedProfile profile, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var stage = "Профиль";
        ConnectivityManager? manager = null;
        UnchangedSocketController? controller = null;
        var bound = false;
        var startingXray = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var configuration = XrayProfileConverter.Convert(profile);
            stage = "Служба проверки";
            manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
            var network = manager is null ? null : FindPhysicalNetwork(manager);
            if (manager is null || network is null) throw new AndroidProbeException(stage, "Connection");

            stage = "DNS сервера";
            var address = IPAddress.TryParse(configuration.ServerHost, out var ip) ? ip.ToString()
                : (await Task.Run(() => network.GetAllByName(configuration.ServerHost), cancellationToken)
                    .WaitAsync(AndroidProbePolicy.XrayDnsBudget, cancellationToken))?
                    .OrderBy(item => item.HostAddress?.Contains(':') == true ? 1 : 0).FirstOrDefault()?.HostAddress;
            if (address is null) throw new AndroidProbeException(stage, "Dns");
            cancellationToken.ThrowIfCancellationRequested();

            stage = "Служба проверки";
            bound = manager.BindProcessToNetwork(network);
            if (!bound) throw new AndroidProbeException(stage, "Connection");

            stage = "Xray";
            budget.CancelAfter(AndroidProbePolicy.XrayProfileBudget);
            var item = new AndroidProbeOutbound(configuration, address, ReserveLoopbackPort());
            controller = new UnchangedSocketController();
            global::LibXray.LibXray.RegisterDialerController(controller);
            global::LibXray.LibXray.RegisterListenerController(controller);
            global::LibXray.LibXray.SetDNS(controller, "1.1.1.1:53");
            startingXray = true;
            if (!Invoke("runXrayFromJson", new JsonObject { ["configJSON"] = AndroidProbeBatchConfiguration.Build([item]) }))
                throw new AndroidProbeException(stage, "Runtime");

            await SocksTcpProbe.WaitForListenerAsync(item.Port, item.Credentials, () => true, budget.Token);
            stage = "HTTPS";
            using var handler = new SocketsHttpHandler
            {
                Proxy = item.Credentials.CreateProxy(item.Port), UseProxy = true, AllowAutoRedirect = false
            };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var result = await ProbePolicy.CheckHttpClientAsync(http, budget.Token);
            result = result with { Diagnostics = result.Diagnostics! with { DurationMilliseconds = watch.Elapsed.TotalMilliseconds } };
            return result.Diagnostics?.ErrorType == "Internal" ? ProbePolicy.Incomplete(result) : result;
        }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = AndroidProbePolicy.Failure(budget.IsCancellationRequested ? new TimeoutException() : error,
                stage, "HTTPS", watch.Elapsed.TotalMilliseconds);
            return stage is "Служба проверки" or "Xray" || failure.Diagnostics?.ErrorType is "Internal" or "Runtime"
                ? ProbePolicy.Incomplete(failure) : failure;
        }
        finally
        {
            if (startingXray)
                try { _ = Invoke("stopXray", new JsonObject()); } catch { }
            if (controller is not null)
            {
                try { global::LibXray.LibXray.ResetDNS(); } catch { }
                controller.Dispose();
            }
            if (bound)
                try { _ = manager!.BindProcessToNetwork(null); } catch { }
        }
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static Network? FindPhysicalNetwork(ConnectivityManager manager)
    {
#pragma warning disable CA1422
        var active = manager.ActiveNetwork;
        return PhysicalNetworkSelection.Select(manager.GetAllNetworks().Select(candidate =>
        {
            var capabilities = manager.GetNetworkCapabilities(candidate);
            return new PhysicalNetworkCandidate<Network>(candidate,
                capabilities?.HasCapability(NetCapability.NotVpn) == true,
                capabilities?.HasCapability(NetCapability.Internet) == true,
                capabilities?.HasCapability(NetCapability.Validated) == true,
                candidate.Equals(active));
        }));
#pragma warning restore CA1422
    }

    private static bool Invoke(string method, JsonObject payload)
    {
        var request = new JsonObject { ["apiVersion"] = 1, ["method"] = method, ["payload"] = payload }.ToJsonString();
        var responseJson = global::LibXray.LibXray.Invoke(request) ?? throw new InvalidOperationException();
        using var response = JsonDocument.Parse(responseJson);
        return response.RootElement.TryGetProperty("success", out var success) && success.GetBoolean();
    }

    private sealed class UnchangedSocketController : Java.Lang.Object, global::LibXray.IDialerController
    {
        public bool ProtectFd(long fileDescriptor) => fileDescriptor >= 0;
    }
}
