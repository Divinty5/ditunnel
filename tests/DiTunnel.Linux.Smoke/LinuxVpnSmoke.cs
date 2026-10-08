using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.NetworkHost.Linux;
using DiTunnel.Platform.Linux.Network;
using Tmds.DBus.Protocol;

[SupportedOSPlatform("linux")]
internal static class LinuxVpnSmoke
{
    public static async Task RunAsync(string runtime, int peerPid)
    {
        if (new FileInfo("/proc/self/ns/net").LinkTarget == new FileInfo("/proc/1/ns/net").LinkTarget)
            throw new InvalidOperationException("VPN smoke requires an isolated network namespace.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        // resolvectl's system-bus calls are confined to this private test bus.
        Environment.SetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS", DBusAddress.Session);
        using var resolvedBus = new DBusConnection(DBusAddress.Session!);
        await resolvedBus.ConnectAsync();
        var resolved = new SyntheticResolved();
        resolvedBus.AddMethodHandler(resolved);
        await resolvedBus.RequestNameAsync("org.freedesktop.resolve1");
        var directory = Path.Combine(Path.GetTempPath(), "ditunnel-vpn-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var commands = new Commands(resolved);
        var baseline4 = await commands.IpAsync(token, "-4", "-j", "rule", "show");
        var baseline6 = await commands.IpAsync(token, "-6", "-j", "rule", "show");
        try
        {
            var path = Path.Combine(directory, "synthetic-server.json");
            await File.WriteAllTextAsync(path, """
                {"log":{"loglevel":"none"},"inbounds":[{"listen":"10.77.0.2","port":18080,"protocol":"vless","settings":{"clients":[{"id":"11111111-1111-1111-1111-111111111111"}],"decryption":"none"}}],"outbounds":[{"protocol":"freedom"}]}
                """, token);
            var start = new ProcessStartInfo("/usr/bin/nsenter") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-t", peerPid.ToString(), "-n", Path.Combine(runtime, "xray"), "run", "-c", path }) start.ArgumentList.Add(arg);
            using var server = Process.Start(start)!;
            using var serverLifetime = new LinuxProcessLifetime(server);
            var output = server.StandardOutput.ReadToEndAsync();
            var errors = server.StandardError.ReadToEndAsync();
            try
            {
                await Task.Delay(300, token);
                var profile = ProfileParser.Parse("vless://11111111-1111-1111-1111-111111111111@10.77.0.2:18080?security=none&type=tcp").Single();
                using var bus = new DBusConnection(DBusAddress.Session!);
                await bus.ConnectAsync();
                await using var probes = new LinuxProbeCoordinator(new LinuxRuntimeServerProbe(runtime, directory));
                await using var vpn = new LinuxVpnCoordinator(() => new LinuxVpnSession(runtime, directory, commands));
                var auth = new LinuxProbeAuthorization(bus, 0);
                bus.AddMethodHandler(new LinuxNetworkService(probes, auth, new(vpn, auth,
                    () => LinuxVpnSession.RecoverAsync(directory, commands, token))));
                using var disappeared = await bus.AddMatchAsync(new MatchRule { Sender = "org.freedesktop.DBus", Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Type = MessageType.Signal },
                    static (message, _) => { var reader = message.GetBodyReader(); return (reader.ReadString(), reader.ReadString(), reader.ReadString()); },
                    (Notification<(string, string, string)> notification) => { if (notification.HasValue && notification.Value.Item3.Length == 0) _ = vpn.SenderDisconnectedAsync(notification.Value.Item1); }, emitOnCapturedContext: false);
                await bus.RequestNameAsync(LinuxNetworkProtocol.Service);
                await using var client = new LinuxNetworkClient(DBusAddress.Session);
                if (!(await client.GetCapabilitiesAsync(token)).CanConnect) throw new InvalidOperationException("VPN capability missing.");
                await client.ConnectAsync(profile, token);
                if (client.Status.State != VpnConnectionState.Connected) throw new InvalidOperationException("VPN not connected.");
                foreach (var address in new[] { "198.51.100.20", "2001:db8:77::20" })
                {
                    var route = JsonNode.Parse(await commands.IpAsync(token, "-j", "route", "get", address))![0]!;
                    if (!((string?)route["dev"])!.StartsWith("dtn")) throw new InvalidOperationException("Application traffic bypassed TUN.");
                }
                var bypass = JsonNode.Parse(await commands.IpAsync(token, "-j", "route", "get", "10.77.0.2", "mark", "51820"))![0]!;
                if ((string?)bypass["dev"] != "uplink") throw new InvalidOperationException("Encrypted transport recursively entered TUN.");
                await CheckTrafficAsync(token);
                await client.DisconnectAsync(token);
                await AssertCleanAsync(commands, directory, baseline4, baseline6, token);
                if (!resolved.SawDns || !resolved.SawRootDomain || !resolved.SawDefaultRoute || !resolved.SawRevert)
                    throw new InvalidOperationException("Per-link resolved D-Bus calls were not made.");
                Console.WriteLine("PASS_VPN_DBUS_TUN_TCP_UDP_IPV4_IPV6_AND_RESOLVED_WIRE_DISCONNECT");

                commands.FailDns = true;
                try { await client.ConnectAsync(profile, token); throw new Exception("DNS failure ignored"); }
                catch (InvalidOperationException) { }
                commands.FailDns = false;
                await AssertCleanAsync(commands, directory, baseline4, baseline6, token);
                Console.WriteLine("PASS_VPN_DNS_FAILURE_ROLLBACK");

                await client.ConnectAsync(profile, token);
                await KillOwnedCoreAsync(directory, token);
                await WaitIdleAsync(vpn, token);
                await AssertCleanAsync(commands, directory, baseline4, baseline6, token);
                Console.WriteLine("PASS_VPN_RUNTIME_CRASH_ROLLBACK");

                using var orphan = new LinuxNetworkClient(DBusAddress.Session);
                await orphan.ConnectAsync(profile, token);
                orphan.Dispose();
                await WaitIdleAsync(vpn, token);
                await AssertCleanAsync(commands, directory, baseline4, baseline6, token);
                Console.WriteLine("PASS_VPN_UI_DISCONNECT_ROLLBACK");

                // Simulate a lost host session after systemd has stopped its owned core.
                await using (var lost = new LinuxVpnSession(runtime, directory, commands))
                {
                    await lost.StartAsync(profile, token);
                    await KillOwnedCoreAsync(directory, token);
                    await LinuxVpnSession.RecoverAsync(directory, commands, token);
                    await AssertCleanAsync(commands, directory, baseline4, baseline6, token);
                }
                Console.WriteLine("PASS_VPN_OWNERSHIP_JOURNAL_RESTART_RECOVERY");

                var privateKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
                var publicKey = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());
                var awgProfile = ProfileParser.Parse($"[Interface]\nPrivateKey = {privateKey}\nAddress = 192.0.2.1/32\nDNS = 192.0.2.2\nJc = 2\nJmin = 10\nJmax = 20\nH1 = 12345\nH2 = 23456\nH3 = 34567\nH4 = 45678\n[Peer]\nPublicKey = {publicKey}\nEndpoint = 10.77.0.2:18083\nAllowedIPs = 0.0.0.0/0\n").Single();
                using var awgClient = new LinuxNetworkClient(DBusAddress.Session);
                await awgClient.ConnectAsync(awgProfile, token);
                await awgClient.DisconnectAsync(token);
                await AssertCleanAsync(commands, directory, baseline4, baseline6, token);
                Console.WriteLine("PASS_AWG_VPN_MARKED_UAPI_START_AND_CLEANUP_NO_REMOTE_HANDSHAKE_CLAIM");
            }
            finally
            {
                if (!server.HasExited) serverLifetime.RequestShutdown();
                await server.WaitForExitAsync(token);
                await Task.WhenAll(output, errors);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task KillOwnedCoreAsync(string directory, CancellationToken token)
    {
        var processes = Process.GetProcessesByName("xray");
        try
        {
            var owned = processes.Single(process => File.ReadAllText($"/proc/{process.Id}/cmdline")
                .Contains(Path.Combine(directory, "vpn-"), StringComparison.Ordinal));
            owned.Kill();
            await owned.WaitForExitAsync(token);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static async Task CheckTrafficAsync(CancellationToken token)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var host in new[] { "198.51.100.20", "[2001:db8:77::20]" })
            if (await http.GetStringAsync($"http://{host}:18081/", token) != "VPN_SYNTHETIC") throw new InvalidOperationException("HTTP did not pass through TUN.");
        foreach (var host in new[] { "198.51.100.20", "2001:db8:77::20" })
        {
            var address = IPAddress.Parse(host);
            using var udp = new UdpClient(address.AddressFamily);
            await udp.SendAsync("VPN_UDP"u8.ToArray(), new IPEndPoint(address, 18082), token);
            var reply = await udp.ReceiveAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            if (Encoding.UTF8.GetString(reply.Buffer) != "VPN_UDP") throw new InvalidOperationException("UDP did not pass through TUN.");
        }
    }
    private static async Task WaitIdleAsync(LinuxVpnCoordinator vpn, CancellationToken token)
    {
        while (!vpn.IsIdle) await Task.Delay(50, token);
    }
    private static async Task AssertCleanAsync(Commands commands, string directory, string baseline4, string baseline6, CancellationToken token)
    {
        if (await commands.IpAsync(token, "-4", "-j", "rule", "show") != baseline4 || await commands.IpAsync(token, "-6", "-j", "rule", "show") != baseline6)
            throw new InvalidOperationException("Owned policy rules were not removed.");
        var links = JsonNode.Parse(await commands.IpAsync(token, "-j", "link", "show"))!.AsArray();
        if (links.Any(link => ((string?)link?["ifname"])?.StartsWith("dtn") == true)
            || File.Exists(Path.Combine(directory, "vpn-owner.json")) || Directory.EnumerateDirectories(directory, "vpn-*").Any())
            throw new InvalidOperationException("Owned TUN or private state was not removed.");
        commands.ActiveDns.RemoveWhere(name => !links.Any(link => (string?)link?["ifname"] == name)); // Emulate resolved's link removal notification.
        if (commands.ActiveDns.Count != 0) throw new InvalidOperationException("Per-link DNS not reverted.");
    }
    private sealed class Commands(SyntheticResolved resolved) : ILinuxNetworkCommands
    {
        private readonly LinuxNetworkCommands real = new();
        public bool FailDns { get => resolved.DenyDomain; set => resolved.DenyDomain = value; }
        public HashSet<string> ActiveDns = [];
        public Task<string> IpAsync(CancellationToken token, params string[] arguments) => real.IpAsync(token, arguments);
        public async Task ResolvedAsync(CancellationToken token, params string[] arguments)
        {
            await real.ResolvedAsync(token, arguments);
            if (arguments[0] == "dns") ActiveDns.Add(arguments[1]);
            if (arguments[0] == "revert") ActiveDns.Remove(arguments[1]);
        }
    }

    private sealed class SyntheticResolved : IPathMethodHandler
    {
        public string Path => "/org/freedesktop/resolve1";
        public bool HandlesChildPaths => false;
        public bool DenyDomain, SawDns, SawRootDomain, SawDefaultRoute, SawRevert;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.Request.InterfaceAsString != "org.freedesktop.resolve1.Manager")
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "Synthetic resolved manager only");
                return default;
            }
            var reader = context.Request.GetBodyReader();
            if (reader.ReadInt32() <= 1) throw new InvalidOperationException("DNS must be scoped to TUN's interface index.");
            switch (context.Request.MemberAsString)
            {
                case "SetLinkDNS":
                case "SetLinkDNSEx":
                    var extended = context.Request.MemberAsString == "SetLinkDNSEx";
                    if (context.Request.SignatureAsString != (extended ? "ia(iayqs)" : "ia(iay)")) throw new InvalidOperationException();
                    var dns = reader.ReadArrayStart(DBusType.Struct);
                    var count = 0;
                    while (reader.HasNext(dns))
                    {
                        reader.AlignStruct();
                        if (reader.ReadInt32() != 2 || reader.ReadArrayOfByte().Length != 4) throw new InvalidOperationException();
                        if (extended && (reader.ReadUInt16() != 0 || reader.ReadString() != "")) throw new InvalidOperationException();
                        count++;
                    }
                    SawDns = count == 2;
                    break;
                case "SetLinkDomains":
                    if (DenyDomain) { context.ReplyError("org.freedesktop.resolve1.InjectedFailure", "Synthetic denial"); return default; }
                    var domains = reader.ReadArrayStart(DBusType.Struct);
                    if (!reader.HasNext(domains)) throw new InvalidOperationException();
                    reader.AlignStruct();
                    SawRootDomain = reader.ReadString() == "." && reader.ReadBool();
                    break;
                case "SetLinkDefaultRoute": SawDefaultRoute = reader.ReadBool(); break;
                case "RevertLink": SawRevert = true; break;
                default: context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "Synthetic resolved has no such method"); return default;
            }
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
            return default;
        }
    }
}
