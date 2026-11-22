using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using static DiTunnel.App.ViewModels.ProfileCatalog;

namespace DiTunnel.App.ViewModels;

public sealed partial class MainViewModel
{
    partial void OnSelectedGroupChanged(ProfileGroup? value)
    {
        if (value is not null && selectionSettings.SelectedSourceId != value.Id)
        {
            selectionSettings.SelectedSourceId = value.Id;
            try { persistSelection(); } catch { }
        }
        var previous = SelectedProfile?.Profile;
        ServerItemViewModel[] rows;
        restoringProfileSelection = true;
        try
        {
            rows = catalog.ShowGroup(value?.Id, CreateProfileRow);
            selectionSettings.SelectedServers.TryGetValue(value?.Id ?? "", out var saved);
            SelectedProfile = catalog.RestoreSelection(previous, saved, activeProfile?.Profile);
        }
        finally { restoringProfileSelection = false; }
        NotifyConnectionPresentation();
        NotifyProfileListChanged();
        OnPropertyChanged(nameof(SelectedSubscriptionUrl)); OnPropertyChanged(nameof(CanShareSubscription));
        OnPropertyChanged(nameof(CanRefreshSelectedSubscription));
        _ = ResolveCountriesAsync(rows);
    }

    private ServerItemViewModel CreateProfileRow(ImportedProfile profile) => probeHistory.CreateRow(profile);

