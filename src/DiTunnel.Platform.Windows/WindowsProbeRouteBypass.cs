using System.Net;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows;

internal sealed class WindowsProbeRouteBypass(EndpointRouteLeases.Lease lease, IDisposable probeLease) : IAsyncDisposable
{
    private static readonly EndpointRouteLeases Routes = new(WindowsNetworkApi.RemoveRoute);
    private static readonly Dictionary<IPAddress, EndpointGate> endpointGates = new();
    public string? SourceAddress => lease.SourceAddress;

    internal static Task<EndpointRouteLeases.Lease> AcquireRouteAsync(IPAddress server, PhysicalUplink uplink, CancellationToken token) =>
        Routes.AcquireAsync(server, () =>
        {
            var route = new NetworkRoute(server + "/32", uplink.InterfaceIndex, uplink.NextHop);
            return new(WindowsNetworkApi.AddRoute(route) ? route : null, uplink.SourceAddress);
        }, token, requiresRoute: true);

    public static async Task<WindowsProbeRouteBypass> CreateAsync(IPAddress server, string sessionDirectory, CancellationToken token)
    {
        var probe = await AcquireEndpointAsync(server, token);
        try
        {
            var acquired = await Routes.AcquireAsync(server, () =>
            {
                if (server.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                    WindowsNetworkApi.IsPhysical(WindowsNetworkApi.BestInterface(server))) return new(null, null);
                var uplink = WindowsNetworkApi.FindUplink();
                var route = new NetworkRoute(server + "/32", uplink.InterfaceIndex, uplink.NextHop);
                return new(WindowsNetworkApi.AddRoute(route) ? route : null, uplink.SourceAddress);
            }, token);
            return new(acquired, probe);
        }
        catch { probe.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        try { await lease.DisposeAsync(); } finally { probeLease.Dispose(); }
    }

    internal static async Task<IDisposable> AcquireEndpointAsync(IPAddress address, CancellationToken token)
    {
        EndpointGate gate;
        lock (endpointGates)
        {
            if (!endpointGates.TryGetValue(address, out gate!)) endpointGates[address] = gate = new();
            gate.Users++;
        }
        try { await gate.Semaphore.WaitAsync(token); }
        catch { ReleaseReference(address, gate); throw; }
        return new ProbeLease(address, gate);
    }
    private static void ReleaseReference(IPAddress address, EndpointGate gate)
    {
        lock (endpointGates)
            if (--gate.Users == 0) { endpointGates.Remove(address); gate.Semaphore.Dispose(); }
    }
    private sealed class EndpointGate { internal readonly SemaphoreSlim Semaphore = new(1); internal int Users; }
    private sealed class ProbeLease(IPAddress address, EndpointGate gate) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            gate.Semaphore.Release();
            ReleaseReference(address, gate);
        }
    }
}
