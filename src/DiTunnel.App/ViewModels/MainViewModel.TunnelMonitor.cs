using DiTunnel.Core.Connection;
using static DiTunnel.App.ViewModels.ProfileCatalog;

namespace DiTunnel.App.ViewModels;

public sealed partial class MainViewModel
{
    private void StartActiveLatencyChecks() =>
        tunnelMonitor.Start(lifetimeCancellation.Token, () => IsBusy || IsConnecting || IsProbing, CheckActiveConnectionAsync);

    internal async Task CheckActiveConnectionAsync(CancellationToken cancellationToken)
    {
        var server = activeProfile ?? SelectedProfile;
        if (server is null || IsProbing || ConnectionState != VpnConnectionState.Connected ||
            !tunnelMonitor.TryBeginCheck()) return;
        var measuresTunnel = tunnelMonitor.MeasuresTunnel;
        var connectedAt = engine?.Status.ConnectedAt;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            var result = await tunnelMonitor.MeasureAsync(server.Profile, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ConnectionState != VpnConnectionState.Connected || engine?.Status.ConnectedAt != connectedAt ||
                IsConnecting || IsProbing || (activeProfile ?? SelectedProfile) is not { } current
                || !SameProfile(current.Profile, server.Profile)) return;
            if (result.IsDeferred)
            {
                tunnelMonitor.ObserveTransportResult(result);
                var offline = result.Diagnostics?.ErrorType == "Offline";
                if (!offline &&
                    (tunnelMonitor.IsConfirmed || probeHistory.Get(server.Profile).ConnectionResult is not null)) return;
                if (offline)
                {
                    tunnelMonitor.SetVerification(false, null);
                    NotifyConnectionDelay();
                }
                if (measuresTunnel) RememberConnectionProbe(server.Profile, result);
                NotifyConnectionPresentation();
                if (!IsBusy && result.Diagnostics is not null)
                {
                    SetActiveProbeFailures(0);
                    SetOperationNotice("Туннель запущен. " + result.Message, result.Diagnostics);
                }
                return;
            }
            if (!measuresTunnel) ApplyProbeResult(server, result);
            else
            {
                tunnelMonitor.SetConfirmed(result.Diagnostics?.ProfileConfirmed == true);
                RememberConnectionProbe(server.Profile, result);
            }
            tunnelMonitor.Delay = result.Milliseconds;
            var failed = result.Milliseconds is null;
            tunnelMonitor.ObserveTransportResult(result);
            SetActiveProbeFailures(failed ? Math.Min(2, tunnelMonitor.Failures + 1) : 0);
            if (!failed && measuresTunnel)
            {
                tunnelMonitor.ResetRecovery();
            }
            NotifyConnectionPresentation();
            NotifyConnectionDelay();
            if (IsBusy) return;
            if (!failed && !measuresTunnel && engine?.Status is { State: VpnConnectionState.Connected, Message: { } networkMessage })
                SetOperationNotice(networkMessage);
            else
                SetOperationNotice(failed ? "Туннель запущен. " + result.Message
                    : measuresTunnel ? "VPN подключён." : "Туннель запущен. Профиль проверен.", result.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch { return; }
        finally
        {
            tunnelMonitor.FinishCheck();
            OnPropertyChanged(nameof(StatusText));
        }
        if (engine is null || IsBusy || IsConnecting || cancellationToken.IsCancellationRequested) return;
        var recovery = tunnelMonitor.GetRecoveryAction(IsLowestMode);
        if (recovery == ActiveTunnelMonitor.RecoveryAction.Restart)
        {
            tunnelMonitor.MarkRecoveryAttempted();
            await ReconnectSelectedProfileAsync(server, restartCurrent: true);
            return;
        }
        if (recovery != ActiveTunnelMonitor.RecoveryAction.Failover) return;
        var alternative = LowestProfileMonitor.SelectBest(Profiles, candidate =>
            candidate.Profile.SourceId == server.Profile.SourceId && candidate.Profile.Content != server.Profile.Content &&
            IsUsableProfile(candidate) && probeHistory.Get(candidate.Profile).Diagnostics?.ProfileConfirmed == true);
        if (alternative is null) return;
        tunnelMonitor.MarkFailoverAttempted();
        await ReconnectSelectedProfileAsync(alternative);
    }

    private bool IsUsableProfile(ServerItemViewModel server) => server.ProbeMilliseconds is not null
        && server.Profile.Usage?.IsExhausted(DateTimeOffset.UtcNow) != true
        && (Platform.ReleaseTarget != ReleaseTarget.AndroidArm64
            || probeHistory.Find(server.Profile)?.Diagnostics?.ProfileConfirmed == true);
}
