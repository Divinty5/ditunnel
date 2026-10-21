using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Linux.Network;

[SupportedOSPlatform("linux")]
internal static class LinuxProtectedSwitchSmoke
{
    public static async Task RunAsync()
    {
        if (File.ReadAllText("/sys/class/dmi/id/product_uuid").Trim() != "26de44ca-6663-4ad0-99d1-8ac3913b6d0f"
            || File.Exists("/run/ditunnel/vpn-owner.json")) throw new InvalidOperationException("Expected idle owned Ubuntu VM and synthetic peer.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        var vless = ProfileParser.Parse("vless://11111111-1111-1111-1111-111111111111@10.77.0.2:18080?security=none&type=tcp").Single();
        var hostname = ProfileParser.Parse("vless://11111111-1111-1111-1111-111111111111@switch.ditunnel.test:18080?security=none&type=tcp").Single();
        var ss = ProfileParser.Parse("ss://" + Convert.ToBase64String(Encoding.ASCII.GetBytes("aes-128-gcm:VM_SYNTHETIC")) + "@10.77.0.2:18083").Single();
        try
        {
            foreach (var ipv4 in new[] { true, false })
            {
                var policy = new LinuxVpnPolicy(SplitTunnelMode.ProxyAll, [], [], true, false, Ipv4Only: ipv4);
                using var client = new LinuxNetworkClient(policy: () => policy);
                await client.ConnectAsync(vless, token);
                await DataAsync(token);
                var journal = await File.ReadAllTextAsync("/run/ditunnel/vpn-owner.json", token);
                foreach (var profile in new[] { ss, vless })
                {
                    var result = await client.ProbeAsync(profile, token);
                    if (result.Milliseconds is null || !result.Message.StartsWith("HTTP ·", StringComparison.Ordinal)) throw new Exception("Protected nonactive probe failed");
                }
                await AssertMarkedOutsiderBlockedAsync(token);
                foreach (var profile in new[] { ss, hostname })
                {
                    await client.SwitchAsync(profile, token);
                    if (!client.IsNetworkProtectionActive || await File.ReadAllTextAsync("/run/ditunnel/vpn-owner.json", token) != journal)
                        throw new Exception("Protected handoff lost its firewall owner");
                    await DataAsync(token);
                }
                Console.WriteLine("PASS_PROTECTED_SERVER_AND_HOSTNAME_SWITCH_AND_NONACTIVE_HTTP_PROBES_IPV4ONLY_" + ipv4);
                await KillOwnedCoreAsync(token);
                for (var i = 0; i < 100 && client.Status.State != VpnConnectionState.Error; i++) await Task.Delay(100, token);
                if (client.Status.State != VpnConnectionState.Error || !client.IsNetworkProtectionActive) throw new Exception("Core failure did not retain KS");
                await AssertMarkedOutsiderBlockedAsync(token);
                await client.ConnectAsync(hostname, token);
                await DataAsync(token);
                Console.WriteLine("PASS_SAME_CLIENT_RECONNECT_WITH_RETAINED_KS_AFTER_CORE_FAILURE_IPV4ONLY_" + ipv4);
                policy = policy with { KillSwitch = false };
                await client.SwitchAsync(ss, token);
                if (client.IsNetworkProtectionActive) throw new Exception("Disabling KS did not apply");
                await DataAsync(token);
                policy = policy with { KillSwitch = true };
                await client.SwitchAsync(vless, token);
                if (!client.IsNetworkProtectionActive) throw new Exception("Enabling KS did not apply");
                await client.DisconnectAsync(token);
                await client.RestoreNetworkAsync(token);
                if (File.Exists("/run/ditunnel/vpn-owner.json")) throw new Exception("Cleanup incomplete");
                Console.WriteLine("PASS_PROTECTED_POLICY_CHANGE_DISCONNECT_RESTORE_IPV4ONLY_" + ipv4);
            }
        }
        catch (Exception error) { Console.Error.WriteLine("PROTECTED_SWITCH_FAILURE: " + error); throw; }
        finally
        {
            using var recovery = new LinuxNetworkClient();
            for (var attempt = 0; ; attempt++)
            {
                try { await recovery.RestoreNetworkAsync(CancellationToken.None); break; }
                catch (Tmds.DBus.Protocol.DBusErrorReplyException) when (attempt < 50) { await Task.Delay(100); }
            }
        }
    }

    private static async Task DataAsync(CancellationToken token)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        if (await http.GetStringAsync("http://198.51.100.20:18081/", token) != "VPN") throw new Exception("Expected synthetic VPN data path");
    }

    private static async Task AssertMarkedOutsiderBlockedAsync(CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetRawSocketOption(1, 36, BitConverter.GetBytes(LinuxRouteLease.Mark));
        try
        {
            await socket.ConnectAsync(IPAddress.Parse("10.77.0.2"), 18083, token).AsTask().WaitAsync(TimeSpan.FromSeconds(1), token);
        }
        catch (TimeoutException) { return; }
        catch (SocketException) { return; }
        throw new Exception("Root socket outside the host cgroup bypassed KS");
    }

    private static async Task KillOwnedCoreAsync(CancellationToken token)
    {
        var directory = Directory.GetDirectories("/run/ditunnel", "vpn-*").Single();
        var expected = new[] { "/usr/lib/ditunnel/runtime/xray", "run", "-config", Path.Combine(directory, "xray.json") };
        var pids = Directory.GetDirectories("/proc").Where(path => int.TryParse(Path.GetFileName(path), out _));
        foreach (var path in pids)
        {
            string[] command;
            try { command = (await File.ReadAllTextAsync(Path.Combine(path, "cmdline"), token)).TrimEnd('\0').Split('\0'); }
            catch (IOException) { continue; }
            if (!command.SequenceEqual(expected)) continue;
            using var process = Process.GetProcessById(int.Parse(Path.GetFileName(path)));
            process.Kill();
            await process.WaitForExitAsync(token);
            return;
        }
        throw new Exception("Owned synthetic core not found");
    }
}
