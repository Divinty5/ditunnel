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
    public LinuxNetworkClient(string? busAddress = null, string destination = LinuxNetworkProtocol.Service, Func<LinuxVpnPolicy>? policy = null)
    {
        connection = new(busAddress ?? DBusAddress.System ?? throw new InvalidOperationException("Системная шина D-Bus недоступна."));
        this.destination = destination;
        this.policy = policy ?? (() => LinuxVpnPolicy.Default);
    }

    public async Task<LinuxNetworkCapabilities> GetCapabilitiesAsync(CancellationToken token = default)
    {
        await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
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
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
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
                catch { Dispose(); }
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
        _ = LinuxNetworkProtocol.ParseProfile(LinuxNetworkProtocol.Version, profile.Content, 0);
        if (sessionId is not null) throw new InvalidOperationException("Сначала отключите текущую сессию VPN.");
        await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
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
            try { await DisconnectAsync().ConfigureAwait(false); }
            catch { connection.Dispose(); Publish(new(VpnConnectionState.Error, "Служба не подтвердила очистку сети. Перезапустите приложение и восстановите сеть.")); }
            if (ex is OperationCanceledException) throw;
            throw new InvalidOperationException(IsNetworkProtectionActive ? "Kill switch сохраняет блокировку. Нажмите «Восстановить сеть» для её снятия."
                : "Не удалось подключить VPN. Проверьте профиль, права polkit и состояние сетевой службы.");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (sessionId is not { } id) return;
        Publish(new(VpnConnectionState.Disconnecting));
        // Once requested, cleanup must finish even if the UI cancels its operation.
        await connection.CallMethodAsync(CreateControl("Disconnect", id)).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        sessionId = null;
        Publish(await ReadStatusAsync(CancellationToken.None).ConfigureAwait(false));
    }

    public async Task RestoreNetworkAsync(CancellationToken cancellationToken = default)
    {
        await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        await connection.CallMethodAsync(CreateControl("RestoreNetwork")).WaitAsync(TimeSpan.FromSeconds(95), cancellationToken).ConfigureAwait(false);
        sessionId = null;
        IsNetworkProtectionActive = false;
        Publish(new(VpnConnectionState.Disconnected, "VPN и kill switch отключены. Доступ к сети восстановлен."));
    }

    public async Task SwitchAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        // The generic disconnect/connect sequence releases KS between sessions.
        // Until an atomic policy handoff is available, require an explicit disconnect.
        if (IsNetworkProtectionActive && policy().KillSwitch)
            throw new NotSupportedException("С активным kill switch сначала отключите VPN вручную, затем подключитесь с новыми настройками.");
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        await ConnectAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    private Task<VpnStatus> ReadStatusAsync(CancellationToken token) => connection.CallMethodAsync(CreateControl("GetStatus"), (message, _) =>
    {
        var reader = message.GetBodyReader();
        var state = reader.ReadUInt32();
        var since = reader.ReadInt64();
        var text = reader.ReadString();
        IsNetworkProtectionActive = reader.ReadBool();
        if (state > (uint)VpnConnectionState.Error || text.Length > 512) throw new InvalidOperationException();
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
                if (sessionId is null || Status.State != VpnConnectionState.Connected) continue;
                var status = await ReadStatusAsync(monitoring.Token).ConfigureAwait(false);
                if (Status.State != VpnConnectionState.Connected) continue;
                Publish(status);
                if (status.State is VpnConnectionState.Disconnected or VpnConnectionState.Error) sessionId = null;
            }
        }
        catch (OperationCanceledException) when (monitoring.IsCancellationRequested) { }
        catch { connection.Dispose(); Publish(new(VpnConnectionState.Error, "Связь с сетевой службой потеряна. Перезапустите приложение.")); }
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
