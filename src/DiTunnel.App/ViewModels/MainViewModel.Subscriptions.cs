using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DiTunnel.Core.Profiles;
using static DiTunnel.App.ViewModels.ProfileCatalog;

namespace DiTunnel.App.ViewModels;

public sealed partial class MainViewModel
{
    private int refreshToastGeneration;
    private bool refreshingSubscriptions;
    [ObservableProperty] private string subscriptionStatus = "";
    [ObservableProperty] private string refreshToast = "";
    [ObservableProperty] private bool isRefreshToastVisible;
    [ObservableProperty] private IBrush refreshToastBackground = new SolidColorBrush(Color.Parse("#D92EBE39"));

    public Task RefreshSubscriptionsAsync(Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download = null, bool showToast = true)
        => RefreshSubscriptionsCoreAsync(sourceId: null, download, showToast, includeEmpty: true);

    internal Task RefreshSubscriptionsOnStartupAsync(Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download = null)
        => RefreshSubscriptionsCoreAsync(sourceId: null, download, showToast: false, includeEmpty: false);

    public Task RefreshSelectedSubscriptionAsync(Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download = null)
        => CanRefreshSelectedSubscription && SelectedGroup is { } group
            ? RefreshSubscriptionsCoreAsync(group.Id, download, showToast: true, includeEmpty: true) : Task.CompletedTask;

    private async Task RefreshSubscriptionsCoreAsync(string? sourceId, Func<string, CancellationToken, Task<IReadOnlyList<ImportedProfile>>>? download,
        bool showToast, bool includeEmpty)
    {
        if (!CanRefreshSubscriptions || !storageAvailable) return;
        var sources = Groups.Where(g => g.SourceUrl is not null && (sourceId is null || g.Id == sourceId)).ToArray();
        if (!includeEmpty)
        {
            var populatedSources = catalog.Library.Profiles.Select(profile => profile.SourceId).ToHashSet(StringComparer.Ordinal);
            sources = sources.Where(group => populatedSources.Contains(group.Id)).ToArray();
        }
        if (sources.Length == 0) return;
        var initialProfiles = catalog.Library.Profiles.ToLookup(profile => profile.SourceId, StringComparer.Ordinal);
        var selectedOnly = sourceId is not null;
        var useDirectRoute = download is null && selectionSettings.RefreshSubscriptionsDirectly;
        var directBlocked = useDirectRoute && Platform.SupportsKillSwitch && engine?.IsNetworkProtectionActive == true;
        refreshingSubscriptions = true;
        OnPropertyChanged(nameof(CanRefreshSubscriptions));
        OnPropertyChanged(nameof(CanRefreshSelectedSubscription));
        if (showToast) IsBusy = true;
        var previousStatus = SubscriptionStatus;
        SubscriptionStatus = selectedOnly ? "Обновляем подписку…" : "Обновляем подписки…";
        try
        {
            var token = lifetimeCancellation.Token;
            var downloads = await SubscriptionRefresh.DownloadAsync(sources, download, useDirectRoute,
                directBlocked ? null : directSubscriptionClient, token);
            token.ThrowIfCancellationRequested();
            // Background refresh must not replace the selected profile while a connection
            // or another profile operation is using it.
            if (!showToast && (IsBusy || HasPendingOperation))
            {
                SubscriptionStatus = previousStatus;
                return;
            }
            var result = SubscriptionRefresh.Apply(catalog, initialProfiles, downloads, token);
            var updated = result.UpdatedCount;
            var failed = downloads.Length - updated;
            if (result.Saved)
            {
                foreach (var removed in result.RemovedProfiles) probeHistory.Remove(removed);
                RebuildGroups();
                if (activeProfile is { } active)
                {
                    var profiles = catalog.Library.Profiles;
                    var refreshed = profiles.FirstOrDefault(profile => SameProfile(profile, active.Profile));
                    if (refreshed is null && profiles.FirstOrDefault(profile => profile.SourceId == active.Profile.SourceId) is { } sourcePeer)
                        refreshed = active.Profile with { Usage = sourcePeer.Usage };
                    if (refreshed is not null && refreshed != active.Profile)
                        activeProfile = Profiles.FirstOrDefault(row => row.Profile == refreshed) ?? new ServerItemViewModel(refreshed);
                }
                RefreshSubscriptionPresentation();
            }
            var summary = selectedOnly
                ? failed == 0 ? "Подписка успешно обновлена" : "Не удалось обновить подписку"
                : failed == 0 ? $"Подписки успешно обновлены: {updated}"
                : updated == 0 ? $"Не удалось обновить: {failed}" : $"Обновлено: {updated}. Не удалось обновить: {failed}";
            var status = failed == 0 ? $"{summary} · {DateTime.Now:HH:mm}" : $"{summary}. Сохранены прежние серверы.";
            var invalidFormats = downloads.Count(item => item.Error is FormatException);
            var downloadErrors = downloads.Count(item => item.Error is HttpRequestException or OperationCanceledException);
            var directFailed = useDirectRoute && updated == 0 && downloads.All(item =>
                item.Error is HttpRequestException { StatusCode: null } or OperationCanceledException);
            if (invalidFormats > 0) status += $" Ошибки формата: {invalidFormats}.";
            if (downloadErrors > 0) status += $" Ошибки загрузки: {downloadErrors}.";
            if (directFailed)
                status = directBlocked ? "Прямое обновление подписок заблокировано защитой VPN."
                    : "Прямое обновление подписок недоступно. Проверьте физическое подключение к сети.";
            SubscriptionStatus = status;
            if (showToast)
            {
                RefreshToast = directFailed ? status : summary;
                RefreshToastBackground = new SolidColorBrush(Color.Parse(failed == 0 ? "#D92EBE39" : "#D9C8324A"));
                IsRefreshToastVisible = true;
                _ = HideRefreshToastAsync(++refreshToastGeneration);
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        finally
        {
            refreshingSubscriptions = false;
            if (showToast) IsBusy = false;
            OnPropertyChanged(nameof(CanRefreshSubscriptions));
            OnPropertyChanged(nameof(CanRefreshSelectedSubscription));
        }
    }

    private async Task HideRefreshToastAsync(int generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), lifetimeCancellation.Token);
            if (generation == refreshToastGeneration) IsRefreshToastVisible = false;
        }
        catch (OperationCanceledException) { }
    }
}
