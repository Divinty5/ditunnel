namespace DiTunnel.Core.Profiles;

/// <summary>Creates an importable link for sharing a single client profile.</summary>
public static class ProfileShareFormatter
{
    public static string? CreateLink(ImportedProfile? profile)
    {
        if (profile is null) return null;
        if (!profile.Kind.Equals(AmneziaWgConfiguration.ProfileKind, StringComparison.OrdinalIgnoreCase))
            return profile.Content.Contains("://", StringComparison.Ordinal) ? profile.Content : null;
        try { return AmneziaVpnLink.Create(profile); }
        catch (FormatException) { return null; }
    }
}