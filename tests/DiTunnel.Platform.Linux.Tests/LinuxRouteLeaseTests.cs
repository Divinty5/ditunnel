using System.Net;
using System.Text.Json.Nodes;
using DiTunnel.Core.Profiles;
using DiTunnel.Infrastructure.Xray;
using DiTunnel.Platform.Linux.Network;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class LinuxRouteLeaseTests
{
    private const string Owner = "0123456789abcdef0123456789abcdef";
    private const string Name = "dtn0123456789";

    [Fact]
    public async Task Policy_conflict_is_rejected_before_mutation()
    {
        var commands = new Commands { Rules = "[{\"priority\":50,\"table\":900}]" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LinuxRouteLease(commands, Name, Owner).PreflightAsync(default));
        Assert.All(commands.Calls, call => Assert.Contains("-j", call));
    }

    [Fact]
    public async Task Rollback_removes_only_exact_owned_rules_and_routes()
    {
        var commands = new Commands
        {
            Rules = """[{"priority":101,"fwmark":"0xca6c","not":null,"src":"all","table":51820,"protocol":"198"}, {"priority":100,"fwmark":"0xca6c","table":"main","protocol":198}, {"priority":101,"fwmark":"0xca6c","not":null,"table":51820,"protocol":99}, {"priority":100,"fwmark":"0xca6c","src":"192.0.2.0/24","table":"main","protocol":198}]""",
            Routes = """[{"dst":"default","dev":"dtn0123456789","table":51820,"protocol":198}, {"dst":"default","dev":"foreign","table":51820,"protocol":198}]"""
        };
        await new LinuxRouteLease(commands, Name, Owner).RollbackAsync(default);
        var deletes = commands.Calls.Where(call => call.Contains("del")).ToArray();
        Assert.Equal(6, deletes.Length);
        Assert.All(deletes, call => { Assert.Contains("198", call); Assert.DoesNotContain("foreign", call); Assert.DoesNotContain("flush", call); });
    }

    [Fact]
    public async Task Dns_is_applied_after_capture_rules_and_failure_can_be_rolled_back()
    {
        var commands = new Commands { Links = """[{"ifname":"dtn0123456789","ifindex":8,"ifalias":"ditunnel:0123456789abcdef0123456789abcdef","linkinfo":{"info_kind":"tun"}}]""", FailDns = true };
        var lease = new LinuxRouteLease(commands, Name, Owner);
        await lease.ClaimLinkAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ActivateAsync(["1.1.1.1"], default));
        Assert.Equal(2, commands.Calls.Count(call => call.Contains("101") && call.Contains("add")));
        commands.FailDns = false;
        await lease.RollbackAsync(default);
        Assert.Contains(commands.Dns, call => call.SequenceEqual(new[] { "revert", Name }));
        Assert.All(commands.Dns, call => Assert.Contains(Name, call));
    }

    [Fact]
    public async Task Foreign_link_alias_is_never_reverted_or_deleted()
    {
        var commands = new Commands { Links = """[{"ifname":"dtn0123456789","ifindex":8,"ifalias":"someone-else","linkinfo":{"info_kind":"tun"}}]""" };
        var lease = new LinuxRouteLease(commands, Name, Owner);
        await Assert.ThrowsAsync<AggregateException>(() => lease.RollbackAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.RemoveOwnedLinkAsync(default));
        Assert.Empty(commands.Dns);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("del"));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [Fact]
    public void Tun_config_preserves_tls_and_marks_remote_egress()
    {
        var profile = ProfileParser.Parse("vless://11111111-1111-1111-1111-111111111111@192.0.2.10:443?security=tls&sni=example.test").Single();
        var config = JsonNode.Parse(LinuxVpnSession.BuildConfiguration(XrayProfileConverter.Convert(profile), IPAddress.Parse("192.0.2.10"), Name, ["1.1.1.1"]))!;
        Assert.Null(config["inbounds"]![0]!["settings"]!["autoOutboundsInterface"]);
        Assert.Equal(51820u, (uint)config["outbounds"]![0]!["streamSettings"]!["sockopt"]!["mark"]!);
        Assert.Equal("tls", (string?)config["outbounds"]![0]!["streamSettings"]!["security"]);
    }

    private sealed class Commands : ILinuxNetworkCommands
    {
        public string Rules = "[]", Routes = "[]", Links = "[]";
        public bool FailDns;
        public List<string[]> Calls = [];
        public List<string[]> Dns = [];
        public Task<string> IpAsync(CancellationToken token, params string[] arguments)
        {
            Calls.Add(arguments);
            return Task.FromResult(arguments.Contains("-j") ? arguments.Contains("link") ? Links : arguments.Contains("rule") ? Rules : Routes : "");
        }
        public Task ResolvedAsync(CancellationToken token, params string[] arguments)
        {
            Dns.Add(arguments);
            return FailDns ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask;
        }
    }
}
