using System.Net;
using System.Net.Sockets;

namespace DiTunnel.Core.Connection;

/// <summary>OS-neutral order for installing terminal network rules.</summary>
public sealed record KillSwitchRulePlan(IReadOnlyList<KillSwitchRule> Rules)
{
    public static KillSwitchRulePlan Create(KillSwitchConfiguration configuration, ulong tunnelInterfaceLuid)
    {
        if (tunnelInterfaceLuid == 0) throw new ArgumentOutOfRangeException(nameof(tunnelInterfaceLuid));
        var rules = new List<KillSwitchRule>();
        foreach (var address in configuration.ServerAddresses)
            rules.Add(new(KillSwitchRuleKind.PermitServer, address, null));
        rules.Add(new(KillSwitchRuleKind.PermitTunnelInterface, null, tunnelInterfaceLuid));
        if (configuration.AllowLocalNetwork) rules.Add(new(KillSwitchRuleKind.PermitLocalNetwork, null, null));
        rules.Add(new(KillSwitchRuleKind.BlockOtherIpv4, null, null));
        rules.Add(new(KillSwitchRuleKind.BlockOtherIpv6, null, null));
        return new(rules);
    }
}

public enum KillSwitchRuleKind { PermitServer, PermitTunnelInterface, PermitLocalNetwork, BlockOtherIpv4, BlockOtherIpv6 }
public sealed record KillSwitchRule(KillSwitchRuleKind Kind, IPAddress? Address, ulong? InterfaceLuid);
