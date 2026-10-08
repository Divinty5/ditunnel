using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Linux.Network;

public interface ILinuxFirewallCommands
{
    Task<string> ReadAsync(CancellationToken token);
    Task ApplyAsync(string batch, CancellationToken token);
}

public sealed class LinuxKillSwitch(ILinuxFirewallCommands commands, string owner)
{
    private string Table => TableFor(owner);
    private string Identity => "ditunnel:" + owner;
    public bool Active { get; private set; }
    public static string TableFor(string owner) => Guid.TryParseExact(owner, "N", out _) ? "dtks_" + owner : throw new ArgumentException();

    public static string CurrentCgroup()
    {
        var line = File.ReadLines("/proc/self/cgroup").Single(value => value.StartsWith("0::/", StringComparison.Ordinal));
        var path = line[4..];
        if (!Regex.IsMatch(path, @"^[a-zA-Z0-9_.@/\-]+$") || path.Split('/').Length is < 2 or > 16)
            throw new InvalidOperationException("Для kill switch требуется отдельная cgroup v2.");
        return path;
    }

    public static string Build(string owner, string name, IPAddress endpoint, ushort port, KillSwitchTransportProtocol protocol, bool allowLan, string cgroup)
    {
        var table = TableFor(owner);
        if (name != "dtn" + owner[..10] || port == 0 || !Regex.IsMatch(cgroup, @"^[a-zA-Z0-9_.@/\-]+$") || cgroup.Split('/').Length < 2)
            throw new ArgumentException();
        var family = endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "ip" : "ip6";
        var transports = protocol switch
        {
            KillSwitchTransportProtocol.Tcp => new[] { "tcp" },
            KillSwitchTransportProtocol.Udp => new[] { "udp" },
            KillSwitchTransportProtocol.TcpAndUdp => new[] { "tcp", "udp" },
            _ => throw new ArgumentException("Транспорт сервера должен быть ограничен TCP и/или UDP.")
        };
        var trusted = $"meta skuid 0 socket cgroupv2 level {cgroup.Split('/').Length} \"{cgroup}\"";
        // No general established/related accept: old direct sockets must also be blocked.
        // Rules live in the kernel, without a process-owned auto-delete table.
        var lines = new List<string>
        {
            $"add table inet {table} {{ comment \"ditunnel:{owner}\"; }}",
            $"add chain inet {table} output {{ type filter hook output priority 10; policy drop; }}",
            $"add chain inet {table} forward {{ type filter hook forward priority 10; policy drop; }}",
            $"add rule inet {table} output oifname \"lo\" accept",
            $"add rule inet {table} output oifname \"{name}\" accept",
            $"add rule inet {table} output meta l4proto {{ tcp, udp }} th dport 53 drop",
            $"add rule inet {table} output {trusted} meta mark {LinuxRouteLease.DirectMark} accept",
            $"add rule inet {table} output udp sport 68 udp dport 67 accept",
            $"add rule inet {table} output ip6 daddr ff02::1:2 udp sport 546 udp dport 547 accept",
            $"add rule inet {table} output icmpv6 type {{ nd-router-solicit, nd-router-advert, nd-neighbor-solicit, nd-neighbor-advert }} accept",
            $"add rule inet {table} forward oifname \"{name}\" accept",
            $"add rule inet {table} forward iifname \"{name}\" accept"
        };
        lines.InsertRange(5, transports.Select(transport => $"add rule inet {table} output {trusted} meta mark {LinuxRouteLease.Mark} {family} daddr {endpoint} {transport} dport {port} accept"));
        if (allowLan)
        {
            lines.Add($"add rule inet {table} output ip daddr {{ 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 }} accept");
            lines.Add($"add rule inet {table} output ip6 daddr {{ fc00::/7, fe80::/10 }} accept");
        }
        return string.Join('\n', lines) + "\n";
    }

    public async Task ActivateAsync(string name, IPAddress endpoint, ushort port, KillSwitchTransportProtocol protocol, bool allowLan, CancellationToken token)
    {
        var rules = JsonNode.Parse(await commands.ReadAsync(token))!["nftables"]!.AsArray();
        if (rules.Any(entry => entry?["table"]?["name"]?.ToString().StartsWith("dtks_", StringComparison.Ordinal) == true))
            throw new InvalidOperationException("Предыдущий kill switch требует восстановления сети.");
        try { await commands.ApplyAsync(Build(owner, name, endpoint, port, protocol, allowLan, CurrentCgroup()), token); }
        catch
        {
            // An interrupted nft invocation may already have committed its atomic batch.
            // Keep the recovery journal until the kernel state has been inspected.
            try { await ExistsAsync(CancellationToken.None); }
            catch { Active = true; }
            throw;
        }
        Active = true;
        if (!await ExistsAsync(token)) throw new InvalidOperationException("Kill switch не подтверждён.");
    }
    public async Task<bool> ExistsAsync(CancellationToken token)
    {
        var tables = JsonNode.Parse(await commands.ReadAsync(token))!["nftables"]!.AsArray();
        var table = tables.Select(entry => entry?["table"]).SingleOrDefault(entry => entry?["family"]?.ToString() == "inet" && entry?["name"]?.ToString() == Table);
        if (table is null) { Active = false; return false; }
        if (table["comment"]?.ToString() != Identity) throw new InvalidOperationException("Владелец firewall изменился.");
        Active = true; return true;
    }
    public async Task ReleaseAsync(CancellationToken token)
    {
        if (await ExistsAsync(token)) await commands.ApplyAsync($"delete table inet {Table}\n", token);
        Active = false;
    }
}
