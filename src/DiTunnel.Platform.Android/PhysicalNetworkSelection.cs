namespace DiTunnel.Platform.Android;

// Android validation is a preference, not proof that a VPN server is reachable.
// A carrier can block the system probe while allowing the user's destination.
internal static class PhysicalNetworkSelection
{
    public static bool CanUse(bool notVpn, bool internet) => notVpn && internet;

    public static T? Select<T>(IEnumerable<PhysicalNetworkCandidate<T>> candidates) where T : class => candidates
        .Where(candidate => CanUse(candidate.NotVpn, candidate.Internet))
        .OrderByDescending(candidate => candidate.Validated)
        .ThenByDescending(candidate => candidate.IsDefault)
        .Select(candidate => candidate.Network)
        .FirstOrDefault();
}

internal sealed record PhysicalNetworkCandidate<T>(T Network, bool NotVpn, bool Internet, bool Validated, bool IsDefault)
    where T : class;
