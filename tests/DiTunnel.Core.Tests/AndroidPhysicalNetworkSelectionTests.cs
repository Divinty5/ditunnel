using DiTunnel.Platform.Android;

namespace DiTunnel.Core.Tests;

public sealed class AndroidPhysicalNetworkSelectionTests
{
    [Fact]
    public void MobileNetworkWithoutSystemValidationStillAllowsConnection()
    {
        var candidates = new[] { Candidate("mobile", validated: false) };
        Assert.Equal("mobile", PhysicalNetworkSelection.Select(candidates));
    }

    [Fact]
    public void ValidatedPhysicalNetworkIsPreferredToUnvalidatedDefault()
    {
        var candidates = new[] { Candidate("wifi", validated: false, isDefault: true), Candidate("mobile", validated: true) };
        Assert.Equal("mobile", PhysicalNetworkSelection.Select(candidates));
    }

    [Fact]
    public void DefaultNetworkWinsWhenValidationIsEqual()
    {
        var candidates = new[] { Candidate("wifi", validated: false), Candidate("mobile", validated: false, isDefault: true) };
        Assert.Equal("mobile", PhysicalNetworkSelection.Select(candidates));
    }

    [Fact]
    public void ActiveValidatedVpnCannotBecomeItsOwnUnderlyingNetwork()
    {
        var candidates = new[] { Candidate("vpn", notVpn: false, validated: true, isDefault: true), Candidate("mobile", validated: false) };
        Assert.Equal("mobile", PhysicalNetworkSelection.Select(candidates));
    }

    [Fact]
    public void LocalOnlyNetworkDoesNotPermitConnection()
    {
        var candidates = new[] { Candidate("wifi", internet: false, validated: true, isDefault: true) };
        Assert.Null(PhysicalNetworkSelection.Select(candidates));
    }

    [Fact]
    public void LossOfValidationDoesNotTurnAnExistingPhysicalNetworkOffline()
    {
        var before = Candidate("mobile", validated: true);
        var after = before with { Validated = false };
        Assert.Equal("mobile", PhysicalNetworkSelection.Select(new[] { before }));
        Assert.Equal("mobile", PhysicalNetworkSelection.Select(new[] { after }));
        Assert.True(PhysicalNetworkSelection.CanUse(after.NotVpn, after.Internet));
    }

    [Fact]
    public void NoPhysicalNetworkStillMeansOffline()
    {
        Assert.Null(PhysicalNetworkSelection.Select(Array.Empty<PhysicalNetworkCandidate<string>>()));
        Assert.False(PhysicalNetworkSelection.CanUse(notVpn: false, internet: true));
        Assert.False(PhysicalNetworkSelection.CanUse(notVpn: true, internet: false));
    }

    private static PhysicalNetworkCandidate<string> Candidate(string name, bool notVpn = true, bool internet = true,
        bool validated = false, bool isDefault = false) => new(name, notVpn, internet, validated, isDefault);
}
