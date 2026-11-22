using DiTunnel.Core.Connection;
using static DiTunnel.App.ViewModels.ProfileCatalog;

namespace DiTunnel.App.ViewModels;

public sealed partial class MainViewModel
{
    public void SetLowestMode(bool enabled)
    {
        selectionSettings.LowestMode = enabled;
        try { selectionSettings.Save(); } catch { Notice = "Не удалось сохранить настройки режима lowest."; return; }
        OnPropertyChanged(nameof(IsLowestMode));
        if (enabled) StartLowestMode(checkImmediately: true);
        else
        {
            lowestMonitor.Stop();
            Notice = "Режим lowest выключен.";
        }
    }

    private void StartLowestMode(bool checkImmediately) =>
        lowestMonitor.Start(lifetimeCancellation.Token, checkImmediately, CheckLowestAsync,
            minutes => Notice = $"Lowest: следующая проверка через {minutes} мин.");

    internal async Task<bool> CheckLowestAsync(CancellationToken cancellationToken)
    {
        if (!CanProbeAll) return false;
        var servers = Profiles.ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation = cancellation;
        IsProbing = true;
        try
        {
            await ProbeServersAsync(servers, cancellation.Token);
            SortProfilesByProbe();
            return await SelectLowestProfileAsync();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { Notice = "Проверка отменена."; return false; }
        catch (OperationCanceledException) { throw; }
        catch { Notice = "Lowest: не удалось проверить серверы."; return false; }
        finally
        {
            ClearPendingProbes();
            if (ReferenceEquals(probeCancellation, cancellation)) probeCancellation = null;
            IsProbing = false;
        }
    }

    private async Task<bool> SelectLowestProfileAsync()
    {
        if (ConnectionState == VpnConnectionState.Connected && !tunnelMonitor.CanSwitchForLowest) return false;
        var best = LowestProfileMonitor.SelectBest(Profiles, IsUsableProfile);
        if (best is null) return false;
        if (ConnectionState == VpnConnectionState.Connected)
        {
            if (activeProfile is { } active && SameProfile(active.Profile, best.Profile)) return false;
        }
        else
        {
            if (best == SelectedProfile) return false;
            SelectedProfile = best;
        }
        Notice = $"Lowest выбрал сервер {best.Name}.";
        if (ConnectionState == VpnConnectionState.Connected)
            await ReconnectSelectedProfileSafelyAsync(best);
        return true;
    }
}
