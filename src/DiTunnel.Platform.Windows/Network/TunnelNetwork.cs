using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DiTunnel.Platform.Windows.Network;

internal sealed class TunnelRouteException() : InvalidOperationException("Контрольный маршрут проходит вне туннеля.");

internal interface ITunnelNetwork
{
    bool HasOtherVpn => false;
    bool IsCorporateDestination(IPAddress address) => false;
    string? InterfaceName(uint index) => null;
    void PrecheckDns();
    PhysicalUplink FindUplink();
    Task<IAsyncDisposable> AcquireServerRouteAsync(System.Net.IPAddress address, PhysicalUplink uplink, CancellationToken token);
    IReadOnlyList<NetworkRoute> Routes();
    bool AddRoute(NetworkRoute route);
    void RemoveRoute(NetworkRoute route);
    uint FindTunnel(string name);
    Task AddAddressAsync(uint index, string address, byte prefix, CancellationToken token);
    void SetMetric(uint index, bool ipv6);
    bool InstallDns(uint tunnelIndex, string tunnelName, string[] servers);
    void FlushDns();
    void CleanupDns();
    IReadOnlyList<string> CachedNames();
    Task<IPAddress[]> ResolveAsync(string name, CancellationToken token);
    Task ProbeAsync(uint tunnelIndex, bool ipv6, CancellationToken token);
    bool UsesTunnel(uint tunnelIndex, IPAddress address) => false;
    void BindTunnelSocket(Socket socket, uint tunnelIndex) => throw new NotSupportedException();
}

internal sealed class TunnelNetwork : ITunnelNetwork
{
    internal const string Ipv4Address = "172.31.255.1";
    internal const string Ipv6Address = "fd52:d17::1";

    public string? InterfaceName(uint index) => NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(item => item.Supports(NetworkInterfaceComponent.IPv4)
            && item.GetIPProperties().GetIPv4Properties()?.Index == index)?.Name;
    public bool HasOtherVpn => WindowsVpnCoexistence.Interfaces("DiTunnel").Count != 0;
    public bool IsCorporateDestination(IPAddress address)
    {
        var interfaces = WindowsVpnCoexistence.Interfaces("DiTunnel");
        return WindowsNetworkApi.Routes().Any(route => interfaces.Contains(route.InterfaceIndex)
            && WindowsVpnCoexistence.CoversSpecificRoute(route.Prefix, address));
    }
    private WindowsInterfaceDns? interfaceDns;
    public void PrecheckDns() => WindowsDnsPolicy.Precheck();
    public PhysicalUplink FindUplink() => WindowsNetworkApi.FindUplink();
    public async Task<IAsyncDisposable> AcquireServerRouteAsync(System.Net.IPAddress address, PhysicalUplink uplink, CancellationToken token) =>
        await WindowsProbeRouteBypass.AcquireRouteAsync(address, uplink, token);
    public IReadOnlyList<NetworkRoute> Routes() => WindowsNetworkApi.Routes();
    public bool AddRoute(NetworkRoute route) => WindowsNetworkApi.AddRoute(route);
    public void RemoveRoute(NetworkRoute route) => WindowsNetworkApi.RemoveRoute(route);
    public uint FindTunnel(string name) => (uint)(NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(n => n.Name == name)?.GetIPProperties().GetIPv4Properties()?.Index ?? 0);
    public Task AddAddressAsync(uint index, string address, byte prefix, CancellationToken token) => WindowsNetworkApi.AddAddressAsync(index, address, prefix, token);
    public void SetMetric(uint index, bool ipv6) => WindowsNetworkApi.SetTunnelMetric(index, ipv6);
    public bool InstallDns(uint tunnelIndex, string tunnelName, string[] servers) =>
        WindowsDnsPolicy.Install(servers, () => interfaceDns = WindowsInterfaceDns.Configure(tunnelIndex, tunnelName, servers));
    public void FlushDns() => WindowsDnsPolicy.Flush();
    public void CleanupDns()
    {
        try { WindowsDnsPolicy.CleanupOwned(); }
        finally { interfaceDns?.Dispose(); }
    }
    public IReadOnlyList<string> CachedNames() => WindowsDnsPolicy.CachedNames();
    public Task<IPAddress[]> ResolveAsync(string name, CancellationToken token) =>
        Dns.GetHostAddressesAsync(name, token).WaitAsync(TimeSpan.FromSeconds(2), token);
    public bool UsesTunnel(uint tunnelIndex, IPAddress address)
    {
        try { return tunnelIndex != 0 && WindowsNetworkApi.BestInterface(address) == tunnelIndex; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public void BindTunnelSocket(Socket socket, uint tunnelIndex)
    {
        const SocketOptionName unicastInterface = (SocketOptionName)31;
        var ipv4 = socket.AddressFamily == AddressFamily.InterNetwork;
        var index = (int)tunnelIndex;
        socket.SetSocketOption(ipv4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6, unicastInterface,
            ipv4 ? IPAddress.HostToNetworkOrder(index) : index);
        socket.Bind(new IPEndPoint(IPAddress.Parse(ipv4 ? Ipv4Address : Ipv6Address), 0));
    }
    public async Task ProbeAsync(uint tunnelIndex, bool ipv6, CancellationToken token)
    {
        var address = IPAddress.Parse(ipv6 ? "2606:4700:4700::1111" : "1.1.1.1");
        if (WindowsNetworkApi.BestInterface(address) != tunnelIndex) throw new TunnelRouteException();
        using var probe = new TcpClient(address.AddressFamily);
        await probe.ConnectAsync(address, 443, token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), token);
    }
}

internal interface ITunnelCore
{
    bool HasExited { get; }
    int ExitCode { get; }
    Task StopAsync();
}

internal sealed class TunnelCore(System.Diagnostics.Process process) : ITunnelCore
{
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public async Task StopAsync()
    {
        try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        finally { process.Dispose(); }
    }
}
