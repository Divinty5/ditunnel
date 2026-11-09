using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class ProbeSocksCredentialsTests
{
    [Fact]
    public void ProbeBuilderRequiresExplicitCredentialsAndNeverChangesTun()
    {
        var profile = XrayProfileConverter.Convert(new("Synthetic", "VLESS", "vless://00000000-0000-4000-8000-000000000001@192.0.2.1:443?security=none"));
        Assert.Throws<ArgumentNullException>(() => profile.Build("192.0.2.1", false));
        var credentials = ProbeSocksCredentials.Create();
        var root = JsonNode.Parse(profile.Build("192.0.2.1", false, enableSocksUdp: false, proxyCredentials: credentials))!;
        var inbound = root["inbounds"]![0]!;
        Assert.Equal("127.0.0.1", inbound["listen"]!.GetValue<string>());
        var settings = inbound["settings"]!;
        Assert.Equal("password", settings["auth"]!.GetValue<string>());
        Assert.False(settings["udp"]!.GetValue<bool>());
        Assert.Equal(credentials.Username, settings["accounts"]![0]!["user"]!.GetValue<string>());
        Assert.Equal(credentials.Password, settings["accounts"]![0]!["pass"]!.GetValue<string>());
        Assert.Equal(profile.Build("192.0.2.1", true), profile.Build("192.0.2.1", true, proxyCredentials: credentials));
        Assert.Null(root["api"]);
    }

    [Fact]
    public void CredentialsRotateAreRfc1929BoundedAndRedactTheirRepresentation()
    {
        var first = ProbeSocksCredentials.Create(); var next = ProbeSocksCredentials.Create();
        Assert.Matches("^[0-9A-F]{32}$", first.Username);
        Assert.Matches("^[0-9A-F]{64}$", first.Password);
        Assert.NotEqual(first.Username, next.Username); Assert.NotEqual(first.Password, next.Password);
        Assert.DoesNotContain(first.Username, first.ToString()); Assert.DoesNotContain(first.Password, first.ToString());
        var proxy = first.CreateProxy(12345);
        var credential = proxy.Credentials!.GetCredential(proxy.Address!, "SOCKS5")!;
        Assert.Equal(first.Username, credential.UserName); Assert.Equal(first.Password, credential.Password);
        Assert.DoesNotContain(first.Password, proxy.Address!.ToString());
    }

    [Theory]
    [InlineData(5, 0)] // No unauthenticated downgrade.
    [InlineData(5, 255)]
    [InlineData(4, 2)]
    public async Task ClientRejectsOtherMethodsAndMalformedVersion(byte version, byte method)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var authentication = ProbeSocksCredentials.Create().AuthenticateAsync(client.GetStream(), deadline.Token);
        var greeting = new byte[3]; await peer.GetStream().ReadExactlyAsync(greeting, deadline.Token);
        Assert.Equal(new byte[] { 5, 1, 2 }, greeting);
        await peer.GetStream().WriteAsync(new byte[] { version, method }, deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => authentication);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 0)]
    public async Task ClientRejectsFailedOrMalformedAuthentication(byte version, byte status)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var credentials = ProbeSocksCredentials.Create();
        var authentication = credentials.AuthenticateAsync(client.GetStream(), deadline.Token);
        var stream = peer.GetStream(); await stream.ReadExactlyAsync(new byte[3], deadline.Token);
        await stream.WriteAsync(new byte[] { 5, 2 }, deadline.Token);
        await stream.ReadExactlyAsync(new byte[3 + credentials.Username.Length + credentials.Password.Length], deadline.Token);
        await stream.WriteAsync(new byte[] { version, status }, deadline.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => authentication);
        Assert.DoesNotContain(credentials.Password, error.ToString());
    }
}
