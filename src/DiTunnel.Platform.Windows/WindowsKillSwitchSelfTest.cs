using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Windows;

/// <summary>Destructive-to-connectivity, bounded integration check used only by release/VM validation.</summary>
public static class WindowsKillSwitchSelfTest
{
    public static async Task<int> RunAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        var loopbackIndex = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            .Select(item => item.GetIPProperties().GetIPv4Properties()?.Index ?? 0)
            .FirstOrDefault(index => index > 0);
        if (loopbackIndex == 0) throw new InvalidOperationException("IPv4 loopback interface was not found.");

        // Non-palindromic octets ensure the test also detects accidental network/host byte-order reversal.
        var allowedAddress = IPAddress.Parse("1.0.0.1");
        var blockedAddress = IPAddress.Parse("8.8.4.4");
        var dnsServer = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(item => item.GetIPProperties().DnsAddresses)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            ?? throw new InvalidOperationException("An IPv4 DNS server was not found.");
        var baselineAllowed = await CanConnectAsync(allowedAddress, 443, cancellationToken);
        var baselineBlocked = await CanConnectAsync(blockedAddress, 443, cancellationToken);
        var baselineDns = await CanQueryDnsAsync(dnsServer, cancellationToken);
        if (!baselineAllowed || !baselineBlocked || !baselineDns)
            throw new InvalidOperationException("The self-test requires baseline TCP and DNS connectivity.");

        var controller = new WindowsKillSwitchController();
        bool permittedEndpoint;
        bool unrelatedEndpointBlocked;
        bool dnsBlocked;
        bool restored;
        try
        {
            var configuration = KillSwitchConfiguration.Create([allowedAddress], 443, KillSwitchTransportProtocol.Tcp, false);
            await controller.ActivateAsync(configuration, checked((uint)loopbackIndex), cancellationToken);
            permittedEndpoint = await CanConnectAsync(allowedAddress, 443, cancellationToken);
            unrelatedEndpointBlocked = !await CanConnectAsync(blockedAddress, 443, cancellationToken);
            dnsBlocked = !await CanQueryDnsAsync(dnsServer, cancellationToken);
        }
        finally
        {
            await controller.DeactivateAsync(CancellationToken.None);
        }
        restored = await CanConnectAsync(blockedAddress, 443, cancellationToken);
        var passed = permittedEndpoint && unrelatedEndpointBlocked && dnsBlocked && restored;
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            passed,
            provider = WindowsKillSwitchController.ProviderKey,
            subLayer = WindowsKillSwitchController.SubLayerKey,
            permittedEndpoint,
            unrelatedEndpointBlocked,
            dnsBlocked,
            restored
        }));
        return passed ? 0 : 2;
    }

    private static async Task<bool> CanConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient(address.AddressFamily);
        try { await client.ConnectAsync(address, port, timeout.Token); return true; }
        catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
    }

    private static async Task<bool> CanQueryDnsAsync(IPAddress server, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new UdpClient(server.AddressFamily);
        try
        {
            client.Connect(server, 53);
            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            byte[] query = [
                (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0,
                7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1
            ];
            await client.SendAsync(query, timeout.Token);
            var response = await client.ReceiveAsync(timeout.Token);
            return response.Buffer.Length >= 12 && response.Buffer[0] == (byte)(id >> 8) && response.Buffer[1] == (byte)id;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException) { return false; }
    }
}
