using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using DiTunnel.Platform.Linux.Network;
using DiTunnel.NetworkHost.Linux;
using Tmds.DBus.Protocol;

[SupportedOSPlatform("linux")]
internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--vpn-namespace") { await LinuxVpnSmoke.RunAsync(args[1], int.Parse(args[2])); return; }
        if (!OperatingSystem.IsLinux() || args.Length != 3) throw new ArgumentException("Expected dotnet, host DLL, runtime directory on Linux.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var directory = Path.Combine(Path.GetTempPath(), "ditunnel-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await CheckXrayLoopbackAsync(args[2], directory, token);
            await CheckAwgOwnerAsync(args[2], directory, token);
            await CheckAuthorizationAsync(token);
            await CheckServiceAsync(args[0], args[1], args[2], directory, token);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task CheckAuthorizationAsync(CancellationToken token)
    {
        using var authorityConnection = new DBusConnection(DBusAddress.Session!);
        using var caller = new DBusConnection(DBusAddress.Session!);
        await authorityConnection.ConnectAsync();
        await caller.ConnectAsync();
        var authority = new SyntheticAuthority(caller.UniqueName!);
        authorityConnection.AddMethodHandler(authority);
        await authorityConnection.RequestNameAsync("org.freedesktop.PolicyKit1");
        var authorization = new LinuxProbeAuthorization(caller, developmentUid: null);
        if (await authorization.AuthorizeAsync(caller.UniqueName!, token)) throw new InvalidOperationException("Denied polkit decision ignored.");
        authority.Allow = true;
        if (!await authorization.AuthorizeAsync(caller.UniqueName!, token)) throw new InvalidOperationException("Polkit subject or decision invalid.");
        if (await authorization.AuthorizeAsync("org.example.ForgedSender", token)) throw new InvalidOperationException("Nonunique bus sender accepted.");
        Console.WriteLine("PASS_POLKIT_WIRE_SUBJECT_AND_ALLOW_DENY_ON_ISOLATED_BUS");
    }

    private sealed class SyntheticAuthority(string sender) : IPathMethodHandler
    {
        public string Path => "/org/freedesktop/PolicyKit1/Authority";
        public bool HandlesChildPaths => false;
        public bool Allow { get; set; }
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.MemberAsString != "CheckAuthorization" || context.Request.SignatureAsString != "(sa{sv})sa{ss}us")
                throw new InvalidOperationException("Unexpected polkit method.");
            var reader = context.Request.GetBodyReader();
            reader.AlignStruct();
            if (reader.ReadString() != "system-bus-name") throw new InvalidOperationException("Unexpected subject.");
            var subject = reader.ReadDictionaryStart();
            var names = 0;
            while (reader.HasNext(subject))
            {
                if (reader.ReadString() != "name" || reader.ReadVariantValue().GetString() != sender)
                    throw new InvalidOperationException("Polkit sender is not broker-authenticated name.");
                names++;
            }
            if (names != 1 || reader.ReadString() != LinuxNetworkProtocol.ProbeAction) throw new InvalidOperationException("Unexpected polkit action.");
            var details = reader.ReadDictionaryStart();
            if (reader.HasNext(details) || reader.ReadUInt32() != 0 || reader.ReadString() != "") throw new InvalidOperationException("Unexpected authorization details.");
            using var writer = context.CreateReplyWriter("(bba{ss})");
            writer.WriteStructureStart();
            writer.WriteBool(Allow);
            writer.WriteBool(false);
            var reply = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(reply);
            context.Reply(writer.CreateMessage());
            return default;
        }
    }

    private static async Task CheckXrayLoopbackAsync(string runtimes, string directory, CancellationToken token)
    {
        using var web = new TcpListener(IPAddress.Loopback, 0);
        web.Start();
        var webPort = ((IPEndPoint)web.LocalEndpoint).Port;
        var response = Task.Run(async () =>
        {
            using var peer = await web.AcceptTcpClientAsync(token);
            var stream = peer.GetStream();
            var bytes = new byte[4096];
            _ = await stream.ReadAsync(bytes, token);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\nSYNTHETIC"u8.ToArray(), token);
        }, token);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "none" },
            ["inbounds"] = new JsonArray(new JsonObject { ["protocol"] = "socks", ["listen"] = "127.0.0.1", ["port"] = port, ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false } }),
            ["outbounds"] = new JsonArray(new JsonObject { ["protocol"] = "freedom" })
        };
        var path = Path.Combine(directory, "loopback.json");
        await File.WriteAllTextAsync(path, config.ToJsonString(), token);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await using var runtime = new XrayProcessManager(new XrayOptions
        {
            ExecutablePath = Path.Combine(runtimes, "xray"), WorkingDirectory = runtimes,
            ValidateConfigurationBeforeStart = false, ProcessLifetimeFactory = process => new LinuxProcessLifetime(process)
        });
        await runtime.StartAsync(path, token);
        await SocksTcpProbe.WaitForListenerAsync(port, () => runtime.IsRunning, token);
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{port}") };
        using var http = new HttpClient(handler);
        var text = await http.GetStringAsync($"http://127.0.0.1:{webPort}/", token);
        if (text != "SYNTHETIC") throw new InvalidOperationException("Loopback response mismatch.");
        await response;
        var pid = runtime.ProcessId!.Value;
        var watch = Stopwatch.StartNew();
        await runtime.StopAsync(token);
        if (runtime.IsRunning || Directory.Exists($"/proc/{pid}") || watch.Elapsed >= TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("Xray did not terminate gracefully.");
        File.Delete(path);
        Console.WriteLine("PASS_XRAY_LOOPBACK_AND_SIGTERM");
    }

    private static async Task CheckAwgOwnerAsync(string runtimes, string directory, CancellationToken token)
    {
        var content = $"[Interface]\nPrivateKey={Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray())}\nAddress=192.0.2.1/32\nDNS=192.0.2.2\nJc=2\nJmin=10\nJmax=20\nH1=12345\nH2=23456\nH3=34567\nH4=45678\n[Peer]\nPublicKey={Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray())}\nEndpoint=127.0.0.1:9\nAllowedIPs=0.0.0.0/0\n";
        var configuration = AmneziaWgProfileConverter.Convert(new("Synthetic", "AmneziaWG", content));
        var path = Path.Combine(directory, "owner-awg.json");
        var ready = Path.Combine(directory, "owner-awg.ready");
        await File.WriteAllTextAsync(path, configuration.BuildProxyRuntimeConfiguration("127.0.0.1", new string('u', 32), new string('p', 64), ready), token);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var owner = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "30" } })!;
        using var bridge = Process.Start(new ProcessStartInfo(Path.Combine(runtimes, "ditunnel-awg"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-config", path, "-owner", owner.Id.ToString() }
        })!;
        try
        {
            var line = await bridge.StandardOutput.ReadLineAsync(token);
            if (line?.StartsWith("READY_", StringComparison.Ordinal) != true) throw new InvalidOperationException("AWG readiness missing.");
            owner.Kill();
            await owner.WaitForExitAsync(token);
            await bridge.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(3), token);
            if (bridge.ExitCode != 0 || File.Exists(ready)) throw new InvalidOperationException("AWG owner cleanup failed.");
        }
        finally
        {
            if (!owner.HasExited) owner.Kill();
            if (!bridge.HasExited) bridge.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(CancellationToken.None);
            await bridge.WaitForExitAsync(CancellationToken.None);
        }
        using var terminated = Process.Start(new ProcessStartInfo(Path.Combine(runtimes, "ditunnel-awg"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-config", path, "-owner", Environment.ProcessId.ToString() }
        })!;
        using var bridgeLifetime = new LinuxProcessLifetime(terminated);
        try
        {
            if ((await terminated.StandardOutput.ReadLineAsync(token))?.StartsWith("READY_", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("AWG second readiness missing.");
            bridgeLifetime.RequestShutdown();
            await terminated.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(3), token);
            if (terminated.ExitCode != 0 || File.Exists(ready)) throw new InvalidOperationException("AWG SIGTERM cleanup failed.");
        }
        finally
        {
            if (!terminated.HasExited) terminated.Kill(entireProcessTree: true);
            await terminated.WaitForExitAsync(CancellationToken.None);
        }
        File.Delete(path);
        Console.WriteLine("PASS_AWG_OWNER_EXIT_AND_SIGTERM");
    }

    private static async Task CheckServiceAsync(string dotnet, string host, string runtimes, string directory, CancellationToken token)
    {
        var state = Path.Combine(directory, "host-state");
        Directory.CreateDirectory(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var service = Process.Start(new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { host, "--session-development", runtimes, state }
        })!;
        using var lifetime = new LinuxProcessLifetime(service);
        try
        {
            var ready = await service.StandardOutput.ReadLineAsync(token);
            if (ready != "READY_DITUNNEL_NETWORK1_PROBE_ONLY") throw new InvalidOperationException("Service readiness missing: " + await service.StandardError.ReadToEndAsync(token));
            using var client = new LinuxNetworkClient(DBusAddress.Session);
            var capabilities = await client.GetCapabilitiesAsync(token);
            if (capabilities != new LinuxNetworkCapabilities(1, true, false, false)) throw new InvalidOperationException("Unsafe capabilities.");
            using var raw = new DBusConnection(DBusAddress.Session!);
            await raw.ConnectAsync();
            foreach (var value in new[] { "{}", new string('a', LinuxNetworkProtocol.MaximumProfileBytes + 1) })
            {
                try { await raw.CallMethodAsync(InvalidProbe(raw, value)).WaitAsync(TimeSpan.FromSeconds(5), token); throw new InvalidOperationException("Invalid payload accepted."); }
                catch (DBusErrorReplyException error) when (error.ErrorName == "org.divinty5.DiTunnel.InvalidRequest") { }
            }
            if (HasRuntimeState(state)) throw new InvalidOperationException("Invalid payload started a runtime.");
            using var remote = new TcpListener(IPAddress.Loopback, 0);
            remote.Start();
            var port = ((IPEndPoint)remote.LocalEndpoint).Port;
            var profile = new ImportedProfile("Synthetic", "VLESS", $"vless://00000000-0000-4000-8000-000000000001@127.0.0.1:{port}?encryption=none&security=none");
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var probe = client.ProbeAsync(profile, cancel.Token);
            using var peer = await remote.AcceptTcpClientAsync(token);
            var files = Directory.GetFiles(state, "*.json", SearchOption.AllDirectories);
            if (files.Length == 0 || files.Any(file => File.GetUnixFileMode(file) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)))
                throw new InvalidOperationException("Probe configuration permissions are not 0600.");
            // The silent synthetic endpoint forces the lease to remain active until cancellation.
            cancel.Cancel();
            try { await probe; throw new InvalidOperationException("Cancelled probe succeeded."); }
            catch (OperationCanceledException) { }
            if (HasRuntimeState(state)) throw new InvalidOperationException("Cancel acknowledged before cleanup.");
            using var disappearedClient = new LinuxNetworkClient(DBusAddress.Session);
            var abandoned = disappearedClient.ProbeAsync(profile, token);
            using var abandonedPeer = await remote.AcceptTcpClientAsync(token);
            disappearedClient.Dispose();
            _ = await abandoned;
            var until = Stopwatch.StartNew();
            while (HasRuntimeState(state) && until.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(25, token);
            if (HasRuntimeState(state)) throw new InvalidOperationException("Lost sender left a runtime.");
            Console.WriteLine("PASS_DBUS_CAPABILITIES_CANCEL_OWNERSHIP_AND_PRIVATE_FILES");
        }
        finally
        {
            if (!service.HasExited) lifetime.RequestShutdown();
            try { await service.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { service.Kill(entireProcessTree: true); await service.WaitForExitAsync(); }
        }
        if (service.ExitCode != 0) throw new InvalidOperationException("Service shutdown failed.");
            File.Delete(Path.Combine(state, "host.lock"));
            Directory.Delete(state);
    }

    private static bool HasRuntimeState(string directory) => Directory.EnumerateFileSystemEntries(directory)
        .Any(path => Path.GetFileName(path) != "host.lock");

    private static MessageBuffer InvalidProbe(DBusConnection connection, string value)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: LinuxNetworkProtocol.Service, path: LinuxNetworkProtocol.Path,
            @interface: LinuxNetworkProtocol.Interface, member: "Probe", signature: "ussu");
        writer.WriteUInt32(1);
        writer.WriteString(Guid.NewGuid().ToString("D"));
        writer.WriteString(value);
        writer.WriteUInt32(0);
        return writer.CreateMessage();
    }
}
