using System.Net;
using DiTunnel.Core.Connection;

namespace DiTunnel.Core.Tests;

public sealed class KillSwitchRulePlanTests
{
    [Fact]
    public void ExceptionsPrecedeTerminalIpv4AndIpv6Blocks()
    {
        var plan = KillSwitchRulePlan.Create(KillSwitchConfiguration.Create([IPAddress.Parse("198.51.100.5")], false), 42);
        Assert.Collection(plan.Rules,
            rule => Assert.Equal(KillSwitchRuleKind.PermitServer, rule.Kind),
            rule => Assert.Equal(KillSwitchRuleKind.PermitTunnelInterface, rule.Kind),
            rule => Assert.Equal(KillSwitchRuleKind.BlockOtherIpv4, rule.Kind),
            rule => Assert.Equal(KillSwitchRuleKind.BlockOtherIpv6, rule.Kind));
    }
}
