using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Android;

internal static class VpnWidgetProfiles
{
    internal static ImportedProfile? Displayed(VpnConnectionState state, ImportedProfile? active, Func<ImportedProfile?> selected)
        => state switch
        {
            VpnConnectionState.Connected => active,
            VpnConnectionState.Reconnecting or VpnConnectionState.Disconnecting => active ?? selected(),
            _ => selected()
        };

    internal static ImportedProfile[] Servers(ImportedProfile? displayed, IReadOnlyList<ImportedProfile> profiles)
        => displayed is null ? [] : profiles.Where(profile => profile.SourceId == displayed.SourceId)
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static bool SameProfile(ImportedProfile? first, ImportedProfile? second)
        => first is not null && second is not null && first.SourceId == second.SourceId && first.Content == second.Content;

    internal readonly record struct Session(string SourceId, string Content, DateTimeOffset? ConnectedAt);

    internal static Session? SessionFor(ImportedProfile? profile, DateTimeOffset? connectedAt)
        => profile is null ? null : new(profile.SourceId, profile.Content, connectedAt);
}
