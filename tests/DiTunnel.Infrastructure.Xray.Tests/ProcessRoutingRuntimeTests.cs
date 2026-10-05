using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace DiTunnel.Infrastructure.Xray.Tests;

[Collection("Xray runtime")]
public sealed class ProcessRoutingRuntimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualXrayRoutesOnlyTheMatchingWindowsExecutable(bool match)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var runtime = Path.Combine(root.FullName, ".tools", "xray", "26.3.27", "windows-x64", "xray.exe");
        Assert.True(File.Exists(runtime));
        var directory = Path.Combine(Path.GetTempPath(), "ditunnel-process-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var selected = new TcpListener(IPAddress.Loopback, 0);
        using var fallback = new TcpListener(IPAddress.Loopback, 0);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        selected.Start(); fallback.Start(); reservation.Start();
        var proxyPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        JsonObject Outbound(string tag, TcpListener listener) => new()
        {
            ["tag"] = tag, ["protocol"] = "freedom",
            ["settings"] = new JsonObject { ["redirect"] = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port }
        };
        try
        {
            var configuration = new JsonObject
            {
                ["log"] = new JsonObject { ["loglevel"] = "none" },
                ["inbounds"] = new JsonArray(new JsonObject
                {
                    ["listen"] = "127.0.0.1", ["port"] = proxyPort, ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false }
                }),
                ["outbounds"] = new JsonArray(Outbound("fallback", fallback), Outbound("selected", selected)),
                ["routing"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject
                {
                    ["process"] = new JsonArray(match ? Environment.ProcessPath! : "ditunnel-does-not-exist.exe"),
                    ["outboundTag"] = "selected"
                }) }
            };
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, configuration.ToJsonString(), timeout.Token);
            await using var manager = new XrayProcessManager(new XrayOptions
            {
                ExecutablePath = runtime, WorkingDirectory = Path.GetDirectoryName(runtime)!,
                ValidateConfigurationBeforeStart = false, ShutdownTimeout = TimeSpan.FromMilliseconds(250)
            });
            await manager.StartAsync(path, timeout.Token);
            using var client = new TcpClient();
            // Wait for the SOCKS listener without changing any adapter, route or WFP filter.
            while (true)
            {
                try { await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token); break; }
                catch (SocketException) { await Task.Delay(50, timeout.Token); }
            }
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var negotiation = new byte[2];
            await stream.ReadExactlyAsync(negotiation, timeout.Token);
            Assert.Equal(new byte[] { 5, 0 }, negotiation);
            await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, 1, 187 }, timeout.Token);
            var reply = new byte[10];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            Assert.Equal(0, reply[1]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("routing-proof"), timeout.Token);
            using var accepted = await (match ? selected : fallback).AcceptTcpClientAsync(timeout.Token);
            var payload = new byte[13];
            await accepted.GetStream().ReadExactlyAsync(payload, timeout.Token);
            Assert.Equal("routing-proof", Encoding.ASCII.GetString(payload));
            Assert.False((match ? fallback : selected).Pending());
        }
        finally
        {
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
