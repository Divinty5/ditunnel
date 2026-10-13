using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Infrastructure.Xray.Tests;

[Collection("Xray runtime")]
public sealed class Ipv4DnsRuntimeTests
{
    [Fact]
    public async Task ActualXrayReturnsAAndEmptyAaaaWithoutQueryingIpv6Upstream()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(root.FullName, ".tools/xray/26.3.27/windows-x64/xray.exe")
            : Path.Combine(root.FullName, ".tools/linux-runtime/linux-x64/xray");
        Assert.True(File.Exists(executable));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        var upstreamPort = ((IPEndPoint)upstream.Client.LocalEndPoint!).Port;
        reservation.Close();
        var types = new ConcurrentBag<ushort>();
        var directory = Path.Combine(Path.GetTempPath(), "DiTunnel-ipv4-dns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        var responder = Task.Run(async () =>
        {
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var request = await upstream.ReceiveAsync(timeout.Token);
                    var type = BinaryPrimitives.ReadUInt16BigEndian(request.Buffer.AsSpan(request.Buffer.Length - 4));
                    types.Add(type);
                    var response = request.Buffer.ToList();
                    response[2] = 0x81; response[3] = 0x80; response[7] = 1;
                    response.AddRange([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 127, 0, 0, 23]);
                    await upstream.SendAsync(response.ToArray(), request.RemoteEndPoint, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        try
        {
            var profile = XrayProfileConverter.Convert(new ImportedProfile("Test", "VLESS",
                "vless://11111111-1111-1111-1111-111111111111@192.0.2.1:443?security=none")).WithIpv4Only(true);
            var config = JsonNode.Parse(profile.Build("192.0.2.1", true))!;
            // Exercise production DNS rules with loopback sockets, without a TUN adapter.
            config["inbounds"] = new JsonArray(new JsonObject
            {
                ["protocol"] = "dokodemo-door", ["tag"] = "tun", ["listen"] = "127.0.0.1", ["port"] = port,
                ["settings"] = new JsonObject { ["address"] = "127.0.0.1", ["port"] = 53, ["network"] = "udp" }
            });
            config["outbounds"]![0] = new JsonObject { ["tag"] = "proxy", ["protocol"] = "freedom", ["settings"] = new JsonObject() };
            config["dns"]!["servers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = upstreamPort });
            config["log"]!["loglevel"] = "none";
            await File.WriteAllTextAsync(path, config.ToJsonString(), timeout.Token);
            await using var manager = new XrayProcessManager(new XrayOptions
            { ExecutablePath = executable, WorkingDirectory = Path.GetDirectoryName(executable)!, ShutdownTimeout = TimeSpan.FromMilliseconds(250) });
            await manager.StartAsync(path, timeout.Token);
            async Task<byte[]> Query(ushort type)
            {
                var query = new List<byte> { 0x12, (byte)type, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
                foreach (var label in "example.test".Split('.')) { query.Add((byte)label.Length); query.AddRange(Encoding.ASCII.GetBytes(label)); }
                query.AddRange([0, 0, (byte)type, 0, 1]);
                using var client = new UdpClient();
                await client.SendAsync(query.ToArray(), new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
                return (await client.ReceiveAsync(timeout.Token)).Buffer;
            }
            var a = await Query(1);
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(6)));
            Assert.Equal(new byte[] { 127, 0, 0, 23 }, a[^4..]);
            var aaaa = await Query(28);
            Assert.Equal(0, aaaa[3] & 15);
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(aaaa.AsSpan(6)));
            Assert.Contains((ushort)1, types);
            Assert.DoesNotContain((ushort)28, types);
        }
        finally
        {
            timeout.Cancel(); await responder;
            File.Delete(path); Directory.Delete(directory);
        }
    }
}
