using System.Net;
using DiTunnel.Core.Connection;
using DiTunnel.Platform.Linux.Network;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class LinuxProbeFirewallTests
{
    private const string Owner = "0123456789abcdef0123456789abcdef";
    private sealed class Firewall : ILinuxFirewallCommands
    {
        public string Identity = "ditunnel:" + Owner;
        public List<string> Batches = [];
        public Task<string> ReadAsync(CancellationToken token) => Task.FromResult("{\"nftables\":[{\"table\":{\"family\":\"inet\",\"name\":\"dtks_" + Owner + "\",\"comment\":\"" + Identity + "\"}}]}");
        public Task ApplyAsync(string batch, CancellationToken token) { Batches.Add(batch); return Task.CompletedTask; }
    }

    [Theory]
    [InlineData("192.0.2.12", "4tcp", KillSwitchTransportProtocol.Tcp)]
    [InlineData("2001:db8::12", "6udp", KillSwitchTransportProtocol.Udp)]
    public async Task Permit_is_limited_to_endpoint_family_transport_and_port_and_removed_after_probe(string address, string set, KillSwitchTransportProtocol protocol)
    {
        var firewall = new Firewall();
        await using (var lease = new LinuxProbeFirewallLease(firewall))
        {
            await lease.PermitAsync(IPAddress.Parse(address), 443, protocol, CancellationToken.None);
            Assert.Single(firewall.Batches);
            Assert.Contains("probe" + set, firewall.Batches[0]);
            Assert.Contains(address + " . 443 timeout 30s", firewall.Batches[0]);
        }
        Assert.Equal(2, firewall.Batches.Count);
        Assert.StartsWith("delete element inet dtks_" + Owner, firewall.Batches[1]);
    }

    [Fact]
    public async Task Changed_firewall_owner_is_never_modified()
    {
        var firewall = new Firewall { Identity = "foreign" };
        await using var lease = new LinuxProbeFirewallLease(firewall);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.PermitAsync(IPAddress.Loopback, 443, KillSwitchTransportProtocol.Tcp, CancellationToken.None));
        Assert.Empty(firewall.Batches);
    }
}
