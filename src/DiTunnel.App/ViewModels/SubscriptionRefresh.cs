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

    public static ProfileLibrary? TrySave(IProfileStore store, IReadOnlyList<ImportedProfile> profiles,
        IReadOnlyList<EmptySubscription> emptySubscriptions, IReadOnlyList<Download> replacements,
        CancellationToken cancellationToken)
    {
        if (replacements.Count == 0) return null;
        var replacedSources = replacements.Select(item => item.Source.Id).ToHashSet(StringComparer.Ordinal);
        var library = new ProfileLibrary(
            profiles.Where(profile => !replacedSources.Contains(profile.SourceId))
                .Concat(replacements.SelectMany(item => item.Profiles)).ToArray(),
            emptySubscriptions.Where(source => !replacedSources.Contains(source.SourceId)).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        try { store.SaveLibrary(library); }
        catch { return null; }
        return library;
    }
}
