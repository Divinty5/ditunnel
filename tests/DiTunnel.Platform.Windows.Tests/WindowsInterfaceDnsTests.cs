using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DiTunnel.Platform.Windows.Network;
using Windows.Win32.NetworkManagement.IpHelper;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class WindowsInterfaceDnsTests
{
    private static readonly Guid TunnelId = new("85dfc5bc-aa34-41a9-a5d3-0082bc9c0ff5");

    [Fact]
    public void ReadOnlyLookupWorksWithRealWindowsInterfacesAndForeignFilters()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        var target = interfaces.First(adapter => adapter.Supports(NetworkInterfaceComponent.IPv4));
        uint index = (uint)target.GetIPProperties().GetIPv4Properties().Index;
        var filter = new UnsupportedInterface("Foreign-filter");
        var actual = WindowsInterfaceDns.Read(index, target.Name, interfaces.Concat([filter]));
        Assert.NotNull(actual);
        Assert.Equal(Guid.Parse(target.Id), actual.Id);
        Assert.Equal(target.Name, actual.Name);
        Assert.Equal(target.GetIPProperties().DnsAddresses, actual.Servers);
        Assert.False(filter.PropertiesRead);
        Assert.Null(WindowsInterfaceDns.Read(index, "DiTunnel-missing", interfaces));
        // Reproduce the old full-enumeration failure after a valid match, without
        // changing any interface, DNS policy or route on the test computer.
        var error = Assert.Throws<NetworkInformationException>(() => new[] { target, filter }.SingleOrDefault(candidate =>
            candidate.GetIPProperties().GetIPv4Properties().Index == index));
        Assert.Equal(10043, error.NativeErrorCode);
    }

    [Fact]
    public void MatchingInterfaceWithoutIpv4IsNotQueried()
    {
        var adapter = new UnsupportedInterface("DiTunnel-test");
        Assert.Null(WindowsInterfaceDns.Read(42, adapter.Name, [adapter]));
        Assert.False(adapter.PropertiesRead);
    }

    [Fact]
    public void SdkLayoutMatchesTheX64NativeDnsSettingsContract()
    {
        Assert.Equal(64, Marshal.SizeOf<DNS_INTERFACE_SETTINGS>());
        Assert.Equal(24, Marshal.OffsetOf<DNS_INTERFACE_SETTINGS>(nameof(DNS_INTERFACE_SETTINGS.NameServer)).ToInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DnsIsConfiguredAndClearedOnlyOnTheVerifiedTunnel(bool ipv6)
    {
        var state = new AdapterState();
        string[] servers = ipv6 ? ["1.1.1.1", "2606:4700:4700::1111"] : ["1.1.1.1", "1.0.0.1"];
        using (WindowsInterfaceDns.Configure(42, "DiTunnel-test", servers, state.Read, state.Set))
            Assert.Equal(servers.Select(IPAddress.Parse).ToHashSet(), state.Servers.ToHashSet());
        Assert.Empty(state.Servers);
        Assert.All(state.Writes, write => Assert.Equal(TunnelId, write.Id));
        Assert.Equal(ipv6 ? 4 : 2, state.Writes.Count);
    }

    [Theory]
    [InlineData("Ethernet", false, "ADAPTER_OWNER")]
    [InlineData("DiTunnel-test", true, "ADAPTER_NOT_EMPTY")]
    public void PhysicalOrPreconfiguredAdapterIsNeverChanged(string name, bool preconfigured, string code)
    {
        var state = new AdapterState { Name = name };
        if (preconfigured) state.Servers.Add(IPAddress.Parse("192.0.2.53"));
        var error = Assert.Throws<DnsPolicyException>(() => WindowsInterfaceDns.Configure(42, "DiTunnel-test",
            ["1.1.1.1"], state.Read, state.Set));
        Assert.Equal(code, error.Code);
        Assert.Empty(state.Writes);
    }

    [Fact]
    public void FailedSecondFamilyRestoresTheFirstFamily()
    {
        var state = new AdapterState { FailIpv6Set = true };
        var error = Assert.Throws<Win32Exception>(() => WindowsInterfaceDns.Configure(42, "DiTunnel-test",
            ["1.1.1.1", "2606:4700:4700::1111"], state.Read, state.Set));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Empty(state.Servers);
        Assert.Contains(state.Writes, write => !write.Ipv6 && write.Servers is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsAutomaticIpv6DefaultsDoNotBlockDnsConfiguration(bool ipv6)
    {
        var state = new AdapterState();
        state.Servers.AddRange(new[] { "fec0:0:0:ffff::1%1", "fec0:0:0:ffff::2%1", "fec0:0:0:ffff::3%1" }.Select(IPAddress.Parse));
        string[] servers = ipv6 ? ["1.1.1.1", "2606:4700:4700::1111"] : ["1.1.1.1"];
        using (WindowsInterfaceDns.Configure(42, "DiTunnel-test", servers, state.Read, state.Set))
            Assert.True(WindowsInterfaceDns.ConfiguredServers(state.Servers).ToHashSet().SetEquals(servers.Select(IPAddress.Parse)));
        Assert.Empty(WindowsInterfaceDns.ConfiguredServers(state.Servers));
    }

    [Fact]
    public void OtherSiteLocalServersArePreservedAndRefused()
    {
        var state = new AdapterState();
        state.Servers.AddRange(new[] { "fec0:0:0:ffff::1", "fec0:0:0:ffff::4" }.Select(IPAddress.Parse));
        var error = Assert.Throws<DnsPolicyException>(() => WindowsInterfaceDns.Configure(42, "DiTunnel-test",
            ["1.1.1.1"], state.Read, state.Set));
        Assert.Equal("ADAPTER_NOT_EMPTY", error.Code);
        Assert.Empty(state.Writes);
        Assert.Equal(2, state.Servers.Count);
    }

    [Fact]
    public void AdapterThatDoesNotApplyDnsCannotReportReady()
    {
        var state = new AdapterState { IgnoreSet = true };
        var error = Assert.Throws<DnsPolicyException>(() => WindowsInterfaceDns.Configure(42, "DiTunnel-test",
            ["1.1.1.1"], state.Read, state.Set));
        Assert.Equal("ADAPTER_VERIFY", error.Code);
        Assert.Contains(state.Writes, write => write.Servers is null);
    }

    [Fact]
    public void RemovedAdapterDuringCleanupIsAcceptedAndCleanupIsIdempotent()
    {
        var state = new AdapterState();
        var lease = WindowsInterfaceDns.Configure(42, "DiTunnel-test", ["1.1.1.1"], state.Read, state.Set);
        state.Removed = true;
        lease.Dispose();
        int writes = state.Writes.Count;
        lease.Dispose();
        Assert.Equal(writes, state.Writes.Count);
    }

    private sealed class UnsupportedInterface(string name) : NetworkInterface
    {
        internal bool PropertiesRead;
        public override string Id => Guid.Empty.ToString();
        public override string Name => name;
        public override string Description => name;
        public override OperationalStatus OperationalStatus => OperationalStatus.Up;
        public override NetworkInterfaceType NetworkInterfaceType => NetworkInterfaceType.Unknown;
        public override long Speed => 0;
        public override bool IsReceiveOnly => false;
        public override bool SupportsMulticast => false;
        public override bool Supports(NetworkInterfaceComponent component) => false;
        public override IPInterfaceProperties GetIPProperties()
        {
            PropertiesRead = true;
            throw new NetworkInformationException(10043);
        }
        public override PhysicalAddress GetPhysicalAddress() => PhysicalAddress.None;
        public override IPv4InterfaceStatistics GetIPv4Statistics() => throw new NotSupportedException();
    }

    private sealed class AdapterState
    {
        internal string Name = "DiTunnel-test";
        internal bool FailIpv6Set, IgnoreSet, Removed;
        internal List<IPAddress> Servers { get; } = [];
        internal List<(Guid Id, bool Ipv6, string? Servers)> Writes { get; } = [];
        internal WindowsInterfaceDns.Adapter Read(uint index) => new(TunnelId, index, Name, Servers.ToArray());
        internal void Set(Guid id, bool ipv6, string? servers)
        {
            Writes.Add((id, ipv6, servers));
            if (Removed) throw new Win32Exception(1168);
            if (servers is not null && ipv6 && FailIpv6Set) throw new Win32Exception(5);
            if (servers is not null && IgnoreSet) return;
            Servers.RemoveAll(address => (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) == ipv6);
            if (servers is not null) Servers.AddRange(servers.Split(',').Select(IPAddress.Parse));
        }
    }
}
