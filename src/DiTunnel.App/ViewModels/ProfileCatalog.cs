using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.ViewModels;

public sealed record ProfileGroup(string Id, string Name, string? SourceUrl = null)
{
    public override string ToString() => Name;
}

internal sealed class ProfileCatalog(IProfileStore store)
{
    private const int SmallSortLimit = 32;
    private readonly ProfileRows visibleRows = [];
    private ILookup<(string SourceId, string Content), ServerItemViewModel>? visibleProfiles;
    private string? visibleSourceId;

    public ProfileLibrary Library { get; private set; } = new([], []);
    public ObservableCollection<ProfileGroup> Groups { get; } = [];
    public ObservableCollection<ServerItemViewModel> VisibleProfiles => visibleRows;

    public static (string SourceId, string Content) Identity(ImportedProfile profile) => (profile.SourceId, profile.Content);

    public static bool SameProfile(ImportedProfile first, ImportedProfile second) => Identity(first) == Identity(second);

    public void Load() => Library = store.LoadLibrary();

    public void Save(ProfileLibrary library)
    {
        store.SaveLibrary(library);
        Library = library;
    }

    public int AddImportedProfiles(ProfileGroup source, IReadOnlyList<ImportedProfile> imported)
    {
        var existing = Library.Profiles.Where(profile => profile.SourceId == source.Id)
            .Select(profile => profile.Content).ToHashSet(StringComparer.Ordinal);
        var added = imported.Where(profile => !existing.Contains(profile.Content)).Select(profile => profile with
            { SourceId = source.Id, SourceName = source.Name, SourceUrl = source.SourceUrl }).ToArray();
        var remainingEmpty = Library.EmptySubscriptions.Where(subscription => subscription.SourceId != source.Id).ToArray();
        Save(new(Library.Profiles.Concat(added).ToArray(), remainingEmpty));
        return added.Length;
    }

    public ImportedProfile[] RemoveProfiles(Func<ImportedProfile, bool> predicate, string? deletedGroupId)
    {
        var previous = Library.Profiles;
        var remaining = previous.Where(profile => !predicate(profile)).ToArray();
        var removed = previous.Where(predicate).ToArray();
        var remainingSources = remaining.Select(profile => profile.SourceId).ToHashSet(StringComparer.Ordinal);
        var empty = Groups.Where(group => group.Id != deletedGroupId && group.SourceUrl is not null && !remainingSources.Contains(group.Id))
            .Select(group => new EmptySubscription(group.Id, group.Name, group.SourceUrl!)).ToArray();
        Save(new(remaining, empty));
        return removed;
    }

    public ProfileGroup? RebuildGroups(string? preferredSourceId)
    {
        var groups = Library.Profiles.GroupBy(profile => profile.SourceId).Select(group => group.First())
            .Select(profile => new ProfileGroup(profile.SourceId, profile.SourceName, profile.SourceUrl))
            .Concat(Library.EmptySubscriptions.Select(source => new ProfileGroup(source.SourceId, source.SourceName, source.SourceUrl)))
            .ToArray();
        if (!Groups.SequenceEqual(groups))
        {
            Groups.Clear();
            foreach (var group in groups) Groups.Add(group);
        }
        return Groups.FirstOrDefault(group => group.Id == preferredSourceId) ?? Groups.FirstOrDefault();
    }

    public ServerItemViewModel[] ShowGroup(string? sourceId, Func<ImportedProfile, ServerItemViewModel> createRow)
    {
        var profiles = Library.Profiles.Where(profile => profile.SourceId == sourceId)
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase);
        if (visibleSourceId != sourceId)
        {
            var newGroup = profiles.Select(createRow).ToArray();
            visibleSourceId = sourceId;
            if (visibleRows.Count > 0 || newGroup.Length > 0)
            {
                ReindexVisibleProfiles(newGroup);
                visibleRows.ReplaceAll(newGroup);
            }
            return newGroup;
        }

        var existingRows = new Dictionary<ImportedProfile, ServerItemViewModel>();
        foreach (var row in visibleRows) existingRows.TryAdd(row.Profile, row);
        var rows = profiles.Select(profile => existingRows.Remove(profile, out var row) ? row : createRow(profile)).ToArray();
        if (!visibleRows.SequenceEqual(rows))
        {
            ApplyChanges(rows);
            ReindexVisibleProfiles(visibleRows);
        }
        return rows;
    }

    public ServerItemViewModel? RestoreSelection(ImportedProfile? previous, ServerSelection? saved, ImportedProfile? active) =>
        FindVisible(previous) ?? FindSavedServer(saved) ?? FindVisible(active) ?? VisibleProfiles.FirstOrDefault();

    public ServerItemViewModel? FindVisible(ImportedProfile? profile) => FindVisibleRows(profile).FirstOrDefault();

    public IEnumerable<ServerItemViewModel> FindVisibleRows(ImportedProfile? profile) =>
        profile is null || visibleProfiles is null ? [] : visibleProfiles[Identity(profile)];

    public void RemoveVisibleProfiles(Func<ImportedProfile, bool> predicate)
    {
        foreach (var row in VisibleProfiles.Where(row => predicate(row.Profile)).ToArray()) VisibleProfiles.Remove(row);
        ReindexVisibleProfiles(visibleRows);
    }

    public bool SortByProbe()
    {
        var ordered = VisibleProfiles
            .OrderBy(server => server.ProbeMilliseconds is not null ? 0 : server.ProbeTimedOut ? 2 : 1)
            .ThenBy(server => server.ProbeMilliseconds ?? double.MaxValue)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (visibleRows.SequenceEqual(ordered)) return false;
        // The order of lookup values only matters when several rows share the same profile key.
        if (visibleProfiles?.Count != ordered.Length) ReindexVisibleProfiles(ordered);
        if (ordered.Length > SmallSortLimit) visibleRows.ReplaceAll(ordered);
        else ApplyChanges(ordered);
        return true;
    }

    private ServerItemViewModel? FindSavedServer(ServerSelection? saved)
    {
        if (saved is null) return null;
        ServerItemViewModel? matchingName = null;
        foreach (var row in VisibleProfiles)
        {
            var selection = ServerSelection.From(row.Profile);
            if (selection.ContentHash == saved.ContentHash) return row;
            if (matchingName is null && selection.IdentityHash == saved.IdentityHash) matchingName = row;
        }
        return matchingName;
    }

    private void ReindexVisibleProfiles(IEnumerable<ServerItemViewModel> rows) =>
        visibleProfiles = rows.ToLookup(row => Identity(row.Profile));

    private void ApplyChanges(ServerItemViewModel[] rows)
    {
        var retained = rows.ToHashSet();
        for (var index = VisibleProfiles.Count - 1; index >= 0; index--)
            if (!retained.Contains(VisibleProfiles[index])) VisibleProfiles.RemoveAt(index);

        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (index < VisibleProfiles.Count && ReferenceEquals(VisibleProfiles[index], row)) continue;
            var current = VisibleProfiles.IndexOf(row);
            if (current < 0) VisibleProfiles.Insert(index, row);
            else VisibleProfiles.Move(current, index);
        }
    }

    private sealed class ProfileRows : ObservableCollection<ServerItemViewModel>
    {
        public void ReplaceAll(ServerItemViewModel[] rows)
        {
            CheckReentrancy();
            var countChanged = Count != rows.Length;
            Items.Clear();
            foreach (var row in rows) Items.Add(row);
            if (countChanged) OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
