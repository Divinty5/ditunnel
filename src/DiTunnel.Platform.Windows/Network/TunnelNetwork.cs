using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DiTunnel.Platform.Windows.Network;

internal sealed class TunnelRouteException() : InvalidOperationException("Контрольный маршрут проходит вне туннеля.");

internal interface ITunnelNetwork
{
    void PrecheckDns();
    PhysicalUplink FindUplink();
    Task<IAsyncDisposable> AcquireServerRouteAsync(System.Net.IPAddress address, PhysicalUplink uplink, CancellationToken token);
    IReadOnlyList<NetworkRoute> Routes();
    bool AddRoute(NetworkRoute route);
    void RemoveRoute(NetworkRoute route);
    uint FindTunnel(string name);
    Task AddAddressAsync(uint index, string address, byte prefix, CancellationToken token);
    void SetMetric(uint index, bool ipv6);
    void InstallDns(string[] servers);
    void FlushDns();
    void CleanupDns();
    IReadOnlyList<string> CachedNames();
    Task<IPAddress[]> ResolveAsync(string name, CancellationToken token);
    Task ProbeAsync(uint tunnelIndex, CancellationToken token);
}

internal sealed class TunnelNetwork : ITunnelNetwork
{
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
    public void InstallDns(string[] servers) => WindowsDnsPolicy.Install(servers);
    public void FlushDns() => WindowsDnsPolicy.Flush();
    public void CleanupDns() => WindowsDnsPolicy.CleanupOwned();
    public IReadOnlyList<string> CachedNames() => WindowsDnsPolicy.CachedNames();
    public Task<IPAddress[]> ResolveAsync(string name, CancellationToken token) =>
        Dns.GetHostAddressesAsync(name, token).WaitAsync(TimeSpan.FromSeconds(2), token);
    public async Task ProbeAsync(uint tunnelIndex, CancellationToken token)
    {
        if (WindowsNetworkApi.BestInterface(IPAddress.Parse("1.1.1.1")) != tunnelIndex) throw new TunnelRouteException();
        using var probe = new TcpClient();
        await probe.ConnectAsync(IPAddress.Parse("1.1.1.1"), 443, token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), token);
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
