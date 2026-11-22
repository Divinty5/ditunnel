using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.ViewModels;

internal sealed class ActiveTunnelMonitor(IServerProbe? probe)
{
    private CancellationTokenSource? loop;
    private int transportFailures;
    private bool failoverAttempted;

    public bool MeasuresTunnel => probe is IActiveTunnelProbe;
    public bool IsChecking { get; private set; }
    public bool IsConfirmed { get; private set; }
    public double? Delay { get; set; }
    public int Failures { get; private set; }
    private bool RecoveryAttempted { get; set; }
    public bool CanSwitchForLowest => Failures == 0 && !RecoveryAttempted;

    internal enum RecoveryAction { None, Restart, Failover }

    public RecoveryAction GetRecoveryAction(bool lowestEnabled)
    {
        if (transportFailures < 2) return RecoveryAction.None;
        if (!RecoveryAttempted) return RecoveryAction.Restart;
        return lowestEnabled && !failoverAttempted ? RecoveryAction.Failover : RecoveryAction.None;
    }

    public void Start(CancellationToken lifetime, Func<bool> isBusy, Func<CancellationToken, Task> check)
    {
        Stop();
        if (probe is null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        loop = cancellation;
        _ = RunAsync(cancellation.Token, isBusy, check);
    }

    public void Stop()
    {
        var cancellation = loop;
        loop = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task RunAsync(CancellationToken token, Func<bool> isBusy, Func<CancellationToken, Task> check)
    {
        try
        {
            var firstCheck = true;
            while (!token.IsCancellationRequested)
            {
                var delay = firstCheck ? TimeSpan.FromSeconds(1) : Failures == 1
                    ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(1);
                await Task.Delay(delay, token);
                while (isBusy() || IsChecking)
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                firstCheck = false;
                await check(token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public bool TryBeginCheck()
    {
        if (probe is null || IsChecking) return false;
        IsChecking = true;
        return true;
    }

    public void FinishCheck() => IsChecking = false;

    public void SetConfirmed(bool confirmed) => IsConfirmed = confirmed;

    public Task<ServerProbeResult> MeasureAsync(ImportedProfile profile, CancellationToken token) =>
        probe is IActiveTunnelProbe tunnel ? tunnel.ProbeActiveTunnelAsync(token)
            : probe!.ProbeAsync(profile, token, ServerProbeMode.Https);

    public void SetVerification(bool confirmed, double? delay)
    {
        IsConfirmed = confirmed;
        Delay = delay;
    }

    public void ResetFailures()
    {
        Failures = 0;
        transportFailures = 0;
    }

    public bool SetFailures(int failures)
    {
        if (Failures == failures) return false;
        Failures = failures;
        return true;
    }

    public void ObserveTransportResult(ServerProbeResult result) =>
        transportFailures = MeasuresTunnel && !result.IsDeferred && result.Diagnostics?.ErrorType is "Timeout" or "Connection"
            ? Math.Min(2, transportFailures + 1) : 0;

    public void ResetRecovery()
    {
        RecoveryAttempted = false;
        failoverAttempted = false;
    }

    public void MarkRecoveryAttempted() => RecoveryAttempted = true;

    public void MarkFailoverAttempted() => failoverAttempted = true;
}
