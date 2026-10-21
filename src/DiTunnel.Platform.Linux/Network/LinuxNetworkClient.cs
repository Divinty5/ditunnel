using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using Tmds.DBus.Protocol;

namespace DiTunnel.Platform.Linux.Network;

public sealed class LinuxNetworkClient : IServerBatchProbe, IProfileVpnEngine, INetworkRecoveryEngine, IDisposable
{
    private readonly CancellationTokenSource monitoring = new();
    private Task? monitor;
    private Guid? sessionId;
    public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
    public ImportedProfile? ActiveProfile { get; private set; }
    public bool RequiresAdministrator => false;
    public event EventHandler<VpnStatus>? StatusChanged;
    private void Publish(VpnStatus status) { Status = status; if (status.State is VpnConnectionState.Disconnected or VpnConnectionState.Error) ActiveProfile = null; StatusChanged?.Invoke(this, status); }
    private readonly DBusConnection connection;
    private readonly string destination;
    private readonly Func<LinuxVpnPolicy> policy;
    public bool IsNetworkProtectionActive { get; private set; }
    private readonly SemaphoreSlim probes = new(1, 1);
    private readonly SemaphoreSlim control = new(1, 1);
    public LinuxNetworkClient(string? busAddress = null, string destination = LinuxNetworkProtocol.Service, Func<LinuxVpnPolicy>? policy = null)
    {
        connection = new(new DBusConnectionOptions(busAddress ?? DBusAddress.System ?? throw new InvalidOperationException("Системная шина D-Bus недоступна.")) { AutoConnect = true });
        this.destination = destination;
        this.policy = policy ?? (() => LinuxVpnPolicy.Default);
    }

