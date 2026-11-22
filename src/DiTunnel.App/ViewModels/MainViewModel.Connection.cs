using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using static DiTunnel.App.ViewModels.ProfileCatalog;

namespace DiTunnel.App.ViewModels;

public sealed partial class MainViewModel
{
    public void RefreshConnectionPolicy()
    {
        OnPropertyChanged(nameof(IsKillSwitchEnabled));
        OnPropertyChanged(nameof(KillSwitchText));
        OnPropertyChanged(nameof(ShowKillSwitchStatus));
    }
    public bool CanRestoreNetwork => engine is INetworkRecoveryEngine;
    public async Task RestoreNetworkAsync()
    {
        if (engine is not INetworkRecoveryEngine recovery) return;
        CancelProbes();
        await CancelConnectionAsync();
        await recovery.RestoreNetworkAsync(lifetimeCancellation.Token);
        UserSettings.Current.KillSwitchEnabled = false;
        UserSettings.Current.Save();
        RefreshConnectionPolicy();
    }

    public async Task ApplyNetworkSettingsAsync()
    {
        if (!UserSettings.Current.KillSwitchEnabled && engine?.IsNetworkProtectionActive == true
            && ConnectionState is VpnConnectionState.Disconnected or VpnConnectionState.Error
            && engine is INetworkRecoveryEngine recovery)
        {
            try { await recovery.RestoreNetworkAsync(lifetimeCancellation.Token); RefreshConnectionPolicy(); }
            catch { Notice = "Не удалось восстановить сеть. Повторите с правами администратора."; }
            return;
        }
        var target = activeProfile ?? SelectedProfile;
        if (engine is null || target is null || IsConnecting || ConnectionState != VpnConnectionState.Connected)
            return;

        IsConnecting = true;
        ConnectionState = VpnConnectionState.Reconnecting;
        tunnelMonitor.Delay = null;
        var cancellation = connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        Notice = "Применяем сетевые настройки и безопасно переподключаем VPN…";
        try
        {
            await Task.Delay(150, cancellation.Token);
            SubscriptionAccess.EnsureAvailable(target.Profile);
            await engine.SwitchAsync(target.Profile, cancellation.Token);
            activeProfile = target;
        }
        catch (OperationCanceledException) { Notice = "Применение сетевых настроек отменено."; }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or FormatException or TimeoutException) { SetConnectionExceptionNotice(e); }
        catch { Notice = "Не удалось применить сетевые настройки. VPN отключён; подключитесь снова."; }
        finally
        {
            ConnectionState = engine.Status.State;
            IsConnecting = false;
            cancellation.Dispose();
            if (ReferenceEquals(connectionCancellation, cancellation)) connectionCancellation = null;
        }
    }

    private void EngineStatusChanged(object? sender, VpnStatus status)
    {
        // Capture the profile with its status, before a later switch changes the engine.
        var running = status.Probe is not null && status.State is (VpnConnectionState.Error or VpnConnectionState.Reconnecting)
            ? connectingProfile?.Profile ?? engine?.ActiveProfile ?? activeProfile?.Profile
            : engine?.ActiveProfile;
        Dispatcher.UIThread.Post(() => ApplyEngineStatus(status, running));
    }

    internal void ApplyEngineStatus(VpnStatus status, ImportedProfile? running)
    {
        var profileChanged = running is not null && (activeProfile is null || !SameProfile(activeProfile.Profile, running));
        var previousStatus = lastEngineStatus;
        var statusChanged = previousStatus != status || profileChanged;
        var newSession = status.State == VpnConnectionState.Connected &&
            (previousStatus?.State != status.State || previousStatus.ConnectedAt != status.ConnectedAt || profileChanged);
        var androidVerificationChanged = Platform.ReleaseTarget == ReleaseTarget.AndroidArm64 &&
            previousStatus?.DelayMilliseconds != status.DelayMilliseconds;
        lastEngineStatus = status;
        if (status.State != VpnConnectionState.Connected || newSession)
        {
            tunnelMonitor.SetVerification(false, null);
            tunnelMonitor.ResetFailures();
        }
        if (status.State == VpnConnectionState.Connected)
        {
            var server = !profileChanged
                ? activeProfile
                : catalog.FindVisible(running);
            if (server is null && running is not null)
            {
                var stored = catalog.Library.Profiles.FirstOrDefault(profile => SameProfile(profile, running)) ?? running;
                server = probeHistory.CreateRow(stored, includeConnectionResult: false);
            }
            if (server is not null) activeProfile = server;
            if (androidVerificationChanged && status.DelayMilliseconds is null)
            {
                tunnelMonitor.SetVerification(false, null);
            }
            if (status.Probe is { } check && statusChanged)
            {
                tunnelMonitor.SetConfirmed(check.Diagnostics?.ProfileConfirmed == true);
                tunnelMonitor.Delay = check.Milliseconds;
                if (server is not null) RememberConnectionProbe(server.Profile, check, connectionAttempt: true);
            }
            else if (status.Probe is null && status.DelayMilliseconds is { } delay && (!tunnelMonitor.MeasuresTunnel ||
                androidVerificationChanged || newSession && Platform.ReleaseTarget == ReleaseTarget.AndroidArm64))
            {
                tunnelMonitor.SetConfirmed(Platform.ReleaseTarget == ReleaseTarget.AndroidArm64);
                if (server is not null)
                {
                    server.ProbeText = $"HTTPS · {delay:0} мс";
                    server.ProbeMilliseconds = delay;
                    server.ProbeTimedOut = false;
                    server.IsProbeUnconfirmed = false;
                }
                if (Platform.ReleaseTarget == ReleaseTarget.AndroidArm64 && server is not null)
                    RememberConnectionProbe(server.Profile, ProbePolicy.Success(delay, "HTTPS", "HTTPS", true));
                tunnelMonitor.Delay = delay;
            }
        }
        else if (status.State == VpnConnectionState.Disconnected) activeProfile = null;
        else if (statusChanged && status.State is (VpnConnectionState.Error or VpnConnectionState.Reconnecting) &&
                 status.Probe is { } failure && (running ?? connectingProfile?.Profile) is { } target)
        {
            RememberConnectionProbe(target, failure, connectionAttempt: true);
            if (status.State == VpnConnectionState.Error && connectingProfile is null)
                activeProfile = catalog.FindVisible(target) ?? new ServerItemViewModel(target);
        }
        if (newSession && ConnectionState == status.State) StartActiveLatencyChecks();
        ConnectionState = status.State;
        // Connected -> Connected and external widget switches also need to invalidate
        // the header. Setting SelectedProfile alone still displays the old active server.
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(SelectedFlag));
        OnPropertyChanged(nameof(HasSelectedFlag));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SubscriptionLimits));
        NotifyConnectionDelay();
        if (statusChanged && status.Message is not null) SetEngineOperationNotice(status);
        RefreshSubscriptionPresentation();
    }

    private void SetEngineOperationNotice(VpnStatus status)
    {
        if (status.Message is not { } message) return;
        if (status.Probe?.Diagnostics is { } diagnostics)
            SetOperationNotice(message.Split('\n', 2)[0], diagnostics);
        else SetOperationNotice(message);
    }

    private void SetConnectionExceptionNotice(Exception error)
    {
        if (error is ProbeFailureException { Diagnostics: { } diagnostics })
            SetOperationNotice(error.Message, diagnostics);
        else if (noticeDiagnostics is null || Notice != error.Message.Split('\n', 2)[0])
            SetOperationNotice(error.Message);
    }

    private void NotifyConnectionCancellation()
    {
        OnPropertyChanged(nameof(HasPendingConnection));
        OnPropertyChanged(nameof(HasPendingOperation));
        OnPropertyChanged(nameof(CanCancelConnection));
        OnPropertyChanged(nameof(CancelConnectionText));
        OnPropertyChanged(nameof(CanProbe));
        OnPropertyChanged(nameof(CanProbeAll));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanSelectProfile));
        OnPropertyChanged(nameof(PowerText));
    }

    [RelayCommand] private async Task CancelConnectionAsync()
    {
        if (!CanCancelConnection || engine is null) return;
        IsCancellingConnection = true;
        connectionCancellation?.Cancel();
        Notice = "Отменяем подключение и восстанавливаем сеть…";
        try
        {
            await engine.DisconnectAsync();
            Notice = "Подключение отменено.";
        }
        catch { Notice = "Не удалось отключить VPN; повторите отключение."; }
        finally { ConnectionState = engine.Status.State; IsCancellingConnection = false; }
    }

    internal async Task ActivateProfileFromUserAsync(ServerItemViewModel value)
    {
        if (!CanSelectProfile || !Profiles.Contains(value)) return;
        SelectedProfile = value;
        if (ConnectionState == VpnConnectionState.Connected)
        {
            tunnelMonitor.ResetRecovery();
            await ReconnectSelectedProfileSafelyAsync(value);
        }
        else if (ConnectionState is VpnConnectionState.Disconnected or VpnConnectionState.Error)
            await ConnectAsync();
    }

    private async Task ReconnectSelectedProfileSafelyAsync(ServerItemViewModel target)
    {
        try
        {
            await ReconnectSelectedProfileAsync(target);
        }
        catch
        {
            ConnectionState = engine?.Status.State ?? VpnConnectionState.Error;
            Notice = "Не удалось переключить сервер. VPN отключён; выберите профиль и подключитесь снова.";
        }
    }

    private async Task ReconnectSelectedProfileAsync(ServerItemViewModel target, bool restartCurrent = false)
    {
        if (engine is null || IsConnecting || ConnectionState is not (VpnConnectionState.Connected or VpnConnectionState.Reconnecting or VpnConnectionState.Error)
            || (!restartCurrent && activeProfile is { } active && SameProfile(target.Profile, active.Profile))) return;
        var wasConnected = ConnectionState == VpnConnectionState.Connected;
        var wasRecovering = ConnectionState == VpnConnectionState.Reconnecting;
        connectingProfile = target;
        IsConnecting = true;
        ConnectionState = VpnConnectionState.Reconnecting;
        tunnelMonitor.Delay = null;
        var cancellation = connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        Notice = restartCurrent ? $"Перезапускаем VPN через {target.Name}…" : $"Переключаемся на сервер {target.Name}…";
        try
        {
            if (wasConnected)
            {
                SubscriptionAccess.EnsureAvailable(target.Profile);
                await engine.SwitchAsync(target.Profile, cancellation.Token);
                activeProfile = target;
                return;
            }
            if (wasRecovering)
            {
                await engine.DisconnectAsync(cancellation.Token);
                if (SelectedProfile != target || cancellation.IsCancellationRequested) return;
            }
            if (SelectedProfile != target || cancellation.IsCancellationRequested)
            {
                return;
            }
            SubscriptionAccess.EnsureAvailable(target.Profile);
            await engine.ConnectAsync(target.Profile, cancellation.Token);
            activeProfile = target;
        }
        catch (OperationCanceledException) { SetOperationNotice("Переключение сервера отменено."); }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or FormatException or TimeoutException) { SetConnectionExceptionNotice(e); }
        catch { SetOperationNotice("Не удалось переключить сервер. Предыдущее подключение отключено; выберите сервер и подключитесь снова."); }
        finally
        {
            if (engine.Status.State == VpnConnectionState.Error) activeProfile = target;
            connectingProfile = null;
            ConnectionState = engine.Status.State;
            IsConnecting = false;
            cancellation.Dispose();
            if (ReferenceEquals(connectionCancellation, cancellation)) connectionCancellation = null;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)] private async Task ConnectAsync()
    {
        if (CanCancelConnection) { await CancelConnectionAsync(); return; }
        if (!CanConnect) return;
        if (engine is null) { Notice = "Сетевой движок недоступен на этой платформе."; return; }
        connectingProfile = ConnectionState is VpnConnectionState.Disconnected or VpnConnectionState.Error ? SelectedProfile : activeProfile;
        IsConnecting = true;
        connectionCancellation = new CancellationTokenSource();
        try
        {
            await InitializeSplitDefaultsAsync().WaitAsync(connectionCancellation.Token);
            if (ConnectionState == VpnConnectionState.Connected)
            {
                await engine.DisconnectAsync();
                Notice = engine.Status.Message ?? "VPN отключён.";
            }
            else if (ConnectionState == VpnConnectionState.Reconnecting)
            {
                var target = activeProfile ?? SelectedProfile;
                if (target is null) Notice = "Сначала выберите сервер.";
                else
                {
                    Notice = $"Перезапускаем VPN через {target.Name}…";
                    await engine.DisconnectAsync(connectionCancellation.Token);
                    SubscriptionAccess.EnsureAvailable(target.Profile);
                    await engine.ConnectAsync(target.Profile, connectionCancellation.Token);
                    if (engine.Status.State == VpnConnectionState.Connected) activeProfile = target;
                }
            }
            else if (SelectedProfile is null) Notice = "Сначала выберите сервер.";
            else
            {
                tunnelMonitor.ResetRecovery();
                Notice = "Подключаем VPN…";
                SubscriptionAccess.EnsureAvailable(SelectedProfile.Profile);
                await engine.ConnectAsync(SelectedProfile.Profile, connectionCancellation.Token);
                if (engine.Status.State == VpnConnectionState.Connected)
                {
                    activeProfile = SelectedProfile;
                    if (engine.Status.Message is null) SetOperationNotice("VPN подключён");
                    else SetEngineOperationNotice(engine.Status);
                }
            }
        }
        catch (OperationCanceledException) { SetOperationNotice("Подключение отменено."); }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or NotSupportedException or FormatException) { SetConnectionExceptionNotice(e); }
        catch { SetOperationNotice("Не удалось изменить состояние VPN. Сетевые изменения отменены; проверьте права и доступность сервера."); }
        finally
        {
            if (engine.Status.State == VpnConnectionState.Error) activeProfile = connectingProfile;
            else if (engine.Status.State == VpnConnectionState.Disconnected) activeProfile = null;
            connectingProfile = null;
            ConnectionState = engine.Status.State;
            IsConnecting = false;
            connectionCancellation.Dispose();
            connectionCancellation = null;
        }
    }

    internal async Task DisconnectForUpdateAsync()
    {
        CancelProbes();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await CancelConnectionAsync().WaitAsync(timeout.Token);
        if (engine is null) return;
        await engine.DisconnectAsync(timeout.Token).WaitAsync(timeout.Token);
        if (engine.Status.State is not (VpnConnectionState.Disconnected or VpnConnectionState.Error))
            throw new UpdateInstallException("VPN ещё отключается. Повторите обновление после отключения.");
    }
}
