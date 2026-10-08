using System.Net;
using System.Text.Json.Nodes;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using DiTunnel.Platform.Linux.Network;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class LinuxProtectionTests
{
    private const string Owner = "0123456789abcdef0123456789abcdef";
    [Fact]
    public void Process_capability_requires_metadata_for_the_actual_binary()
    {
        var root=Path.Combine(Path.GetTempPath(),"ditunnel-process-runtime-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root,"xray"),[1,2,3]);
            Assert.False(LinuxXrayProcessSupport.IsAvailable(root));
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[]{1,2,3}));
            File.WriteAllText(Path.Combine(root,"XRAY-SOURCE.json"),new JsonObject{["linuxProcessLookup"]=2,["binarySha256"]=hash}.ToJsonString());
            Assert.True(LinuxXrayProcessSupport.IsAvailable(root));
            File.WriteAllBytes(Path.Combine(root,"xray"),[4,5,6]);
            Assert.False(LinuxXrayProcessSupport.IsAvailable(root));
        }
        finally { Directory.Delete(root,true); }
    }
    [Theory]
    [InlineData(-1)] [InlineData(3)]
    public void Invalid_mode_is_rejected(int mode) => Assert.Throws<ArgumentException>(() => (LinuxVpnPolicy.Default with { Mode = (SplitTunnelMode)mode }).Validate());
    [Theory]
    [InlineData("curl")] [InlineData("/usr/bin/../bin/curl")] [InlineData("/usr/bin/\napp")] [InlineData("/usr/bin/")]
    public void Process_rules_are_absolute_executables(string value) => Assert.Throws<ArgumentException>(() => (LinuxVpnPolicy.Default with { Processes = [value] }).Validate());
    [Fact]
    public void Ipc_rejects_injected_unknown_properties_and_oversized_lists()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => LinuxVpnPolicy.Parse("{\"Mode\":0,\"Domains\":[],\"Processes\":[],\"script\":\"flush ruleset\"}"));
        Assert.Throws<ArgumentException>(() => (LinuxVpnPolicy.Default with { Domains = Enumerable.Repeat("example.test",257).ToArray() }).Validate());
    }
    [Theory] [InlineData(SplitTunnelMode.BypassSelected)] [InlineData(SplitTunnelMode.ProxySelected)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Split_keeps_dns_in_proxy_and_separates_direct_transport_marks(SplitTunnelMode mode)
    {
        var profile=ProfileParser.Parse("vless://11111111-1111-1111-1111-111111111111@192.0.2.10:443?security=tls&sni=example.test").Single();
        var policy=new LinuxVpnPolicy(mode,["198.51.100.20","example.test"],["/usr/bin/curl"],true,false);
        var config=JsonNode.Parse(LinuxVpnSession.BuildConfiguration(XrayProfileConverter.Convert(profile),IPAddress.Parse("192.0.2.10"),"dtn0123456789",["1.1.1.1"],policy))!;
        Assert.Equal("proxy",config["routing"]!["rules"]![0]!["outboundTag"]!.ToString());
        Assert.Equal("53",config["routing"]!["rules"]![0]!["port"]!.ToString());
        foreach(var outbound in config["outbounds"]!.AsArray())
            Assert.Equal(outbound!["protocol"]!.ToString()=="freedom"?51821:51820,outbound["streamSettings"]!["sockopt"]!["mark"]!.GetValue<int>());
        Assert.Contains("/usr/bin/curl",config.ToJsonString());
        Assert.Contains("tls",config.ToJsonString());
    }
    [Fact]
    public void Firewall_is_kernel_persistent_scoped_and_not_a_blanket_mark_exception()
    {
        var batch=LinuxKillSwitch.Build(Owner,"dtn0123456789",IPAddress.Parse("192.0.2.10"),443,KillSwitchTransportProtocol.Tcp,true,"system.slice/ditunnel-network.service");
        Assert.Contains("socket cgroupv2 level 2",batch); Assert.Contains("meta skuid 0",batch);
        Assert.Contains("meta mark 51820 ip daddr 192.0.2.10 tcp dport 443",batch);
        Assert.Contains("meta mark 51821",batch); Assert.Contains("policy drop",batch);
        Assert.DoesNotContain("ct state established",batch); Assert.DoesNotContain("flush ruleset",batch); Assert.DoesNotContain("flags owner",batch);
        Assert.True(batch.IndexOf("th dport 53 drop",StringComparison.Ordinal)<batch.IndexOf("ip daddr { 10.0.0.0",StringComparison.Ordinal));
        Assert.True(batch.IndexOf("th dport 53 drop",StringComparison.Ordinal)<batch.IndexOf("meta mark 51821",StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(KillSwitchTransportProtocol.Tcp,true,false)]
    [InlineData(KillSwitchTransportProtocol.Udp,false,true)]
    [InlineData(KillSwitchTransportProtocol.TcpAndUdp,true,true)]
    public void Transport_protocols_are_explicit(KillSwitchTransportProtocol protocol,bool tcp,bool udp)
    {
        var batch=LinuxKillSwitch.Build(Owner,"dtn0123456789",IPAddress.Parse("2001:db8::1"),443,protocol,false,"system.slice/ditunnel-network.service");
        Assert.Equal(tcp,batch.Contains("ip6 daddr 2001:db8::1 tcp dport 443"));
        Assert.Equal(udp,batch.Contains("ip6 daddr 2001:db8::1 udp dport 443"));
        Assert.Throws<ArgumentException>(() => LinuxKillSwitch.Build(Owner,"dtn0123456789",IPAddress.Loopback,443,KillSwitchTransportProtocol.Any,false,"system.slice/ditunnel-network.service"));
    }
    private sealed class ProtectedSession : ILinuxVpnSession
    {
        public bool IsProtectionActive { get; private set; }=true;
        public Task StartAsync(ImportedProfile _,CancellationToken token)=>Task.CompletedTask;
        public Task WaitForExitAsync(CancellationToken token)=>Task.Delay(Timeout.Infinite,token);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        public Task ReleaseProtectionAsync() { IsProtectionActive=false; return Task.CompletedTask; }
    }
    private sealed class FailedProtectedSession : ILinuxVpnSession
    {
        public bool IsProtectionActive { get; private set; } = true;
        public Task StartAsync(ImportedProfile _,CancellationToken token) => Task.FromException(new IOException("Startup failed after firewall commit"));
        public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task ReleaseProtectionAsync() { IsProtectionActive=false; return Task.CompletedTask; }
    }
    [Fact]
    public async Task Failed_start_and_denied_recovery_do_not_release_protection()
    {
        await using var coordinator = new LinuxVpnCoordinator(() => new FailedProtectedSession());
        var id=Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ConnectAsync(":1.1",id,new("Synthetic","VLESS","unused"),_=>Task.FromResult(true)));
        await coordinator.DisconnectAsync(":1.1",id);
        Assert.True(coordinator.ProtectionActive);
        var restored=false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.RecoverAsync(":1.2",_=>Task.FromResult(false),()=> { restored=true; return Task.CompletedTask; }));
        Assert.False(restored); Assert.True(coordinator.ProtectionActive);
        await coordinator.RecoverAsync(":1.2",_=>Task.FromResult(true),()=> { restored=true; return Task.CompletedTask; });
        Assert.True(restored); Assert.False(coordinator.ProtectionActive);
    }
    [Fact]
    public async Task Host_shutdown_preserves_protection()
    {
        var session=new ProtectedSession();
        var coordinator=new LinuxVpnCoordinator(()=>session);
        await coordinator.ConnectAsync(":1.1",Guid.NewGuid(),new("Synthetic","VLESS","unused"),_=>Task.FromResult(true));
        await coordinator.DisposeAsync();
        Assert.True(session.IsProtectionActive);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Owner_loss_preserves_protection_but_explicit_disconnect_releases_it(bool lost)
    {
        await using var coordinator=new LinuxVpnCoordinator(()=>new ProtectedSession());
        var id=Guid.NewGuid(); await coordinator.ConnectAsync(":1.1",id,new("Synthetic","VLESS","unused"),_=>Task.FromResult(true));
        if(lost) await coordinator.SenderDisconnectedAsync(":1.1"); else await coordinator.DisconnectAsync(":1.1",id);
        Assert.Equal(lost,coordinator.ProtectionActive);
        Assert.True(coordinator.IsIdle);
        if(lost) Assert.Contains("Kill switch",coordinator.Status.Message);
        if(lost)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ConnectAsync(":1.2",Guid.NewGuid(),new("Synthetic","VLESS","unused"),_=>Task.FromResult(true)));
            Assert.True(coordinator.ProtectionActive);
        }
    }
}
