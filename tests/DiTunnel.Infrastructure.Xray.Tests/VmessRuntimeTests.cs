using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class VmessRuntimeTests
{
    [Theory]
    [InlineData("tcp")]
    [InlineData("ws")]
    [InlineData("httpupgrade")]
    [InlineData("grpc")]
    public async Task ActualXrayVmessTransfersHttpThroughLoopbackServer(string transport)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var runtime = Path.Combine(root.FullName, ".tools", "xray", "26.3.27", "windows-x64", "xray.exe");
        Assert.True(File.Exists(runtime));
        var directory = Path.Combine(Path.GetTempPath(), "ditunnel-vmess-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var http = new TcpListener(IPAddress.Loopback, 0);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        http.Start(); reservation.Start();
        var httpPort = ((IPEndPoint)http.LocalEndpoint).Port;
        var vmessPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        try
        {
            const string id = "00000000-0000-0000-0000-000000000001";
            var stream = new JsonObject { ["network"] = transport, ["security"] = "none" };
            if (transport is "ws" or "httpupgrade") stream[transport == "ws" ? "wsSettings" : "httpupgradeSettings"] = new JsonObject { ["path"] = "/proxy" };
            if (transport == "grpc") stream["grpcSettings"] = new JsonObject { ["serviceName"] = "proxy" };
            var server = new JsonObject
            {
                ["log"] = new JsonObject { ["loglevel"] = "none" },
                ["inbounds"] = new JsonArray(new JsonObject
                {
                    ["listen"] = "127.0.0.1", ["port"] = vmessPort, ["protocol"] = "vmess",
                    ["settings"] = new JsonObject { ["clients"] = new JsonArray(new JsonObject { ["id"] = id }) },
                    ["streamSettings"] = stream
                }),
                ["outbounds"] = new JsonArray(new JsonObject
                {
                    ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = "127.0.0.1:" + httpPort }
                })
            };
            var path = Path.Combine(directory, "server.json");
            await File.WriteAllTextAsync(path, server.ToJsonString(), timeout.Token);
            await using var manager = new XrayProcessManager(new XrayOptions
            {
                ExecutablePath = runtime, WorkingDirectory = Path.GetDirectoryName(runtime)!,
                ValidateConfigurationBeforeStart = false, ShutdownTimeout = TimeSpan.FromMilliseconds(250)
            });
            await manager.StartAsync(path, timeout.Token);
            var response = Task.Run(async () =>
            {
                using var connection = await http.AcceptTcpClientAsync(timeout.Token);
                var network = connection.GetStream();
                var buffer = new byte[4096];
                var request = "";
                while (!request.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var count = await network.ReadAsync(buffer, timeout.Token);
                    if (count == 0) throw new IOException("Request closed prematurely");
                    request += Encoding.ASCII.GetString(buffer, 0, count);
                }
                Assert.StartsWith("HEAD / HTTP/1.1", request);
                await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            });
            var json = JsonSerializer.Serialize(new { add = "127.0.0.1", port = vmessPort, id, aid = "0", scy = "auto", net = transport, tls = "", path = "/proxy" });
            var profile = Assert.Single(ProfileParser.Parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))));
            var configuration = XrayProfileConverter.Convert(profile);
            var milliseconds = await XrayServerProbe.MeasureAsync(configuration, IPAddress.Loopback, runtime,
                Path.Combine(directory, "probe.json"), timeout.Token, useHttps: false, probeTimeout: TimeSpan.FromSeconds(8));
            Assert.True(milliseconds >= 0);
            await response;
        }
        finally
        {
            timeout.Cancel();
            // The generated path is unique and restricted to the OS temporary directory.
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.StartsWith(tempRoot, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, true);
        }
    }
}
