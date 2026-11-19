using DiTunnel.Android;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Tests;

public sealed class VpnWidgetProfilesTests
{
    private static readonly ImportedProfile Active = new("Server 1", "VLESS", "synthetic-active", "connected", "Active subscription");
    private static readonly ImportedProfile Selected = new("Server 1", "VLESS", "synthetic-selected", "browsing", "Selected subscription");
    private static readonly ImportedProfile Peer = new("Peer", "VLESS", "synthetic-peer", "connected", "Active subscription");

    [Fact]
    public void BrowsingAnotherSubscriptionKeepsTheRunningProfileAndWidgetRows()
    {
        var displayed = VpnWidgetProfiles.Displayed(VpnConnectionState.Connected, Active, () => Selected);

        Assert.Same(Active, displayed);
        Assert.Equal([Peer, Active], VpnWidgetProfiles.Servers(displayed, [Active, Selected, Peer]));
        Assert.True(VpnWidgetProfiles.SameProfile(Active, displayed));
        Assert.False(VpnWidgetProfiles.SameProfile(Selected, displayed));
    }

    [Fact]
    public void WidgetsFollowARealSwitchAndReturnToSelectionWhenDisconnected()
    {
        Assert.Same(Selected, VpnWidgetProfiles.Displayed(VpnConnectionState.Connected, Selected, () => Active));
        Assert.Same(Active, VpnWidgetProfiles.Displayed(VpnConnectionState.Disconnected, Selected, () => Active));
    }

    [Fact]
    public void AnUnknownRunningProfileDoesNotBecomeTheSelectedProfile()
    {
        var displayed = VpnWidgetProfiles.Displayed(VpnConnectionState.Connected, null, () => Selected);

        Assert.Null(displayed);
        Assert.Empty(VpnWidgetProfiles.Servers(displayed, [Selected]));
    }

    [Fact]
    public void LatencySessionDistinguishesSubscriptionsAndReconnects()
    {
        var time = DateTimeOffset.UnixEpoch;
        var session = VpnWidgetProfiles.SessionFor(Active, time);

        Assert.Equal(session, VpnWidgetProfiles.SessionFor(Active with { Name = "Renamed" }, time));
        Assert.NotEqual(session, VpnWidgetProfiles.SessionFor(Active with { SourceId = Selected.SourceId }, time));
        Assert.NotEqual(session, VpnWidgetProfiles.SessionFor(Active, time.AddSeconds(1)));
    }
}
