using System.Net;
using System.Text.Json.Nodes;
using DiTunnel.Core.Connection;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class TunnelHostTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlockedTrafficOrWrongRouteRefusesConnectedStateAndRollsBack(bool blocked)
    {
        await using var fixture = new Fixture();
        fixture.Network.ProbeException = blocked
            ? new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.AccessDenied)
            : new TunnelRouteException();
        var borrowed = new NetworkRoute("198.51.100.7/32", 2, "192.0.2.1");
        fixture.Network.Table.Add(borrowed);
        using var host = fixture.Start();
        var events = await DrainAsync(host);
        Assert.Contains(blocked ? "ERROR_NETWORK_ACCESS_DENIED" : "ERROR_PROBE_ROUTE", events);
        Assert.Contains("ERROR_STAGE_PROBE", events);
        Assert.DoesNotContain("CONNECTED", events);
        Assert.DoesNotContain("PROBE_WARNING", events);
        Assert.Contains("STOPPED", events);
        Assert.Equal(1, host.ExitCode);
        Assert.Equal([borrowed], fixture.Network.Table);
        Assert.Equal(1, fixture.Network.CleanupCount);
        Assert.True(fixture.Core.Stopped);
    }

    [Fact]
    public async Task ControlEndpointTimeoutKeepsWarningWithoutLoggingExceptionSecrets()
    {
        await using var fixture = new Fixture();
        fixture.Network.ProbeException = new TimeoutException("private profile credentials");
        using var host = fixture.Start();
        var events = new List<string>();
        while (await host.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { } line)
        {
            events.Add(line);
            if (line == "CONNECTED") break;
        }
        Assert.Contains("PROBE_WARNING", events);
        Assert.Contains("PROBE_EXCEPTION_TimeoutException", events);
        Assert.Contains("CONNECTED", events);
        Assert.DoesNotContain(events, line => line.Contains("private profile credentials"));
        host.RequestStop();
        Assert.Contains("STOPPED", await DrainAsync(host));
    }

    [Fact]
    public async Task RoutesWaitForWfpAndBorrowedRoutesSurviveDisconnect()
    {
        await using var fixture = new Fixture(protection: true);
        var borrowed = new NetworkRoute("192.0.2.11/32", 2, "192.0.2.1");
        fixture.Network.Table.Add(borrowed);
        using var host = fixture.Start();
        await ReadUntilAsync(host, "TUNNEL_INTERFACE_42");
        Assert.Single(fixture.Network.Table);
        Assert.Equal(borrowed, fixture.Network.Table.Single());
        await File.WriteAllTextAsync(fixture.Options.ProtectionReady!, "READY");
        await ReadUntilAsync(host, "CONNECTED");
        Assert.Contains(fixture.Network.Table, r => r.Prefix == "0.0.0.0/1");
        Assert.Contains(fixture.Network.Table, r => r.Prefix == "8000::/1");
        host.RequestStop();
        Assert.Contains("STOPPED", await DrainAsync(host));
        Assert.Equal([borrowed], fixture.Network.Table);
        Assert.True(fixture.Core.Stopped);
        Assert.False(File.Exists(fixture.Options.Config));
    }

    [Theory]
    [InlineData("DNS_RULE")]
    [InlineData("DNS_CACHE")]
    [InlineData("ADDRESSES")]
    [InlineData("ROUTES")]
    public async Task PartialStartupIsRolledBackAndReportsTheFailingStage(string stage)
    {
        await using var fixture = new Fixture();
        fixture.Network.FailingStage = stage;
        using var host = fixture.Start();
        var events = await DrainAsync(host);
        Assert.Contains("ERROR_STAGE_" + stage, events);
        Assert.Contains("STOPPED", events);
        Assert.Empty(fixture.Network.Table);
        Assert.Equal(1, fixture.Network.CleanupCount);
        Assert.True(fixture.Core.Stopped);
    }

    [Fact]
    public async Task CorporateDnsPrecheckRefusalDoesNotStartACoreOrDeletePolicies()
    {
        await using var fixture = new Fixture();
        fixture.Network.FailingStage = "PRECHECK";
        using var host = fixture.Start();
        var events = await DrainAsync(host);
        Assert.Contains("ERROR_DNS_POLICY", events);
        Assert.Equal(0, fixture.CoreStarts);
        Assert.Equal(0, fixture.Network.CleanupCount);
        Assert.Empty(fixture.Network.Table);
    }

    [Fact]
    public async Task DnsPrecheckFailureReportsOperationAndNumericCodeWithoutExceptionSecrets()
    {
        await using var fixture = new Fixture();
        fixture.Network.PrecheckException = new System.Runtime.InteropServices.COMException(
            "private profile credentials", unchecked((int)0x80041003));
        using var host = fixture.Start();
        var events = await DrainAsync(host);
        Assert.Contains("ERROR_PRECHECK_DNS", events);
        Assert.Contains("ERROR_EXCEPTION_COMException", events);
        Assert.Contains("ERROR_HRESULT_80041003", events);
        Assert.DoesNotContain(events, message => message.Contains("private profile credentials", StringComparison.Ordinal));
        Assert.Contains("STOPPED", events);
        Assert.Equal(0, fixture.CoreStarts);
        Assert.Equal(0, fixture.Network.CleanupCount);
    }

    [Fact]
    public async Task CancellationWhileWaitingForWfpRollsBackPhysicalServerRoute()
    {
        await using var fixture = new Fixture(protection: true);
        using var host = fixture.Start();
        await ReadUntilAsync(host, "TUNNEL_INTERFACE_42");
        host.RequestStop();
        var events = await DrainAsync(host);
        Assert.Contains("CANCELLED", events);
        Assert.Contains("STOPPED", events);
        Assert.Empty(fixture.Network.Table);
        Assert.True(fixture.Core.Stopped);
    }

    [Theory]
    [InlineData("192.0.2.1", "192.0.2.6")]
    [InlineData("192.0.2.2", "192.0.2.5")]
    public async Task NetworkLossPreservesProtectionAcrossRollback(string gateway, string address)
    {
        await using var fixture = new Fixture(protection: true);
        await File.WriteAllTextAsync(fixture.Options.ProtectionReady!, "READY");
        using var host = fixture.Start();
        await ReadUntilAsync(host, "CONNECTED");
        fixture.Network.Uplink = new(2, gateway, address);
        var events = await DrainAsync(host);
        Assert.Contains("ERROR_NETWORK_CHANGED", events);
        Assert.Contains("STOPPED", events);
        Assert.Equal("READY", await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "preserve-kill-switch")));
        Assert.Empty(fixture.Network.Table);
    }

    [Fact]
    public async Task FailedPhysicalRouteRemovalDoesNotClaimSuccessfulCleanup()
    {
        await using var fixture = new Fixture();
        using var host = fixture.Start();
        await ReadUntilAsync(host, "CONNECTED");
        fixture.Network.FailRemoval = true;
        host.RequestStop();
        var events = await DrainAsync(host);
        Assert.Contains("ERROR_CLEANUP", events);
        Assert.DoesNotContain("STOPPED", events);
        Assert.True(fixture.Core.Stopped);
    }

    [Theory]
    [InlineData(SplitTunnelMode.ProxyAll)]
    [InlineData(SplitTunnelMode.ProxySelected)]
    [InlineData(SplitTunnelMode.BypassSelected)]
    public async Task PrivateDnsIsAlwaysRoutedThroughTheTunnel(SplitTunnelMode mode)
    {
        await using var fixture = new Fixture(mode: mode);
        using var host = fixture.Start();
        await ReadUntilAsync(host, "CONNECTED");
        Assert.Contains(fixture.Network.Table, r => r.Prefix == "10.20.30.1/32" && r.InterfaceIndex == 42);
        Assert.Equal(["10.20.30.1"], fixture.Network.DnsServers!);
        if (mode == SplitTunnelMode.ProxySelected) Assert.DoesNotContain(fixture.Network.Table, r => r.Prefix == "0.0.0.0/1");
        host.RequestStop();
        await DrainAsync(host);
    }

    [Fact]
    public async Task AmneziaHopKeepsLoopbackWhileDirectOutboundBindsToPhysicalAddress()
    {
        await using var fixture = new Fixture(amnezia: true);
        using var host = fixture.Start();
        await ReadUntilAsync(host, "CONNECTED");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Options.Config))!;
        var proxy = config["outbounds"]![0]!;
        Assert.Null(proxy["sendThrough"]);
        Assert.Equal(24567, (int)proxy["settings"]!["servers"]![0]!["port"]!);
        Assert.Equal("192.0.2.5", (string?)config["outbounds"]![1]!["sendThrough"]);
        var awg = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "amneziawg.json")))!;
        Assert.Equal("192.0.2.5", (string?)awg["sourceAddress"]);
        Assert.Equal(2, (int)awg["sourceInterface"]!);
        host.RequestStop();
        Assert.Contains("STOPPED", await DrainAsync(host));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "amneziawg.json")));
    }

    private static async Task ReadUntilAsync(WindowsTunnelHost host, string expected)
    {
        var events = new List<string>();
        while (await host.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { } line)
        {
            events.Add(line);
            if (line == expected) return;
        }
        Assert.Fail("Missing event: " + expected + "; " + string.Join(", ", events));
    }
    private static async Task<List<string>> DrainAsync(WindowsTunnelHost host)
    {
        var events = new List<string>();
        while (await host.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { } line) events.Add(line);
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return events;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "DiTunnel-native-test-" + Guid.NewGuid().ToString("N"));
        internal FakeNetwork Network { get; } = new();
        internal FakeCore Core { get; } = new();
        internal TunnelHostOptions Options { get; }
        internal int CoreStarts;
        private WindowsTunnelHost? host;
        internal Fixture(bool protection = false, SplitTunnelMode mode = SplitTunnelMode.ProxyAll, bool amnezia = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var config = Path.Combine(Directory, "config.json");
            File.WriteAllText(config, """{"inbounds":[{"tag":"tun","settings":{"autoOutboundsInterface":"auto"}}],"outbounds":[{"tag":"proxy","protocol":"socks","settings":{"servers":[{"address":"127.0.0.1","port":0}]}},{"tag":"direct"}]}""");
            File.WriteAllText(Path.Combine(Directory, "amneziawg.json"), "{}");
            Options = new("synthetic-xray", config, IPAddress.Parse("192.0.2.11"), "DiTunnel-test", mode,
                [IPAddress.Parse("203.0.113.20")], [], protection ? Path.Combine(Directory, "kill-switch.ready") : null,
                amnezia ? "synthetic-awg" : null, ["10.20.30.1"]);
        }
        internal WindowsTunnelHost Start() => host = new(Options, Network, (path, _) =>
        {
            Interlocked.Increment(ref CoreStarts);
            if (path == "synthetic-awg") File.WriteAllText(Path.Combine(Directory, "amneziawg.ready"), "24567");
            return Core;
        });
        public async ValueTask DisposeAsync()
        {
            if (host is { HasExited: false }) { host.RequestStop(); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); host.Dispose(); }
            // Only the explicitly created fixture directory is removed.
            foreach (var path in System.IO.Directory.GetFiles(Directory)) File.Delete(path);
            System.IO.Directory.Delete(Directory);
        }
    }
    private sealed class FakeCore : ITunnelCore
    {
        public bool HasExited => false;
        public int ExitCode => 0;
        internal bool Stopped;
        public Task StopAsync() { Stopped = true; return Task.CompletedTask; }
    }
    private sealed class FakeNetwork : ITunnelNetwork
    {
        internal PhysicalUplink Uplink = new(2, "192.0.2.1", "192.0.2.5");
        internal readonly List<NetworkRoute> Table = [];
        internal string? FailingStage;
        internal Exception? PrecheckException;
        internal Exception? ProbeException;
        internal bool FailRemoval;
        internal int CleanupCount;
        internal string[]? DnsServers;
        public void PrecheckDns()
        {
            if (PrecheckException is not null) throw PrecheckException;
            if (FailingStage == "PRECHECK") throw new InvalidOperationException("Обнаружены существующие правила DNS.");
        }
        public PhysicalUplink FindUplink() => Uplink;
        public Task<IAsyncDisposable> AcquireServerRouteAsync(IPAddress address, PhysicalUplink uplink, CancellationToken token)
        {
            var route = new NetworkRoute(address + "/32", uplink.InterfaceIndex, uplink.NextHop);
            return Task.FromResult<IAsyncDisposable>(new FakeLease(this, AddRoute(route) ? route : null));
        }
        public IReadOnlyList<NetworkRoute> Routes() => Table;
        public bool AddRoute(NetworkRoute route)
        {
            if (FailingStage == "ROUTES" && route.InterfaceIndex == 42) throw new InvalidOperationException();
            if (Table.Contains(route)) return false;
            Table.Add(route); return true;
        }
        public void RemoveRoute(NetworkRoute route) { if (FailRemoval && route.InterfaceIndex == 2) throw new InvalidOperationException(); Table.Remove(route); }
        public uint FindTunnel(string name) => 42;
        public Task AddAddressAsync(uint index, string address, byte prefix, CancellationToken token) => FailingStage == "ADDRESSES" ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask;
        public void SetMetric(uint index, bool ipv6) { }
        public void InstallDns(string[] servers) { if (FailingStage == "DNS_RULE") throw new InvalidOperationException(); DnsServers = servers; }
        public void FlushDns() { if (FailingStage == "DNS_CACHE") throw new InvalidOperationException(); }
        public void CleanupDns() => CleanupCount++;
        public IReadOnlyList<string> CachedNames() => [];
        public Task<IPAddress[]> ResolveAsync(string name, CancellationToken token) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.20") });
        public Task ProbeAsync(uint tunnelIndex, CancellationToken token) => ProbeException is null ? Task.CompletedTask : Task.FromException(ProbeException);
        private sealed class FakeLease(FakeNetwork network, NetworkRoute? owned) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { if (owned is not null) network.RemoveRoute(owned); return ValueTask.CompletedTask; }
        }
    }
}
