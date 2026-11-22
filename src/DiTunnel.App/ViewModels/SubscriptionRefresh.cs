using DiTunnel.Core.Profiles;

namespace DiTunnel.App.ViewModels;

internal static class SubscriptionRefresh
{
    internal sealed record Download(ProfileGroup Source, ImportedProfile[] Profiles, Exception? Error);

    public static async Task<Download[]> DownloadAsync(IReadOnlyList<ProfileGroup> sources,
        Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download,
        bool useDirectRoute, Func<HttpClient>? createDirectClient, CancellationToken cancellationToken)
    {
        using var directClient = useDirectRoute ? createDirectClient?.Invoke() : null;
        using var concurrency = new SemaphoreSlim(4);
        return await Task.WhenAll(sources.Select(DownloadSourceAsync));

        async Task<Download> DownloadSourceAsync(ProfileGroup source)
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                if (useDirectRoute && directClient is null) throw new HttpRequestException();
                var request = download is not null ? download(source.SourceUrl!, deadline.Token)
                    : (directClient is null ? new ProfileImporter() : new ProfileImporter(directClient))
                        .ImportAsync(source.SourceUrl!, deadline.Token, source.Name);
                var profiles = await request.WaitAsync(deadline.Token);
                var replacement = profiles.DistinctBy(profile => profile.Content).Select(profile => profile with
                    { SourceId = source.Id, SourceName = source.Name, SourceUrl = source.SourceUrl }).ToArray();
                return new(source, replacement, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { return new(source, [], error); }
            finally { concurrency.Release(); }
        }
    }

    public static ApplyResult Apply(ProfileCatalog catalog, ILookup<string, ImportedProfile> initialProfiles,
        IReadOnlyList<Download> downloads, CancellationToken cancellationToken)
    {
        // Imports and removals can finish while a background download is in flight.
        var currentGroups = catalog.Groups.ToHashSet();
        var currentProfiles = catalog.Library.Profiles.ToLookup(profile => profile.SourceId, StringComparer.Ordinal);
        var eligible = downloads.Where(item => item.Profiles.Length > 0 && currentGroups.Contains(item.Source) &&
            initialProfiles[item.Source.Id].SequenceEqual(currentProfiles[item.Source.Id])).ToArray();
        var emptySources = catalog.Library.EmptySubscriptions.Select(source => source.SourceId).ToHashSet(StringComparer.Ordinal);
        var replacements = eligible.Where(item => emptySources.Contains(item.Source.Id) ||
            !currentProfiles[item.Source.Id].SequenceEqual(item.Profiles)).ToArray();
        if (!TrySave(catalog, replacements, cancellationToken))
            return new(eligible.Length - replacements.Length, false, []);

        var removed = new List<ImportedProfile>();
        foreach (var item in replacements)
        {
            var retained = item.Profiles.Select(profile => profile.Content).ToHashSet(StringComparer.Ordinal);
            removed.AddRange(currentProfiles[item.Source.Id].Where(profile => !retained.Contains(profile.Content)));
        }
        return new(eligible.Length, true, removed);
    }

    internal sealed record ApplyResult(int UpdatedCount, bool Saved, IReadOnlyList<ImportedProfile> RemovedProfiles);

    private static bool TrySave(ProfileCatalog catalog, IReadOnlyList<Download> replacements, CancellationToken cancellationToken)
    {
        if (replacements.Count == 0) return false;
        var replacedSources = replacements.Select(item => item.Source.Id).ToHashSet(StringComparer.Ordinal);
        var current = catalog.Library;
        var library = new ProfileLibrary(
            current.Profiles.Where(profile => !replacedSources.Contains(profile.SourceId))
                .Concat(replacements.SelectMany(item => item.Profiles)).ToArray(),
            current.EmptySubscriptions.Where(source => !replacedSources.Contains(source.SourceId)).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        try { catalog.Save(library); }
        catch { return false; }
        return true;
    }
}
