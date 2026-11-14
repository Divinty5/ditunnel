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
using OperationCanceledException = System.OperationCanceledException;

namespace DiTunnel.Platform.Android;

internal static class AndroidXrayProbeRunner
{
    public static async Task<ServerProbeResult> MeasureAsync(Context context, ImportedProfile profile, ServerProbeMode mode, CancellationToken cancellationToken)
        => (await MeasureBatchAsync(context, [profile], mode, cancellationToken))[0];

    public static async Task<IReadOnlyList<ServerProbeResult>> MeasureBatchAsync(Context context, IReadOnlyList<ImportedProfile> profiles,
        ServerProbeMode mode, CancellationToken cancellationToken, Action<int, ServerProbeResult>? report = null)
    {
        if (profiles.Count is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(profiles));
        var watch = Stopwatch.StartNew();
        var results = new ServerProbeResult?[profiles.Count];
        void Complete(int index, ServerProbeResult result)
        {
            results[index] = result;
            report?.Invoke(index, result);
        }
        var prepared = new AndroidProbeOutbound?[profiles.Count];
        var manager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager
            ?? throw new AndroidProbeException("Подключение к серверу", "Connection");
        var network = FindPhysicalNetwork(manager) ?? throw new AndroidProbeException("Подключение к серверу", "Connection");
        // Resolve independently and in parallel. A malformed or unresolvable peer never
        // enters the shared runtime and cannot discard neighbouring results.
        await Task.WhenAll(profiles.Select(async (profile, index) =>
        {
            var stage = "Профиль";
            try
            {
                var converted = XrayProfileConverter.Convert(profile);
                stage = "DNS сервера";
                var address = System.Net.IPAddress.TryParse(converted.ServerHost, out var ip) ? ip.ToString()
                    : (await Task.Run(() => network.GetAllByName(converted.ServerHost), cancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken))?
                        .OrderBy(item => item.HostAddress?.Contains(':') == true ? 1 : 0).FirstOrDefault()?.HostAddress;
                if (address is null) throw new AndroidProbeException(stage, "Dns");
                var item = new AndroidProbeOutbound(converted, address, ReserveLoopbackPort());
                // Validate this configuration before merging it into the batch.
                _ = AndroidProbeBatchConfiguration.Build([item]);
                prepared[index] = item;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { Complete(index, AndroidProbePolicy.Failure(error, stage, "HTTPS", watch.Elapsed.TotalMilliseconds)); }
        }));
        if (prepared.All(item => item is null)) return results.Select(item => item!).ToArray();
        if (!manager.BindProcessToNetwork(network)) throw new AndroidProbeException("Подключение к серверу", "Connection");
        using var controller = new UnchangedSocketController();
        try
        {
            global::LibXray.LibXray.RegisterDialerController(controller);
            global::LibXray.LibXray.RegisterListenerController(controller);
            global::LibXray.LibXray.SetDNS(controller, "1.1.1.1:53");
            async Task RunGroup(int[] indexes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var started = false;
                try
                {
                    using var response = Invoke("runXrayFromJson", new JsonObject
                    {
                        ["configJSON"] = AndroidProbeBatchConfiguration.Build(indexes.Select(index => prepared[index]!).ToArray())
                    });
                    started = response.Success;
                    if (started)
                    {
                        await Task.WhenAll(indexes.Select(async index =>
                        {
                            var stage = "Xray";
                            try
                            {
                                var item = prepared[index]!;
                                var port = item.Port;
                                await WaitForListenerAsync(item, cancellationToken);
                                stage = "HTTPS";
                                using var handler = new SocketsHttpHandler
                                {
                                    Proxy = item.Credentials.CreateProxy(port), UseProxy = true, AllowAutoRedirect = false
                                };
                                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                                var result = await AndroidProbePolicy.CheckHttpClientAsync(http, cancellationToken);
                                Complete(index, result with { Diagnostics = result.Diagnostics! with { DurationMilliseconds = watch.Elapsed.TotalMilliseconds } });
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception error) { Complete(index, AndroidProbePolicy.Failure(error, stage, "HTTPS", watch.Elapsed.TotalMilliseconds)); }
                        }));
                    }
                }
                finally
                {
                    // A rejected configuration may have partly initialized the native runtime.
                    try { using var _ = Invoke("stopXray", new JsonObject()); } catch { }
                }
                if (started) return;
                if (indexes.Length == 1)
                    Complete(indexes[0], AndroidProbePolicy.Failure(new AndroidProbeException("Xray", "Runtime"), "Xray", "HTTPS", watch.Elapsed.TotalMilliseconds));
                else
                {
                    // Only a failed native start needs subdivision; healthy batches start once.
                    var middle = indexes.Length / 2;
                    await RunGroup(indexes[..middle]);
                    await RunGroup(indexes[middle..]);
                }
            }
            await RunGroup(Enumerable.Range(0, profiles.Count).Where(index => prepared[index] is not null).ToArray());
            return results.Select(item => item!).ToArray();
        }
        finally
        {
            try { global::LibXray.LibXray.ResetDNS(); } catch { }
            try { _ = manager.BindProcessToNetwork(null); } catch { }
        }
    }

    public static Task<ServerProbeResult> MeasureViaProxyAsync(Context context, ImportedProfile profile, ServerProbeMode mode, CancellationToken cancellationToken)
        => MeasureAsync(context, profile, mode, cancellationToken);

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForListenerAsync(AndroidProbeOutbound item, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await SocksTcpProbe.WaitForListenerAsync(item.Port, item.Credentials, () => true, timeout.Token);
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

    private sealed class UnchangedSocketController : Java.Lang.Object, global::LibXray.IDialerController
    {
        public bool ProtectFd(long fileDescriptor) => fileDescriptor >= 0;
    }

    private sealed record InvokeResponse(bool Success, JsonDocument? Data, string? Error) : IDisposable
    {
        public void Dispose() => Data?.Dispose();
    }
}
