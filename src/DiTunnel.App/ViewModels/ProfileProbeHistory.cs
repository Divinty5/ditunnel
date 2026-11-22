using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using static DiTunnel.App.ViewModels.ProfileCatalog;

namespace DiTunnel.App.ViewModels;

internal sealed class ProfileProbeHistory(ProfileCatalog catalog)
{
    private readonly Dictionary<(string SourceId, string Content), ProbeSnapshot> snapshots = [];
    private readonly HashSet<(string SourceId, string Content)> pending = [];

    public ProbeSnapshot? Find(ImportedProfile profile) => snapshots.GetValueOrDefault(Identity(profile));

    public ProbeSnapshot Get(ImportedProfile profile)
    {
        if (Find(profile) is { } snapshot) return snapshot;
        var row = catalog.FindVisible(profile);
        return row is null
            ? new("Не проверен", null, false, null)
            : new(row.ProbeText, row.ProbeMilliseconds, row.ProbeTimedOut, row.CountryCode);
    }

    public void Remove(ImportedProfile profile) => snapshots.Remove(Identity(profile));

    public void RecordCountry(ImportedProfile profile, string? code) =>
        snapshots[Identity(profile)] = Get(profile) with { CountryCode = code };

    public ServerItemViewModel CreateRow(ImportedProfile profile, bool includeConnectionResult = true)
    {
        var row = new ServerItemViewModel(profile)
        {
            IsProbePending = includeConnectionResult && pending.Contains(Identity(profile))
        };
        if (Find(profile) is { } snapshot)
        {
            row.ProbeText = snapshot.Text;
            row.ProbeMilliseconds = snapshot.Milliseconds;
            row.ProbeTimedOut = snapshot.TimedOut;
            row.IsProbeUnconfirmed = snapshot.Diagnostics is { Method: "TCP", ProfileConfirmed: false };
            row.CountryCode = snapshot.CountryCode;
            if (includeConnectionResult)
                row.UpdateConnectionProbe(snapshot.ConnectionResult, snapshot.ConnectionAttemptFailed);
        }
        return row;
    }

    public void MarkPending(IEnumerable<ServerItemViewModel> servers)
    {
        foreach (var server in servers)
        {
            pending.Add(Identity(server.Profile));
            server.IsProbePending = true;
        }
    }

    public bool ClearPending()
    {
        if (pending.Count == 0) return false;
        pending.Clear();
        foreach (var row in catalog.VisibleProfiles) row.IsProbePending = false;
        return true;
    }

    public ServerItemViewModel RecordProfileResult(ServerItemViewModel server, ServerProbeResult result)
    {
        var timedOut = !result.IsDeferred && result.Milliseconds is null && result.Message != "Не проверен";
        var previous = Get(server.Profile);
        var key = Identity(server.Profile);
        if (catalog.Library.Profiles.Any(profile => SameProfile(profile, server.Profile)))
            snapshots[key] = previous with
            {
                Text = result.Message,
                Milliseconds = result.Milliseconds,
                TimedOut = timedOut,
                Diagnostics = result.Diagnostics,
                ConnectionResult = null,
                ConnectionAttemptFailed = false
            };

        pending.Remove(key);
        var rows = catalog.FindVisibleRows(server.Profile);
        var visible = rows.FirstOrDefault() ?? server;
        visible.ProbeText = result.Message;
        visible.ProbeMilliseconds = result.Milliseconds;
        visible.ProbeTimedOut = timedOut;
        visible.IsProbeUnconfirmed = result.Diagnostics is { Method: "TCP", ProfileConfirmed: false };
        visible.IsProbePending = false;
        visible.UpdateConnectionProbe(null, false);
        foreach (var duplicate in rows.Skip(1)) duplicate.UpdateConnectionProbe(null, false);
        return visible;
    }

    public bool RecordConnectionResult(ImportedProfile profile, ServerProbeResult result, bool connectionAttempt)
    {
        if (!catalog.Library.Profiles.Any(candidate => SameProfile(candidate, profile))) return false;

        var previous = Get(profile);
        var updated = previous with
        {
            ConnectionResult = result,
            ConnectionAttemptFailed = result.Milliseconds is null && !result.IsDeferred &&
                result.Diagnostics?.ErrorType != "ControlService" && (connectionAttempt || previous.ConnectionAttemptFailed)
        };
        snapshots[Identity(profile)] = updated;
        var unavailableChanged = false;
        foreach (var row in catalog.FindVisibleRows(profile))
            unavailableChanged |= row.UpdateConnectionProbe(result, updated.ConnectionAttemptFailed);
        return unavailableChanged;
    }

    internal sealed record ProbeSnapshot(string Text, double? Milliseconds, bool TimedOut, string? CountryCode,
        ProbeDiagnostics? Diagnostics = null, ServerProbeResult? ConnectionResult = null, bool ConnectionAttemptFailed = false);
}
