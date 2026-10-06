using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Windows.Network;

internal sealed record TunnelHostOptions(string Runtime, string Config, IPAddress Server, string Name,
    SplitTunnelMode SplitMode, IPAddress[] SplitAddresses, string[] SplitDomains, string? ProtectionReady,
    string? AmneziaRuntime, string[] DnsServers, bool SupportsIpv4 = true, bool SupportsIpv6 = true, bool HasProcessRules = false)
{
    internal bool CaptureAllTraffic => SplitMode != SplitTunnelMode.ProxySelected || HasProcessRules;
}

/// <summary>Runs in the elevated broker. The GUI can disappear while rollback continues here.</summary>
internal sealed class WindowsTunnelHost : IDisposable
{
    private readonly Channel<string> events = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource stopping = new();
    private readonly TunnelHostOptions options;
    private readonly ITunnelNetwork network;
    private readonly Func<string, string[], ITunnelCore>? startCore;
    private readonly List<NetworkRoute> ownedRoutes = [];
    private readonly Dictionary<string, NetworkRoute> splitRoutes = new(StringComparer.Ordinal);
    private ITunnelCore? xray;
    private ITunnelCore? amnezia;
    private readonly List<Task> drainTasks = [];
    private uint tunnelIndex;
    private string stage = "PRECHECK";
    private readonly Task completion;
    internal bool HasExited => completion.IsCompleted;
    internal int ExitCode { get; private set; }

    internal WindowsTunnelHost(TunnelHostOptions options, ITunnelNetwork? network = null, Func<string, string[], ITunnelCore>? startCore = null)
    {
        this.options = options;
        this.network = network ?? new TunnelNetwork();
        this.startCore = startCore;
        completion = Task.Run(RunAsync);
    }

    internal async Task<string?> ReadLineAsync() => await events.Reader.WaitToReadAsync() ? await events.Reader.ReadAsync() : null;
    internal Task WaitForExitAsync() => completion;
    internal void RequestStop() => stopping.Cancel();
    public void Dispose() => stopping.Dispose();
    private void Emit(string value) => events.Writer.TryWrite(value);
    private string DirectoryPath => Path.GetDirectoryName(options.Config)!;
    private string PreservePath => Path.Combine(DirectoryPath, "preserve-kill-switch");

    private void Stage(string value)
    {
        stage = value;
        Emit("STAGE_" + value);
        stopping.Token.ThrowIfCancellationRequested();
    }

    private void AddRoute(NetworkRoute route)
    {
        if (network.AddRoute(route)) ownedRoutes.Add(route);
    }

