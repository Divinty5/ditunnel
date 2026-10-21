using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Linux.Network;

// The host serializes probes and control operations. Only root sockets in its
// cgroup carrying the transport mark can use these short-lived endpoint permits.
internal sealed class LinuxProbeFirewallLease(ILinuxFirewallCommands? commands) : IAsyncDisposable
{
    private readonly List<(string Table, string Owner, string Set, string Element)> entries = [];

    public async Task PermitAsync(IPAddress address, ushort port, KillSwitchTransportProtocol protocol, CancellationToken token)
    {
        if (commands is null) return;
        var transports = protocol switch
        {
            KillSwitchTransportProtocol.Tcp => new[] { "tcp" },
            KillSwitchTransportProtocol.Udp => new[] { "udp" },
            KillSwitchTransportProtocol.TcpAndUdp => new[] { "tcp", "udp" },
            _ => throw new ArgumentException("Некорректный транспорт проверки.")
        };
        var rules = JsonNode.Parse(await commands.ReadAsync(token).ConfigureAwait(false))!["nftables"]!.AsArray();
        foreach (var table in rules.Select(entry => entry?["table"]).Where(table => table?["name"]?.ToString().StartsWith("dtks_", StringComparison.Ordinal) == true))
        {
            var name = table!["name"]!.ToString();
            var owner = name[5..];
            if (table["family"]?.ToString() != "inet" || !Guid.TryParseExact(owner, "N", out _) || table["comment"]?.ToString() != "ditunnel:" + owner)
                throw new InvalidOperationException("Владелец firewall изменился.");
            foreach (var transport in transports)
            {
                var set = "probe" + (address.AddressFamily == AddressFamily.InterNetwork ? "4" : "6") + transport;
                var element = $"{address} . {port}";
                // Record before mutation: cancellation may arrive after nft commits.
                entries.Add((name, owner, set, element));
                await commands.ApplyAsync($"add element inet {name} {set} {{ {element} timeout 30s }}\n", token).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (commands is null || entries.Count == 0) return;
        var rules = JsonNode.Parse(await commands.ReadAsync(CancellationToken.None).ConfigureAwait(false))!["nftables"]!.AsArray();
        foreach (var entry in entries)
        {
            var table = rules.Select(node => node?["table"]).SingleOrDefault(node => node?["family"]?.ToString() == "inet" && node?["name"]?.ToString() == entry.Table);
            if (table is null) continue;
            if (table["comment"]?.ToString() != "ditunnel:" + entry.Owner) throw new InvalidOperationException("Владелец firewall изменился.");
            // A permit also expires in the kernel if the client or service is killed.
            await commands.ApplyAsync($"delete element inet {entry.Table} {entry.Set} {{ {entry.Element} }}\n", CancellationToken.None).ConfigureAwait(false);
        }
    }
}
