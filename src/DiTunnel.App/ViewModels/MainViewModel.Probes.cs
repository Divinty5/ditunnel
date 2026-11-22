using CommunityToolkit.Mvvm.Input;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using static DiTunnel.App.ViewModels.ProfileCatalog;

namespace DiTunnel.App.ViewModels;

public sealed partial class MainViewModel
{
    [RelayCommand] private Task ProbeSelectedAsync() => RunProbesAsync(false);
    [RelayCommand] private Task ProbeAllAsync() => RunProbesAsync(true);
    [RelayCommand] private void CancelProbes() => probeCancellation?.Cancel();

    private async Task RunProbesAsync(bool all)
    {
        if (all ? !CanProbeAll : !CanProbe) return;
        var servers = all ? Profiles.ToArray() : [SelectedProfile!];
        using var cancellation = new CancellationTokenSource();
        probeCancellation = cancellation;
        IsProbing = true;
        try
        {
            SetOperationNotice(servers.Length == 1 ? $"Проверяем сервер {servers[0].Name}…" : $"Проверяем серверы: {servers.Length}…");
            var results = await ProbeServersAsync(servers, cancellation.Token);
            SortProfilesByProbe();
            if (all && results[0].IsDeferred && results.All(result => result.IsDeferred && result.Message == results[0].Message))
            {
                SetOperationNotice(results[0].Message, results[0].Diagnostics);
                return;
            }
            var changed = all && IsLowestMode && await SelectLowestProfileAsync();
            if (!changed && all)
            {
                var deferred = results.Count(result => result.IsDeferred);
                Notice = $"Проверены: {results.Length - deferred}. Ответили: {results.Count(result => result.Milliseconds is not null)}. Ошибки: {results.Count(result => !result.IsDeferred && result.Milliseconds is null)}."
                    + (deferred > 0 ? $" Пропущены: {deferred}." : "");
            }
            else if (!changed) SetOperationNotice($"{servers[0].Name}: {results[0].Message}.", results[0].Diagnostics);
        }
        catch (OperationCanceledException) { SetOperationNotice("Проверка отменена."); }
        catch { SetOperationNotice("Не удалось выполнить проверку сервера."); }
        finally
        {
            ClearPendingProbes();
            probeCancellation = null;
            IsProbing = false;
        }
    }
    private void ApplyProbeResult(ServerItemViewModel server, ServerProbeResult result)
    {
        var visible = probeHistory.RecordProfileResult(server, result);
        if (ConnectionState == VpnConnectionState.Connected && activeProfile is { } active && SameProfile(visible.Profile, active.Profile) && !tunnelMonitor.MeasuresTunnel &&
            Platform.ReleaseTarget != ReleaseTarget.AndroidArm64)
            tunnelMonitor.Delay = result.Milliseconds;
        OnPropertyChanged(nameof(UnavailableProfileCount));
        OnPropertyChanged(nameof(CanRemoveUnavailable));
        OnPropertyChanged(nameof(HasConnectionError));
        if (visible == SelectedProfile)
        {
            OnPropertyChanged(nameof(ConnectionHint)); OnPropertyChanged(nameof(HasDisplayDelay));
            OnPropertyChanged(nameof(DelayBrush));
            OnPropertyChanged(nameof(ConnectionHintBrush));
        }
    }

    private void ApplyPreciseProfileFailure(ServerItemViewModel server, ServerProbeResult result, ServerProbeMode mode)
    {
        var running = activeProfile ?? SelectedProfile;
        if (mode != ServerProbeMode.Https || result.IsDeferred || result.Milliseconds is not null ||
            ConnectionState != VpnConnectionState.Connected || running is null ||
            !SameProfile(running.Profile, server.Profile)) return;
        if (!tunnelMonitor.MeasuresTunnel) SetActiveProbeFailures(Math.Max(1, tunnelMonitor.Failures));
        StartActiveLatencyChecks();
    }

    private void SetActiveProbeFailures(int failures)
    {
        if (!tunnelMonitor.SetFailures(failures)) return;
        NotifyConnectionPresentation();
        NotifyConnectionDelay();
    }

    private void NotifyConnectionDelay()
    {
        OnPropertyChanged(nameof(ConnectionHint));
        OnPropertyChanged(nameof(HasDisplayDelay));
        OnPropertyChanged(nameof(DelayBrush));
        OnPropertyChanged(nameof(ConnectionHintBrush));
    }

    private void RememberConnectionProbe(ImportedProfile profile, ServerProbeResult result, bool connectionAttempt = false)
    {
        if (!probeHistory.RecordConnectionResult(profile, result, connectionAttempt)) return;
        OnPropertyChanged(nameof(UnavailableProfileCount));
        OnPropertyChanged(nameof(CanRemoveUnavailable));
    }

    private void ClearPendingProbes()
    {
        if (!probeHistory.ClearPending()) return;
        OnPropertyChanged(nameof(UnavailableProfileCount));
        OnPropertyChanged(nameof(CanRemoveUnavailable));
    }

    public void ToggleProbeMode()
    {
        UserSettings.Current.ServerProbeMode = UserSettings.Current.ServerProbeMode == ServerProbeMode.Fast ? ServerProbeMode.Https : ServerProbeMode.Fast;
        try { UserSettings.Current.Save(); Notice = "Тип проверки изменён."; }
        catch { Notice = "Не удалось сохранить тип проверки."; }
    }

    private Task<ServerProbeResult[]> ProbeServersAsync(ServerItemViewModel[] servers, CancellationToken cancellationToken)
    {
        profilesRemovedDuringProbe = false;
        probeHistory.MarkPending(servers);
        OnPropertyChanged(nameof(UnavailableProfileCount));
        OnPropertyChanged(nameof(CanRemoveUnavailable));
        var mode = UserSettings.Current.ServerProbeMode;
        return profileProbeCoordinator.RunAsync(servers.Select(server => server.Profile).ToArray(), mode, (index, result) =>
        {
            ApplyProbeResult(servers[index], result);
            ApplyPreciseProfileFailure(servers[index], result, mode);
        }, cancellationToken);
    }
}