    private ITunnelCore StartCore(string path, params string[] arguments)
    {
        if (startCore is not null) return startCore(path, arguments);
        var info = new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить ядро туннеля.");
        // Drain without retaining unbounded output or exposing profile credentials.
        drainTasks.Add(DrainAsync(process.StandardOutput));
        drainTasks.Add(DrainAsync(process.StandardError));
        return new TunnelCore(process);
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) > 0) { }
    }

    private async Task ConfigureOutboundAsync(PhysicalUplink uplink)
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(options.Config, stopping.Token))!;
        var tun = config["inbounds"]!.AsArray().First(n => (string?)n?["tag"] == "tun")!;
        tun["settings"]!.AsObject().Remove("autoOutboundsInterface");
        foreach (var outbound in config["outbounds"]!.AsArray())
            if (options.AmneziaRuntime is null || (string?)outbound!["tag"] != "proxy")
                outbound!["sendThrough"] = uplink.SourceAddress;
        if (options.AmneziaRuntime is not null)
        {
            Stage("AMNEZIAWG");
            var awgPath = Path.Combine(DirectoryPath, "amneziawg.json");
            var awg = JsonNode.Parse(await File.ReadAllTextAsync(awgPath, stopping.Token))!;
            awg["sourceAddress"] = uplink.SourceAddress;
            awg["sourceInterface"] = uplink.InterfaceIndex;
            await File.WriteAllTextAsync(awgPath, awg.ToJsonString(), stopping.Token);
            amnezia = StartCore(options.AmneziaRuntime, "-config", awgPath, "-owner", Environment.ProcessId.ToString());
            var readyPath = Path.Combine(DirectoryPath, "amneziawg.ready");
            await WaitAsync(() => File.Exists(readyPath), TimeSpan.FromSeconds(15));
            var port = int.Parse(await File.ReadAllTextAsync(readyPath, stopping.Token), System.Globalization.CultureInfo.InvariantCulture);
            if (port is < 1 or > 65535) throw new InvalidOperationException("Ядро AmneziaWG сообщило некорректный порт.");
            var proxy = config["outbounds"]!.AsArray().First(n => (string?)n?["tag"] == "proxy")!;
            proxy["settings"]!["servers"]![0]!["port"] = port;
        }
        await File.WriteAllTextAsync(options.Config, config.ToJsonString(), stopping.Token);
    }

    private void CheckCores()
    {
        stopping.Token.ThrowIfCancellationRequested();
        if (xray is { HasExited: true }) { Emit("ERROR_XRAY_EXIT_" + xray.ExitCode); throw new InvalidOperationException("Xray завершился."); }
        if (amnezia is { HasExited: true }) { Emit("ERROR_AWG_EXIT_" + amnezia.ExitCode); throw new InvalidOperationException("AmneziaWG завершился."); }
    }

    private async Task WaitAsync(Func<bool> predicate, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!predicate())
        {
            CheckCores();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Сетевой модуль не дождался готовности Windows.");
            await Task.Delay(100, stopping.Token);
        }
        CheckCores();
    }

    private void UpdateSplitRoutes(IEnumerable<string> addresses, PhysicalUplink uplink)
    {
        if (options.SplitMode == SplitTunnelMode.ProxyAll) return;
        var desired = addresses.Where(a => a != options.Server.ToString() && !options.DnsServers.Contains(a)).ToHashSet();
        if (desired.Count == 0) return;
        foreach (var address in splitRoutes.Keys.Except(desired).ToArray())
        {
            var old = splitRoutes[address];
            if (ownedRoutes.Contains(old)) { network.RemoveRoute(old); ownedRoutes.Remove(old); }
            splitRoutes.Remove(address);
        }
        foreach (var address in desired.Except(splitRoutes.Keys))
        {
            var route = options.SplitMode == SplitTunnelMode.BypassSelected
                ? new NetworkRoute(address + "/32", uplink.InterfaceIndex, uplink.NextHop)
                : new NetworkRoute(address + "/32", tunnelIndex, "0.0.0.0");
            AddRoute(route);
            splitRoutes[address] = route;
            Emit("SPLIT_ADDRESS_" + address);
        }
    }

    private async Task RefreshSplitAsync(PhysicalUplink uplink)
    {
        if (options.SplitMode == SplitTunnelMode.ProxyAll || options.SplitDomains.Length == 0) return;
        var names = new HashSet<string>(options.SplitDomains, StringComparer.OrdinalIgnoreCase);
        foreach (var cached in network.CachedNames())
            if (options.SplitDomains.Any(d => cached.Equals(d, StringComparison.OrdinalIgnoreCase) || cached.EndsWith('.' + d, StringComparison.OrdinalIgnoreCase)))
                names.Add(cached);
        var addresses = new HashSet<string>();
        foreach (var name in names)
        {
            try
            {
                var resolved = await network.ResolveAsync(name, stopping.Token);
                foreach (var address in resolved.Where(a => a.AddressFamily == AddressFamily.InterNetwork)) addresses.Add(address.ToString());
            }
            catch (Exception error) when (error is SocketException or TimeoutException) { }
        }
        UpdateSplitRoutes(addresses, uplink);
    }

    private async Task RunAsync()
    {
        // Mutex ownership is thread-affine; keep a dedicated thread in the broker for its lifetime
        // instead of taking/releasing a mutex across async continuations (see broker).
        bool cleanupFailed = false;
        bool dnsPrecheckCompleted = false;
        string precheckStep = "DNS";
        IAsyncDisposable? endpointLease = null;
        try
        {
            Stage("PRECHECK");
            network.PrecheckDns();
            dnsPrecheckCompleted = true;
            precheckStep = "UPLINK";
            var uplink = network.FindUplink();
            Emit("PHYSICAL_INTERFACE_" + uplink.InterfaceIndex);
            precheckStep = "SERVER_ROUTE";
            endpointLease = await network.AcquireServerRouteAsync(options.Server, uplink, stopping.Token);
            if (!network.Routes().Any(r => r.Prefix == options.Server + "/32" && r.InterfaceIndex == uplink.InterfaceIndex && r.NextHop == uplink.NextHop))
                throw new InvalidOperationException("Не установлен физический маршрут к серверу.");
            precheckStep = "OUTBOUND";
            await ConfigureOutboundAsync(uplink);
            Stage("XRAY");
            xray = StartCore(options.Runtime, "run", "-config", options.Config);
            await WaitAsync(() =>
            {
                tunnelIndex = network.FindTunnel(options.Name);
                return tunnelIndex != 0;
            }, TimeSpan.FromSeconds(20));
            Stage("ADDRESSES");
            if (options.SupportsIpv4) await network.AddAddressAsync(tunnelIndex, "172.31.255.1", 30, stopping.Token);
            if (options.SupportsIpv6) await network.AddAddressAsync(tunnelIndex, "fd52:d17::1", 64, stopping.Token);
            network.SetMetric(tunnelIndex, false);
            network.SetMetric(tunnelIndex, true);
            Emit("TUNNEL_INTERFACE_" + tunnelIndex);
            if (options.ProtectionReady is not null)
            {
                await WaitAsync(() => File.Exists(options.ProtectionReady), TimeSpan.FromSeconds(20));
                if (await File.ReadAllTextAsync(options.ProtectionReady, stopping.Token) != "READY")
                    throw new InvalidOperationException("Kill switch не активирован.");
            }
            Stage("ROUTES");
            if (!options.CaptureAllTraffic && options.SplitAddresses.Length == 0)
                throw new InvalidOperationException("Нет адресов выбранных доменов.");
            if (options.CaptureAllTraffic)
                foreach (var prefix in new[] { "0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1" })
                    AddRoute(new(prefix, tunnelIndex, prefix.Contains(':') ? "::" : "0.0.0.0"));
            else
            {
                // An unsupported family must not escape over the physical default route
                // while selected-domain routing is active. Xray rejects it explicitly.
                if (!options.SupportsIpv4)
                    foreach (var prefix in new[] { "0.0.0.0/1", "128.0.0.0/1" }) AddRoute(new(prefix, tunnelIndex, "0.0.0.0"));
                if (!options.SupportsIpv6)
                    foreach (var prefix in new[] { "::/1", "8000::/1" }) AddRoute(new(prefix, tunnelIndex, "::"));
            }
            foreach (var server in options.DnsServers)
                AddRoute(new(server + (server.Contains(':') ? "/128" : "/32"), tunnelIndex, server.Contains(':') ? "::" : "0.0.0.0"));
            UpdateSplitRoutes(options.SplitAddresses.Select(a => a.ToString()), uplink);
            Stage("DNS_RULE");
            Emit(network.InstallDns(tunnelIndex, options.Name, options.DnsServers) ? "DNS_INTERFACE_READY" : "DNS_NRPT_READY");
            Stage("DNS_CACHE");
            network.FlushDns();
            if (options.SplitMode == SplitTunnelMode.ProxySelected)
            {
                Stage("DNS_VERIFY");
                var domain = options.SplitDomains.FirstOrDefault(d => !IPAddress.TryParse(d, out _));
                if (domain is not null)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (true)
                    {
                        try { if ((await network.ResolveAsync(domain, stopping.Token)).Any(a =>
                            a.AddressFamily == AddressFamily.InterNetwork ? options.SupportsIpv4 : options.SupportsIpv6)) break; }
                        catch (Exception error) when (error is SocketException or TimeoutException) { }
                        if (DateTime.UtcNow >= deadline) throw new TimeoutException("DNS выбранного домена не стал доступен.");
                        await Task.Delay(250, stopping.Token);
                    }
                }
            }
            else
            {
                Stage("PROBE");
                try
                {
                    await network.ProbeAsync(tunnelIndex, !options.SupportsIpv4, stopping.Token);
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied)
                {
                    Emit("ERROR_NETWORK_ACCESS_DENIED");
                    throw;
                }
                catch (TunnelRouteException)
                {
                    Emit("ERROR_PROBE_ROUTE");
                    throw;
                }
                catch (Exception error) when (!stopping.IsCancellationRequested)
                {
                    // A single control endpoint may time out while the tunnel remains usable.
                    // Keep the warning, but record only safe type/numeric diagnostics.
                    Emit("PROBE_WARNING");
                    Emit("PROBE_EXCEPTION_" + error.GetType().Name);
                    if (error is SocketException socket) Emit("PROBE_SOCKET_" + socket.NativeErrorCode);
                }
            }
            Emit("CONNECTED");
            var nextSplit = DateTime.MinValue;
            while (!stopping.IsCancellationRequested)
            {
                CheckCores();
                PhysicalUplink? current = null;
                try { current = network.FindUplink(); } catch (InvalidOperationException) { }
                if (current != uplink)
                {
                    Emit("ERROR_NETWORK_CHANGED");
                    if (options.ProtectionReady is not null) await File.WriteAllTextAsync(PreservePath, "READY");
                    break;
                }
                if (DateTime.UtcNow >= nextSplit)
                {
                    await RefreshSplitAsync(uplink);
                    nextSplit = DateTime.UtcNow.AddSeconds(1);
                }
                await Task.Delay(500, stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { Emit("CANCELLED"); }
        catch (Exception error)
        {
            Emit("ERROR_STAGE_" + stage);
            if (stage == "PRECHECK") Emit("ERROR_PRECHECK_" + precheckStep);
            // Only type and numeric code are logged; raw messages may contain profile secrets.
            Emit("ERROR_EXCEPTION_" + error.GetType().Name);
            if (error is DnsPolicyException dnsError) Emit("ERROR_DNS_" + dnsError.Code);
            if (error is System.Runtime.InteropServices.COMException)
                Emit("ERROR_HRESULT_" + error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            if (error is System.ComponentModel.Win32Exception native) Emit("ERROR_NATIVE_" + native.NativeErrorCode);
            if (error is InvalidOperationException && error.Message.StartsWith("Обнаружены существующие правила DNS", StringComparison.Ordinal)) Emit("ERROR_DNS_POLICY");
            Emit("ERROR_TUN");
            ExitCode = 1;
        }
        finally
        {
            Emit("STAGE_CLEANUP");
            foreach (var route in ownedRoutes.AsEnumerable().Reverse())
                try { network.RemoveRoute(route); }
                catch { if (route.InterfaceIndex != tunnelIndex) cleanupFailed = true; }
            // A refused precheck must not remove policies installed by the corporate network or another owner.
            if (dnsPrecheckCompleted)
                try { network.CleanupDns(); } catch { cleanupFailed = true; }
            foreach (var process in new[] { xray, amnezia })
            {
                try { if (process is not null) await process.StopAsync(); }
                catch { cleanupFailed = true; }
            }
            try { await Task.WhenAll(drainTasks); } catch { }
            foreach (var path in new[] { options.Config, options.Config + ".stop", options.ProtectionReady,
                Path.Combine(DirectoryPath, "amneziawg.json"), Path.Combine(DirectoryPath, "amneziawg.ready"), Path.Combine(DirectoryPath, "amneziawg.ready.tmp") })
                try { if (path is not null) File.Delete(path); } catch { cleanupFailed = true; }
            try { if (endpointLease is not null) await endpointLease.DisposeAsync(); } catch { cleanupFailed = true; }
            Emit(cleanupFailed ? "ERROR_CLEANUP" : "STOPPED");
            events.Writer.TryComplete();
        }
    }
}