    public async Task<LinuxNetworkCapabilities> GetCapabilitiesAsync(CancellationToken token = default)
    {
        return await connection.CallMethodAsync(CreateCapabilities(), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            return new LinuxNetworkCapabilities(reader.ReadUInt32(), reader.ReadBool(), reader.ReadBool(), reader.ReadBool(), reader.ReadBool());
        }).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
    }

    private MessageBuffer CreateCapabilities()
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: LinuxNetworkProtocol.Path,
            @interface: LinuxNetworkProtocol.Interface, member: "GetCapabilities");
        return writer.CreateMessage();
    }

    public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
    {
        await probes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ProbeCoreAsync(profile, cancellationToken, mode).ConfigureAwait(false); }
        finally { probes.Release(); }
    }

    public async Task<IReadOnlyList<ServerProbeResult>> ProbeManyAsync(IReadOnlyList<ImportedProfile> profiles, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast)
    {
        var results = new List<ServerProbeResult>(profiles.Count);
        foreach (var profile in profiles) results.Add(await ProbeAsync(profile, cancellationToken, mode).ConfigureAwait(false));
        return results;
    }

    private async Task<ServerProbeResult> ProbeCoreAsync(ImportedProfile profile, CancellationToken cancellationToken, ServerProbeMode mode)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        try
        {
            _ = LinuxNetworkProtocol.ParseProfile(LinuxNetworkProtocol.Version, profile.Content, (uint)mode);
            var call = connection.CallMethodAsync(CreateProbe(id, profile.Content, mode), static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var success = reader.ReadBool();
                var milliseconds = reader.ReadDouble();
                var text = reader.ReadString();
                var deferred = reader.ReadBool();
                if (text.Length > 512 || success && (!double.IsFinite(milliseconds) || milliseconds < 0))
                    throw new InvalidOperationException("Некорректный ответ сетевой службы.");
                return new ServerProbeResult(success ? milliseconds : null, text, deferred);
            });
            try { return await call.WaitAsync(LinuxNetworkProtocol.ProbeTimeout + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                try { await CancelAsync(id).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch { /* The service bounds probe lifetime; recovery must remain available. */ }
                // Observe the original task even when a broken bus prevents its reply.
                _ = call.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(null, "Не удалось проверить сервер. Проверьте профиль и доступность сетевой службы Linux."); }
    }

    private MessageBuffer CreateProbe(Guid id, string content, ServerProbeMode mode)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: LinuxNetworkProtocol.Path,
            @interface: LinuxNetworkProtocol.Interface, member: "Probe", signature: "ussu");
        writer.WriteUInt32(LinuxNetworkProtocol.Version);
        writer.WriteString(id.ToString("D"));
        writer.WriteString(content);
        writer.WriteUInt32((uint)mode);
        return writer.CreateMessage();
    }

    private Task CancelAsync(Guid id)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: LinuxNetworkProtocol.Path,
            @interface: LinuxNetworkProtocol.Interface, member: "CancelProbe", signature: "s");
        writer.WriteString(id.ToString("D"));
        return connection.CallMethodAsync(writer.CreateMessage());
    }

    public async Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        await control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ConnectCoreAsync(profile, cancellationToken).ConfigureAwait(false); }
        finally { control.Release(); }
    }

    private async Task ConnectCoreAsync(ImportedProfile profile, CancellationToken cancellationToken)
    {
        _ = LinuxNetworkProtocol.ParseProfile(LinuxNetworkProtocol.Version, profile.Content, 0);
        if (sessionId is not null && Status.State == VpnConnectionState.Error)
        {
            var current = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            Publish(current);
            if (current.State is VpnConnectionState.Error or VpnConnectionState.Disconnected) sessionId = null;
        }
        if (sessionId is not null) throw new InvalidOperationException(Status.State == VpnConnectionState.Error
            ? "Служба не подтвердила очистку сети. Нажмите «Восстановить сеть»." : "Сначала отключите текущую сессию VPN.");
        var id = Guid.NewGuid();
        sessionId = id;
        Publish(new(VpnConnectionState.Connecting));
        var call = connection.CallMethodAsync(CreateControl("Connect", id, profile.Content));
        try
        {
            await call.WaitAsync(TimeSpan.FromSeconds(95), cancellationToken).ConfigureAwait(false);
            ActiveProfile = profile;
            Publish(await ReadStatusAsync(cancellationToken).ConfigureAwait(false));
            monitor ??= MonitorAsync();
        }
        catch (Exception ex)
        {
            _ = call.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            try { await DisconnectCoreAsync().ConfigureAwait(false); }
            catch { await PublishCleanupFailureAsync().ConfigureAwait(false); }
            if (ex is OperationCanceledException) throw;
            throw new InvalidOperationException(IsNetworkProtectionActive ? "Kill switch сохраняет блокировку. Нажмите «Восстановить сеть» для её снятия."
                : "Не удалось подключить VPN. Проверьте профиль, права polkit и состояние сетевой службы.");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        catch { await PublishCleanupFailureAsync().ConfigureAwait(false); throw; }
        finally { control.Release(); }
    }

    private async Task PublishCleanupFailureAsync()
    {
        // A failed Connect may already have enabled KS. Read its actual state
        // without treating an unavailable reply as proof that protection is off.
        try { _ = await ReadStatusAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        Publish(new(VpnConnectionState.Error, "Служба не подтвердила очистку сети. Нажмите «Восстановить сеть»."));
    }

    private async Task DisconnectCoreAsync()
    {
        if (sessionId is not { } id) return;
        Publish(new(VpnConnectionState.Disconnecting));
        // Once requested, cleanup must finish even if the UI cancels its operation.
        await connection.CallMethodAsync(CreateControl("Disconnect", id)).WaitAsync(TimeSpan.FromSeconds(95)).ConfigureAwait(false);
        sessionId = null;
        Publish(await ReadStatusAsync(CancellationToken.None).ConfigureAwait(false));
    }

    public async Task RestoreNetworkAsync(CancellationToken cancellationToken = default)
    {
        await control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RestoreNetworkCoreAsync(cancellationToken).ConfigureAwait(false); }
        catch { await PublishCleanupFailureAsync().ConfigureAwait(false); throw; }
        finally { control.Release(); }
    }

    private async Task RestoreNetworkCoreAsync(CancellationToken cancellationToken)
    {
        await connection.CallMethodAsync(CreateControl("RestoreNetwork")).WaitAsync(TimeSpan.FromSeconds(95), cancellationToken).ConfigureAwait(false);
        sessionId = null;
        IsNetworkProtectionActive = false;
        Publish(new(VpnConnectionState.Disconnected, "VPN и kill switch отключены. Доступ к сети восстановлен."));
    }

    public async Task SwitchAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        _ = LinuxNetworkProtocol.ParseProfile(LinuxNetworkProtocol.Version, profile.Content, 0);
        await control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessionId is not { } id) { await ConnectCoreAsync(profile, cancellationToken).ConfigureAwait(false); return; }
            Publish(new(VpnConnectionState.Reconnecting));
            var call = connection.CallMethodAsync(CreateControl("Switch", id, profile.Content));
            try
            {
                await call.WaitAsync(TimeSpan.FromSeconds(95), cancellationToken).ConfigureAwait(false);
                ActiveProfile = profile;
                Publish(await ReadStatusAsync(cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                _ = call.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                if (cancellationToken.IsCancellationRequested) await DisconnectCoreAsync().ConfigureAwait(false);
                else
                {
                    var status = await ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                    Publish(status);
                    if (status.State is VpnConnectionState.Error or VpnConnectionState.Disconnected) sessionId = null;
                }
                throw;
            }
        }
        finally { control.Release(); }
    }

    private Task<VpnStatus> ReadStatusAsync(CancellationToken token) => connection.CallMethodAsync(CreateControl("GetStatus"), (message, _) =>
    {
        var reader = message.GetBodyReader();
        var state = reader.ReadUInt32();
        var since = reader.ReadInt64();
        var text = reader.ReadString();
        var protectedNetwork = reader.ReadBool();
        if (state > (uint)VpnConnectionState.Error || text.Length > 512) throw new InvalidOperationException();
        IsNetworkProtectionActive = protectedNetwork;
        return new VpnStatus((VpnConnectionState)state, text.Length == 0 ? null : text, since == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(since));
    }).WaitAsync(TimeSpan.FromSeconds(5), token);

    public async Task RefreshStatusAsync(CancellationToken token = default) => Publish(await ReadStatusAsync(token).ConfigureAwait(false));

    private async Task MonitorAsync()
    {
        try
        {
            while (!monitoring.IsCancellationRequested)
            {
                await Task.Delay(1000, monitoring.Token).ConfigureAwait(false);
                if (sessionId is null || Status.State is not (VpnConnectionState.Connected or VpnConnectionState.Error)) continue;
                if (!await control.WaitAsync(0, monitoring.Token).ConfigureAwait(false)) continue;
                try
                {
                    var status = await ReadStatusAsync(monitoring.Token).ConfigureAwait(false);
                    Publish(status);
                    if (status.State is VpnConnectionState.Disconnected or VpnConnectionState.Error) sessionId = null;
                }
                catch (OperationCanceledException) when (monitoring.IsCancellationRequested) { throw; }
                catch { Publish(new(VpnConnectionState.Error, "Связь с сетевой службой потеряна. Нажмите «Восстановить сеть».")); }
                finally { control.Release(); }
            }
        }
        catch (OperationCanceledException) when (monitoring.IsCancellationRequested) { }
    }

    private MessageBuffer CreateControl(string member, Guid? id = null, string? content = null)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: LinuxNetworkProtocol.Path, @interface: LinuxNetworkProtocol.Interface,
            member: member, signature: content is not null ? "usss" : id is not null ? "s" : null);
        if (content is not null) writer.WriteUInt32(LinuxNetworkProtocol.Version);
        if (id is not null) writer.WriteString(id.Value.ToString("D"));
        if (content is not null) { writer.WriteString(content); writer.WriteString(policy().Serialize()); }
        return writer.CreateMessage();
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally { Dispose(); if (monitor is not null) await monitor.ConfigureAwait(false); }
    }
    public void Dispose() { monitoring.Cancel(); connection.Dispose(); }
}
