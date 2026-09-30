using System.Diagnostics;
using System.Text.Json;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class AmneziaWgProfileConverterTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static ImportedProfile Profile(string extra = "") => new("Synthetic AWG", "AmneziaWG", $"""
        [Interface]
        PrivateKey = {Key}
        Address = 192.0.2.2/32, 2001:db8::2/128
        DNS = 192.0.2.53, example.com
        MTU = 1280
        Jc = 2
        Jmin = 10
        Jmax = 20
        S1 = 16
        S2 = 32
        H1 = 12345
        H2 = 23456
        H3 = 34567
        H4 = 45678
        {extra}
        [Peer]
        PublicKey = {Key}
        PresharedKey = {Key}
        Endpoint = vpn.example.com:51820
        AllowedIPs = 0.0.0.0/0, ::/0
        PersistentKeepalive = 25
        """);

    [Fact]
    public void RuntimePreservesKeysObfuscationAndCryptokeyRoutes()
    {
        var configuration = AmneziaWgProfileConverter.Convert(Profile());
        using var runtime = JsonDocument.Parse(configuration.BuildRuntimeConfiguration("192.0.2.1", "user", "password", "ready", "192.0.2.5"));
        var root = runtime.RootElement;
        var uapi = root.GetProperty("uapi").GetString()!;
        Assert.Contains("private_key=" + string.Concat(Enumerable.Repeat("01", 32)) + "\n", uapi);
        Assert.Contains("endpoint=192.0.2.1:51820\n", uapi);
        Assert.Contains("jc=2\n", uapi);
        Assert.Contains("allowed_ip=0.0.0.0/0\nallowed_ip=::/0\n", uapi);
        Assert.Contains("persistent_keepalive_interval=25\n", uapi);
        Assert.Equal("192.0.2.5", root.GetProperty("sourceAddress").GetString());
        Assert.Equal("192.0.2.53", Assert.Single(root.GetProperty("dns").EnumerateArray()).GetString());
        Assert.Equal(2, root.GetProperty("addresses").GetArrayLength());
        Assert.DoesNotContain(Key, configuration.ToString());
    }

    [Theory]
    [InlineData(SplitTunnelMode.ProxyAll)]
    [InlineData(SplitTunnelMode.BypassSelected)]
    [InlineData(SplitTunnelMode.ProxySelected)]
    public void SocksHopStaysOnLoopbackWhileEndpointMetadataAndRoutingArePreserved(SplitTunnelMode mode)
    {
        var configuration = AmneziaWgProfileConverter.Convert(Profile()).CreateProxyConfiguration(12345, "synthetic-user", "synthetic-password");
        var policy = new SplitTunnelPolicy(mode, ["example.com"], []);
        using var document = JsonDocument.Parse(configuration.Build("192.0.2.1", true, splitTunnel: policy, outboundSourceAddress: "192.0.2.5", blockAds: true));
        var outbounds = document.RootElement.GetProperty("outbounds").EnumerateArray().ToArray();
        var proxy = Assert.Single(outbounds, item => item.GetProperty("tag").GetString() == "proxy");
        var server = proxy.GetProperty("settings").GetProperty("servers")[0];
        Assert.Equal("127.0.0.1", server.GetProperty("address").GetString());
        Assert.Equal(12345, server.GetProperty("port").GetInt32());
        Assert.Equal("synthetic-user", server.GetProperty("users")[0].GetProperty("user").GetString());
        Assert.False(proxy.TryGetProperty("sendThrough", out _));
        Assert.Equal("vpn.example.com", configuration.ServerHost);
        Assert.Equal(51820, configuration.ServerPort);
        Assert.Equal(KillSwitchTransportProtocol.Udp, configuration.ServerTransport);
        Assert.Contains(outbounds, item => item.GetProperty("protocol").GetString() == "blackhole");
        if (mode != SplitTunnelMode.ProxyAll)
            Assert.Equal("192.0.2.5", Assert.Single(outbounds, item => item.GetProperty("tag").GetString() == "direct").GetProperty("sendThrough").GetString());
    }

    [Fact]
    public void RejectsIpv6OuterEndpointUntilWindowsHostSupportsIt()
        => Assert.Throws<NotSupportedException>(() => AmneziaWgProfileConverter.Convert(Profile()).BuildRuntimeConfiguration("2001:db8::1", "user", "password"));

    [Theory]
    [InlineData(SplitTunnelMode.ProxyAll)]
    [InlineData(SplitTunnelMode.ProxySelected)]
    [InlineData(SplitTunnelMode.BypassSelected)]
    public void RulesAlwaysKeepTheProfilesDnsResolversInsideAwg(SplitTunnelMode mode)
    {
        var awg = AmneziaWgProfileConverter.Convert(Profile());
        using var document = JsonDocument.Parse(awg.CreateProxyConfiguration(12345, "user", "password").Build("192.0.2.1", true,
            splitTunnel: new SplitTunnelPolicy(mode, ["192.0.2.53"], []), dnsServers: awg.DnsServers));
        var rule = document.RootElement.GetProperty("routing").GetProperty("rules")[0];
        Assert.Equal("192.0.2.53", Assert.Single(rule.GetProperty("ip").EnumerateArray()).GetString());
        Assert.Equal("proxy", rule.GetProperty("outboundTag").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("S3 = 16\nS4 = 16\nHeaderProtectionKey = KEY\nContentPaddingAddition = 10-20")]
    public async Task GeneratedConfigurationIsAcceptedByPinnedKernelWithoutOpeningSockets(string extra)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var runtime = Path.Combine(root.FullName, ".tools", "amneziawg", "1", "windows-x64", "ditunnel-awg.exe");
        // Source-only checkouts run the converter tests; Build-AmneziaWG -Test prepares the native checks.
        if (!File.Exists(runtime)) return;
        var configuration = AmneziaWgProfileConverter.Convert(Profile(extra.Replace("KEY", Key)));
        var directory = Path.Combine(root.FullName, "artifacts", "awg-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "amneziawg.json");
        try
        {
            await File.WriteAllTextAsync(path, configuration.BuildRuntimeConfiguration("192.0.2.1", new string('u', 32), new string('p', 64)));
            var info = new ProcessStartInfo(runtime) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-config", path, "-validate" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            Assert.Equal("VALID", (await stdout).Trim());
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task RuntimeStopsAndClosesLoopbackListenerWhenOwnerExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var runtime = Path.Combine(root.FullName, ".tools", "amneziawg", "1", "windows-x64", "ditunnel-awg.exe");
        if (!File.Exists(runtime)) return;
        var directory = Path.Combine(root.FullName, "artifacts", "awg-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "amneziawg.json");
        var ready = Path.Combine(directory, "amneziawg.ready");
        Process? owner = null;
        Process? bridge = null;
        try
        {
            var profile = Profile() with { Content = Profile().Content.Replace("PersistentKeepalive = 25", "") };
            await File.WriteAllTextAsync(path, AmneziaWgProfileConverter.Convert(profile).BuildRuntimeConfiguration("127.0.0.1", new string('u', 32), new string('p', 64), ready, "127.0.0.1"));
            var ownerInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }) ownerInfo.ArgumentList.Add(argument);
            owner = Process.Start(ownerInfo)!;
            var info = new ProcessStartInfo(runtime) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-config", path, "-owner", owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }) info.ArgumentList.Add(argument);
            bridge = Process.Start(info)!;
            var error = bridge.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var output = await bridge.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.NotNull(output);
            Assert.StartsWith("READY_", output);
            var port = int.Parse(output[6..], System.Globalization.CultureInfo.InvariantCulture);
            using var socket = new System.Net.Sockets.TcpClient();
            await socket.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
            Assert.True(File.Exists(ready));
            owner.Kill(); await owner.WaitForExitAsync(timeout.Token);
            await bridge.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, bridge.ExitCode);
            Assert.False(File.Exists(ready));
            Assert.Equal("", await error);
            using var reconnect = new System.Net.Sockets.TcpClient();
            await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(() => reconnect.ConnectAsync(System.Net.IPAddress.Loopback, port));
        }
        finally
        {
            foreach (var process in new[] { bridge, owner })
                if (process is not null) { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } process.Dispose(); }
            foreach (var file in new[] { path, ready, ready + ".tmp" }) if (File.Exists(file)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