    private void NotifyProfileListChanged()
    {
        OnPropertyChanged(nameof(ProfileCount));
        OnPropertyChanged(nameof(CanProbeAll));
        OnPropertyChanged(nameof(UnavailableProfileCount));
        OnPropertyChanged(nameof(CanRemoveUnavailable));
        OnPropertyChanged(nameof(IsSelectedSubscriptionEmpty));
    }
    private async Task ResolveCountriesAsync(ServerItemViewModel[] rows)
    {
        if (countryResolver is null) return;
        foreach (var row in rows)
        {
            if (lifetimeCancellation.IsCancellationRequested) return;
            if (row.CountryCode is not null) continue;
            string? code;
            try { code = await countryResolver.ResolveAsync(row.Profile, lifetimeCancellation.Token); }
            catch { continue; }
            Dispatcher.UIThread.Post(() =>
            {
                var visible = catalog.FindVisible(row.Profile);
                if (visible is null || !ReferenceEquals(visible.Profile, row.Profile)) return;
                probeHistory.RecordCountry(visible.Profile, code);
                visible.CountryCode = code;
                var displayed = DisplayProfile;
                if (displayed is not null && SameProfile(displayed.Profile, visible.Profile))
                {
                    displayed.CountryCode = code;
                    OnPropertyChanged(nameof(SelectedFlag));
                    OnPropertyChanged(nameof(HasSelectedFlag));
                }
            });
        }
    }
    partial void OnSelectedProfileChanged(ServerItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasConnectionError));
        RefreshSubscriptionPresentation();
        if (value is not null && !restoringProfileSelection)
        {
            var selected = ServerSelection.From(value.Profile);
            if (selectionSettings.SelectedServers.GetValueOrDefault(value.Profile.SourceId) != selected)
            {
                selectionSettings.SelectedServers[value.Profile.SourceId] = selected;
                try { persistSelection(); } catch { }
            }
        }
        OnPropertyChanged(nameof(SelectedFlag)); OnPropertyChanged(nameof(HasSelectedFlag));
        OnPropertyChanged(nameof(SelectedName)); OnPropertyChanged(nameof(SelectedSummary)); OnPropertyChanged(nameof(SubscriptionLimits)); OnPropertyChanged(nameof(ConnectionHint)); OnPropertyChanged(nameof(HasDisplayDelay)); OnPropertyChanged(nameof(DelayBrush)); OnPropertyChanged(nameof(ConnectionHintBrush)); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanProbeAll));
        OnPropertyChanged(nameof(SelectedServerUri)); OnPropertyChanged(nameof(CanShareServer));
    }

    private void RebuildGroups(string? preferred = null)
    {
        var groupToSelect = catalog.RebuildGroups(preferred ?? SelectedGroup?.Id ?? selectionSettings.SelectedSourceId);
        // A record-equal selection does not raise a property change after importing into an existing group.
        if (SelectedGroup == groupToSelect)
        {
            OnSelectedGroupChanged(SelectedGroup);
            OnPropertyChanged(nameof(SelectedGroup));
        }
        else SelectedGroup = groupToSelect;
    }
    [RelayCommand] private void OpenImport() { if (!CanImport) return; ImportMessage = ""; IsManualImportOpen = false; IsImportOpen = true; }
    [RelayCommand] private void OpenManualImport() { if (!CanImport) return; ImportMessage = ""; IsManualImportOpen = true; }
    [RelayCommand] private void CloseImport() { if (!IsBusy) { IsImportOpen = false; IsManualImportOpen = false; ImportText = ""; ImportName = ""; } }

    [RelayCommand] private void RemoveProfile()
    {
        if (SelectedProfile is not { } selected || !CanManageProfiles) return;
        bool Matches(ImportedProfile profile) => SameProfile(profile, selected.Profile);
        if (activeProfile is { } active && Matches(active.Profile) && ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Error)
        {
            _ = SwitchOrDisconnectAndRemoveAsync(Matches, "Сервер удалён.");
            return;
        }
        RemoveWhere(Matches, "Сервер удалён.");
    }

    public Task<IReadOnlyList<InstalledApplication>> GetInstalledApplicationsAsync(CancellationToken cancellationToken = default) =>
        installedApplicationProvider?.GetInstalledApplicationsAsync(cancellationToken)
        ?? Task.FromResult<IReadOnlyList<InstalledApplication>>([]);

    public async Task InitializeSplitDefaultsAsync()
    {
        if (installedApplicationProvider is null) return;
        try
        {
            if (await UserSettings.Current.InitializeSplitTunnelDefaultsAsync(() => GetInstalledApplicationsAsync()))
                await ApplyNetworkSettingsAsync();
        }
        catch { Notice = "Не удалось применить начальные настройки раздельного туннелирования."; }
    }
    [RelayCommand] private void RemoveGroup()
    {
        if (SelectedGroup is not { } group || !CanManageProfiles) return;
        if (activeProfile?.Profile.SourceId == group.Id && ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Error)
        {
            _ = SwitchOrDisconnectAndRemoveAsync(p => p.SourceId == group.Id, "Профиль и все его серверы удалены.", group.Id);
            return;
        }
        RemoveWhere(p => p.SourceId == group.Id, "Профиль и все его серверы удалены.", deletedGroupId: group.Id);
    }
    [RelayCommand] private void RemoveUnavailable()
    {
        var sourceId = SelectedGroup?.Id;
        var unavailable = Profiles.Where(server => server.IsUnavailable)
            .Select(server => server.Profile.Content).ToHashSet(StringComparer.Ordinal);
        if (sourceId is null || unavailable.Count == 0 || !CanRemoveUnavailable) return;
        bool Predicate(ImportedProfile profile) => profile.SourceId == sourceId && unavailable.Contains(profile.Content);
        var message = $"Удалено недоступных серверов: {unavailable.Count}.";
        if (activeProfile is not null && Predicate(activeProfile.Profile) && ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Error)
            _ = SwitchOrDisconnectAndRemoveAsync(Predicate, message);
        else RemoveWhere(Predicate, message);
    }
    private async Task SwitchOrDisconnectAndRemoveAsync(Func<ImportedProfile, bool> predicate, string message, string? deletedGroupId = null)
    {
        if (engine is null) return;
        var selectedSourceId = SelectedGroup?.Id;
        IsConnecting = true;
        try
        {
            var remaining = catalog.Library.Profiles.Where(profile => !predicate(profile));
            var best = remaining
                .Select(profile => (Profile: profile, Delay: Profiles.FirstOrDefault(row => row.Profile.Content == profile.Content)?.ProbeMilliseconds))
                .OrderBy(candidate => candidate.Profile.SourceId == SelectedGroup?.Id ? 0 : 1)
                .ThenBy(candidate => candidate.Delay ?? double.MaxValue)
                .ThenBy(candidate => candidate.Profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.Profile)
                .FirstOrDefault();
            if (best is null)
            {
                Notice = "Отключаем VPN перед удалением последнего профиля…";
                await engine.DisconnectAsync();
                activeProfile = null;
            }
            else
            {
                Notice = $"Переключаем VPN на сервер {best.Name} перед удалением…";
                await engine.SwitchAsync(best);
                activeProfile = Profiles.FirstOrDefault(row => row.Profile == best) ?? new ServerItemViewModel(best);
            }
            ConnectionState = engine.Status.State;
            RemoveWhere(predicate, message, deletedGroupId is null ? selectedSourceId : best?.SourceId, deletedGroupId);
        }
        catch (Exception error) { Notice = error is InvalidOperationException ? error.Message : "Не удалось отключить VPN; профиль не удалён."; }
        finally { IsConnecting = false; }
    }
    private void RemoveWhere(Func<ImportedProfile, bool> predicate, string message, string? preferredSourceId = null, string? deletedGroupId = null)
    {
        try
        {
            if (!storageAvailable) throw new IOException();
            var removedProfiles = catalog.RemoveProfiles(predicate, deletedGroupId);
            preferredSourceId ??= SelectedGroup?.Id ?? activeProfile?.Profile.SourceId;
            foreach (var removed in removedProfiles) probeHistory.Remove(removed);
            if (IsProbing) profilesRemovedDuringProbe = true;
            if (deletedGroupId is null && SelectedGroup is { } selectedGroup && selectedGroup.Id == preferredSourceId &&
                (selectedGroup.SourceUrl is not null || catalog.Library.Profiles.Any(profile => profile.SourceId == selectedGroup.Id)))
                RemoveDisplayedProfiles(predicate);
            else RebuildGroups(preferredSourceId);
            if (ConnectionState == VpnConnectionState.Connected && activeProfile is { } running)
                activeProfile = catalog.FindVisible(running.Profile) ?? running;
            Notice = message;
        }
        catch { Notice = "Не удалось сохранить изменения. Профили не удалены."; }
    }

    private void RemoveDisplayedProfiles(Func<ImportedProfile, bool> predicate)
    {
        var selected = SelectedProfile;
        restoringProfileSelection = true;
        try
        {
            catalog.RemoveVisibleProfiles(predicate);
            SelectedProfile = selected is not null && Profiles.Contains(selected) ? selected
                : catalog.FindVisible(activeProfile?.Profile) ?? Profiles.FirstOrDefault();
        }
        finally { restoringProfileSelection = false; }
        UpdateHighlightedProfiles();
        NotifyProfileListChanged();
    }
    [RelayCommand] private async Task ImportAsync()
    {
        if (!CanImport) return;
        IsBusy = true; ImportMessage = "Проверяем конфигурацию…";
        try
        {
            var input = ImportText.Trim();
            var importer = new ProfileImporter();
            var isSubscription = Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme == "https";
            var sourceUrl = isSubscription ? new UriBuilder(uri!) { Fragment = "" }.Uri.AbsoluteUri : null;
            var existing = sourceUrl is null ? null : Groups.FirstOrDefault(group => group.SourceUrl == sourceUrl);
            var sourceId = existing?.Id ?? Guid.NewGuid().ToString("N");
            var fragmentName = isSubscription && !string.IsNullOrWhiteSpace(uri!.Fragment)
                ? Uri.UnescapeDataString(uri.Fragment.TrimStart('#')).Trim()
                : null;
            var pathName = isSubscription
                ? Uri.UnescapeDataString(uri!.Segments.LastOrDefault()?.Trim('/') ?? "").Trim()
                : null;
            var sourceName = string.IsNullOrWhiteSpace(ImportName)
                ? existing?.Name ?? fragmentName ?? (string.IsNullOrWhiteSpace(pathName) ? null : pathName)
                    ?? (sourceUrl is null ? "Ручной профиль" : "Подписка") + $" {Groups.Count + 1}"
                : ImportName.Trim();
            var imported = await importer.ImportAsync(input, subscriptionName: sourceName);
            if (!storageAvailable) { ImportMessage = "Хранилище недоступно. Сохранённый файл защищён от перезаписи."; return; }
            var added = catalog.AddImportedProfiles(new(sourceId, sourceName, sourceUrl), imported);
            RebuildGroups(ConnectionState == VpnConnectionState.Connected ? SelectedGroup?.Id : sourceId);
            Notice = added == 0 ? "Эти серверы уже есть в подписке."
                : importer.SkippedEntries > 0 ? $"Добавлено серверов: {added}. Пропущено записей: {importer.SkippedEntries}."
                : $"Добавлено серверов: {added}.";
            ImportText = ""; ImportName = ""; IsImportOpen = false;
        }
        catch (FormatException e) { ImportMessage = e.Message; }
        catch (HttpRequestException) { ImportMessage = "Не удалось импортировать. Проверьте адрес и доступность подписки."; }
        catch (OperationCanceledException) { ImportMessage = "Не удалось импортировать: сервер не ответил вовремя."; }
        catch { ImportMessage = "Не удалось импортировать или сохранить профили. Существующие профили сохранены."; }
        finally { IsBusy = false; }
    }

    private void SortProfilesByProbe()
    {
        if (profilesRemovedDuringProbe) return;
        var selected = SelectedProfile;
        restoringProfileSelection = true;
        try
        {
            if (!catalog.SortByProbe()) return;
            if (ReferenceEquals(SelectedProfile, selected)) OnPropertyChanged(nameof(SelectedProfile));
            else SelectedProfile = selected;
            ProfileOrderChanged?.Invoke();
        }
        finally { restoringProfileSelection = false; }
    }
}
