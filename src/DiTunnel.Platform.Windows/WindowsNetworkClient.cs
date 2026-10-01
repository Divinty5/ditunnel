using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows;

/// <summary>Unprivileged UI facade. Only the separately authenticated broker touches the network.</summary>
public sealed class WindowsNetworkClient(Func<SplitTunnelPolicy> split, Func<ConnectionPolicy> connection,
    Func<bool> blockAds, Func<bool> strictAds) : IProfileVpnEngine, IServerProbe
{
    private readonly SemaphoreSlim startup = new(1, 1);
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<NetworkReply>> pending = new();
    private NamedPipeServerStream? pipe;
    private Process? broker;
    private Task? reader;
    private long sequence;
    private bool disposed;
    private bool closing;
    public VpnStatus Status { get; private set; } = VpnStatus.Disconnected;
    public ImportedProfile? ActiveProfile { get; private set; }
    public bool RequiresAdministrator => false;
    public bool IsNetworkProtectionActive { get; private set; }
    public event EventHandler<VpnStatus>? StatusChanged;

    public async Task ConnectAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        await RequestAsync("connect", profile, cancellationToken);
        ActiveProfile = profile;
    }
    public async Task SwitchAsync(ImportedProfile profile, CancellationToken cancellationToken = default)
    {
        await RequestAsync("switch", profile, cancellationToken);
        ActiveProfile = profile;
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (pipe is { IsConnected: true }) await RequestAsync("disconnect", null, cancellationToken);
        else if (IsNetworkProtectionActive) throw new InvalidOperationException("Связь с сетевым модулем потеряна. Восстановление сети не подтверждено.");
        ActiveProfile = null;
    }
    public async Task<ServerProbeResult> ProbeAsync(ImportedProfile profile, CancellationToken cancellationToken = default, ServerProbeMode mode = ServerProbeMode.Fast) =>
        (await RequestAsync("probe", profile, cancellationToken, mode)).Probe ?? new(null, "Сетевой модуль не вернул результат проверки.");

    private async Task EnsureStartedAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await startup.WaitAsync(token);
        try
        {
            if (pipe is { IsConnected: true }) return;
            if (broker is { HasExited: false })
                throw new InvalidOperationException("Предыдущий сетевой модуль ещё восстанавливает сеть. Дождитесь его завершения.");
            pipe?.Dispose(); broker?.Dispose();
            var path = Path.Combine(AppContext.BaseDirectory, "Di-Tunnel.NetworkHost.exe");
            if (!File.Exists(path)) throw new InvalidOperationException("Сетевой модуль не найден. Пересоберите или переустановите Di-Tunnel.");
            var name = "DiTunnel.Network." + Guid.NewGuid().ToString("N");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            using var identity = WindowsIdentity.GetCurrent();
            foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security, HandleInheritability.None);
            var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var owner = Process.GetCurrentProcess();
            foreach (var arg in new[] { "--broker", name, Environment.ProcessId.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString() })
                start.ArgumentList.Add(arg);
            try { broker = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить сетевой модуль."); }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            { throw new InvalidOperationException("Запрос прав для сетевого модуля отменён. Интерфейс продолжает работать без повышения прав."); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await pipe.WaitForConnectionAsync(timeout.Token);
            NetworkPeer.Check(pipe, broker.Id, server: false);
            reader = ReadRepliesAsync(pipe);
        }
        catch { pipe?.Dispose(); pipe = null; throw; }
        finally { startup.Release(); }
    }

    private async Task<NetworkReply> RequestAsync(string operation, ImportedProfile? profile, CancellationToken token,
        ServerProbeMode mode = ServerProbeMode.Fast)
    {
        await EnsureStartedAsync(token);
        var id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<NetworkReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await WriteAsync(new(id, operation, profile, split(), connection(), blockAds(), strictAds(), mode), token);
            using var registration = token.Register(() => _ = CancelRequestAsync(id));
            // Wait for cancellation acknowledgement, including rollback, before the UI starts another operation.
            var reply = await completion.Task;
            token.ThrowIfCancellationRequested();
            if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
            return reply;
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task CancelRequestAsync(long id)
    {
        try { await WriteAsync(new(id, "cancel"), CancellationToken.None); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task WriteAsync(NetworkRequest request, CancellationToken token)
    {
        await writing.WaitAsync(token);
        try { await NetworkProtocol.WriteAsync(pipe ?? throw new IOException("Связь с сетевым модулем потеряна."), request, token); }
        catch { pipe?.Dispose(); throw; } // A partial frame must never be reused.
        finally { writing.Release(); }
    }

    private async Task ReadRepliesAsync(NamedPipeServerStream source)
    {
        try
        {
            while (await NetworkProtocol.ReadAsync<NetworkReply>(source) is { } reply)
            {
                IsNetworkProtectionActive = reply.ProtectionActive;
                if (reply.Status is not null) { Status = reply.Status; StatusChanged?.Invoke(this, Status); }
                if (reply.Id > 0 && pending.TryGetValue(reply.Id, out var completion)) completion.TrySetResult(reply);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or System.Text.Json.JsonException) { }
        finally
        {
            source.Dispose();
            foreach (var completion in pending.Values)
                completion.TrySetException(new IOException("Связь с сетевым модулем потеряна. Восстановление сети не подтверждено."));
            if (!closing)
            {
                Status = new(VpnConnectionState.Error, "Связь с сетевым модулем потеряна. Дождитесь восстановления сети перед новым подключением.");
                StatusChanged?.Invoke(this, Status);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        closing = true;
        try { await DisconnectAsync(); }
        finally
        {
            disposed = true;
            pipe?.Dispose();
            if (reader is not null) await reader;
            // Closing the pipe asks the independent broker to finish rollback; never kill it here.
            broker?.Dispose();
        }
    }
}
