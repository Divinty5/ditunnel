namespace DiTunnel.Core.Connection;

/// <summary>
/// User-selected protection and lifecycle behavior. Platform implementations must not claim
/// that a setting is active until every required OS rule has been installed successfully.
/// </summary>
public sealed record ConnectionPolicy(
    bool KillSwitchEnabled,
    bool AllowLocalNetwork,
    bool StartWithWindows,
    bool AutoConnect)
{
    public static ConnectionPolicy Default { get; } = new(false, false, false, false);

    // AutoConnect also applies when the user starts the app manually; StartWithWindows only controls launch.
    public ConnectionPolicy Normalize() => this;
}

public enum VpnRecoveryReason
{
    None,
    NetworkLost,
    NetworkChanged,
    SleepResume,
    TunnelProcessExited
}

public sealed record ReconnectSchedule(int Attempt, TimeSpan Delay)
{
    public static ReconnectSchedule? Create(ConnectionPolicy policy, int failedAttempts)
    {
        if (!policy.AutoConnect || failedAttempts < 0) return null;
        var seconds = Math.Min(30, Math.Pow(2, Math.Min(failedAttempts, 5)));
        return new(failedAttempts + 1, TimeSpan.FromSeconds(seconds));
    }

    public static ReconnectSchedule? CreateForActiveTunnel(int failedAttempts)
    {
        if (failedAttempts >= 10) return null;
        var seconds = Math.Min(30, Math.Pow(2, Math.Min(Math.Max(0, failedAttempts), 5)));
        return new(Math.Max(0, failedAttempts) + 1, TimeSpan.FromSeconds(seconds));
    }
}
