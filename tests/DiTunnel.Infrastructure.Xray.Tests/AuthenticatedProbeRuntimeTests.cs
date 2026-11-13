using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Android;

namespace DiTunnel.Infrastructure.Xray.Tests;

[Collection("Xray runtime")]
public sealed class AuthenticatedProbeRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualXrayRejectsOutsidersButForwardsAuthenticatedProfile(bool androidBatch)
    {
        var runtime = FindRuntime();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        const string id = "00000000-0000-4000-8000-000000000001";
        var upstreamPort = ReservePort();
        var server = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "none" },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["listen"] = "127.0.0.1", ["port"] = upstreamPort, ["protocol"] = "vless",
                ["settings"] = new JsonObject { ["decryption"] = "none",
                    ["clients"] = new JsonArray(new JsonObject { ["id"] = id }) }
            }),
            ["outbounds"] = new JsonArray(new JsonObject
            {
                ["protocol"] = "freedom", ["tag"] = "direct",
                ["settings"] = XrayRuntimeCollection.AllowTestHttpEndpoint(targetPort)
            })
        };
        await using var upstream = Manager(runtime);
        await upstream.StartFromJsonAsync(server.ToJsonString(), token);
        var profile = XrayProfileConverter.Convert(new ImportedProfile("Synthetic", "VLESS", $"vless://{id}@127.0.0.1:{upstreamPort}?security=none"));
        var first = new AndroidProbeOutbound(profile, "127.0.0.1", ReservePort());
        var second = new AndroidProbeOutbound(profile, "127.0.0.1", ReservePort());
        var config = androidBatch ? AndroidProbeBatchConfiguration.Build([first, second])
            : profile.Build("127.0.0.1", false, first.Port, enableSocksUdp: false, proxyCredentials: first.Credentials);
        await using var proxy = Manager(runtime);
        var logs = new List<string>(); proxy.LogReceived += entry => { lock (logs) logs.Add(entry.Message); };
        await proxy.StartFromJsonAsync(config, token);
        await SocksTcpProbe.WaitForListenerAsync(first.Port, first.Credentials, () => proxy.IsRunning, token);

        await RejectNoAuth(first.Port, token);
        await RejectSocks4(first.Port, targetPort, token);
        await RejectPassword(first.Port, first.Credentials.Username, "wrong-password", token);
        await RejectPassword(first.Port, "wrong-user", first.Credentials.Password, token);
        await RejectPassword(first.Port, second.Credentials.Username, second.Credentials.Password, token);
        if (androidBatch)
        {
            await RejectNoAuth(second.Port, token);
            await RejectPassword(second.Port, first.Credentials.Username, first.Credentials.Password, token);
            await SocksTcpProbe.WaitForListenerAsync(second.Port, second.Credentials, () => proxy.IsRunning, token);
        }
        Assert.False(target.Pending());
        var serve = Task.Run(async () =>
        {
            using var connection = await target.AcceptTcpClientAsync(token);
            using var reader = new StreamReader(connection.GetStream(), Encoding.ASCII, leaveOpen: true);
            Assert.Equal("GET /synthetic-exit HTTP/1.1", await reader.ReadLineAsync(token));
            while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
            await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"), token);
        }, token);
        using HttpMessageHandler handler = androidBatch
            ? new SocketsHttpHandler { Proxy = first.Credentials.CreateProxy(first.Port), UseProxy = true }
            : new HttpClientHandler { Proxy = first.Credentials.CreateProxy(first.Port), UseProxy = true };
        using var http = new HttpClient(handler);
        Assert.Equal("OK", await http.GetStringAsync($"http://127.0.0.1:{targetPort}/synthetic-exit", token));
        await serve;
        await proxy.StopAsync(token);
        // Restart on the same port: an earlier pair is not valid for a new invocation.
        var next = new AndroidProbeOutbound(profile, "127.0.0.1", first.Port);
        await proxy.StartFromJsonAsync(profile.Build("127.0.0.1", false, first.Port, enableSocksUdp: false, proxyCredentials: next.Credentials), token);
        await SocksTcpProbe.WaitForListenerAsync(next.Port, next.Credentials, () => proxy.IsRunning, token);
        await RejectPassword(next.Port, first.Credentials.Username, first.Credentials.Password, token);
        lock (logs)
        {
            Assert.DoesNotContain(logs, line => line.Contains(first.Credentials.Password, StringComparison.Ordinal));
            Assert.DoesNotContain(logs, line => line.Contains(first.Credentials.Username, StringComparison.Ordinal));
        }
    }

    private static async Task RejectNoAuth(int port, CancellationToken token)
    {
        using var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, port, token);
        await peer.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, token);
        var reply = new byte[2]; await peer.GetStream().ReadExactlyAsync(reply, token);
        Assert.Equal(new byte[] { 5, 255 }, reply);
    }
    private static async Task RejectPassword(int port, string username, string password, CancellationToken token)
    {
        using var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, port, token);
        var stream = peer.GetStream();
        // Offering both methods must not select noauth.
        await stream.WriteAsync(new byte[] { 5, 2, 0, 2 }, token);
        var reply = new byte[2]; await stream.ReadExactlyAsync(reply, token);
        Assert.Equal(new byte[] { 5, 2 }, reply);
        await stream.WriteAsync(new byte[] { 1, (byte)username.Length }.Concat(Encoding.ASCII.GetBytes(username))
            .Concat(new byte[] { (byte)password.Length }).Concat(Encoding.ASCII.GetBytes(password)).ToArray(), token);
        await stream.ReadExactlyAsync(reply, token); Assert.Equal(new byte[] { 1, 255 }, reply);
    }
    private static async Task RejectSocks4(int port, int targetPort, CancellationToken token)
    {
        using var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, port, token);
        await peer.GetStream().WriteAsync(new byte[] { 4, 1, (byte)(targetPort >> 8), (byte)targetPort, 127, 0, 0, 1, 0 }, token);
        var reply = new byte[8]; await peer.GetStream().ReadExactlyAsync(reply, token);
        Assert.Equal(91, reply[1]);
    }
    private static int ReservePort()
    { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static XrayProcessManager Manager(string runtime) => new(new XrayOptions
    { ExecutablePath = runtime, WorkingDirectory = Path.GetDirectoryName(runtime)!, ValidateConfigurationBeforeStart = false,
        StartupGracePeriod = TimeSpan.FromMilliseconds(100), ShutdownTimeout = TimeSpan.FromMilliseconds(100) });
    private static string FindRuntime()
    {
        var configured = Environment.GetEnvironmentVariable("DITUNNEL_TEST_XRAY");
        if (configured is not null) { Assert.True(File.Exists(configured)); return configured; }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var runtime = Path.Combine(root.FullName, ".tools", "xray", "26.6.27", "windows-x64", "xray.exe");
        Assert.True(OperatingSystem.IsWindows() && File.Exists(runtime), "Set DITUNNEL_TEST_XRAY to the pinned local native runtime.");
        return runtime;
    }
}
