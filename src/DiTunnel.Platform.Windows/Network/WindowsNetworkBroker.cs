using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using DiTunnel.Core.Connection;

namespace DiTunnel.Platform.Windows.Network;

public static class WindowsNetworkBroker
{
    private static NetworkProcessJob? activeJob;
    public static int Run(string[] args)
    {
        // Mutex is acquired and released on this synchronous entry thread, while async work runs below.
        using var mutex = new Mutex(false, @"Global\DiTunnel.NetworkHost.v1");
        bool locked;
        try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
        if (!locked) return 4;
        try
        {
            if (args.SequenceEqual(new[] { "--cleanup-wfp" }))
            {
                WindowsDnsPolicy.CleanupOwned();
                WindowsKillSwitchController.CleanupStaleFilters();
                return 0;
            }
            if (args.SequenceEqual(new[] { "--wfp-self-test" }))
            {
                using var output = new StringWriter();
                var code = WindowsKillSwitchSelfTest.RunAsync(output).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                    "Di-Tunnel-Wfp-Self-Test.json"), output.ToString());
                return code;
            }
            if (args.Length != 4 || args[0] != "--broker" || !args[1].StartsWith("DiTunnel.Network.", StringComparison.Ordinal) ||
                !Guid.TryParseExact(args[1][17..], "N", out _) || !int.TryParse(args[2], out var pid) || !long.TryParse(args[3], out var ticks)) return 2;
            using var owner = Process.GetProcessById(pid);
            NetworkPeer.CheckOwner(owner, ticks);
            activeJob = new NetworkProcessJob();
            var result = RunAsync(args[1], owner).GetAwaiter().GetResult();
            // Keep the job handle open until Program calls Environment.Exit after releasing the mutex.
            return result;
        }
        catch { return 3; } // No exception text: provider/core errors can contain configuration secrets.
        finally { mutex.ReleaseMutex(); }
    }

    private static async Task<int> RunAsync(string pipeName, Process owner)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var lifetime = new CancellationTokenSource();
        var commands = new SemaphoreSlim(1, 1);
        var requests = new ConcurrentDictionary<long, CancellationTokenSource>();
        var tasks = new List<Task>();
        var split = SplitTunnelPolicy.Default;
        var connection = ConnectionPolicy.Default;
        bool ads = false, strict = false;
        var explicitlyStopped = false;
        var killSwitch = new WindowsKillSwitchController();
        var engine = new WindowsVpnEngine(() => split, () => connection, killSwitch, () => ads, () => strict);
        var probe = new WindowsServerProbe(killSwitch, engine);
        // All replies and statuses share one queue so a delayed Connecting frame cannot
        // arrive after the final Connected acknowledgement. Event handlers never block cleanup.
        var statusFrames = System.Threading.Channels.Channel.CreateUnbounded<NetworkReply>();
        Task SendAsync(NetworkReply reply) { statusFrames.Writer.TryWrite(reply); return Task.CompletedTask; }
        engine.StatusChanged += (_, status) => statusFrames.Writer.TryWrite(new(0, status, engine.IsNetworkProtectionActive));
        var statusWriter = Task.Run(async () =>
        {
            await foreach (var reply in statusFrames.Reader.ReadAllAsync())
            {
                try { if (pipe.IsConnected) await NetworkProtocol.WriteAsync(pipe, reply); }
                catch (IOException) { lifetime.Cancel(); }
            }
        });
        async Task HandleAsync(NetworkRequest request, CancellationTokenSource cancellation)
        {
            bool commandLocked = false;
            try
            {
                ServerProbeResult? result = null;
                if (request.Operation == "probe")
                    result = await probe.ProbeAsync(request.Profile ?? throw new FormatException("Профиль отсутствует."), cancellation.Token, request.ProbeMode);
                else
                {
                    await commands.WaitAsync(cancellation.Token);
                    commandLocked = true;
                    if (request.Operation is "connect" or "switch")
                    {
                        split = request.Split ?? SplitTunnelPolicy.Default;
                        connection = request.Connection ?? ConnectionPolicy.Default;
                        ads = request.BlockAds; strict = request.StrictAds;
                        explicitlyStopped = false;
                    }
                    switch (request.Operation)
                    {
                        case "connect": await engine.ConnectAsync(request.Profile ?? throw new FormatException("Профиль отсутствует."), cancellation.Token); break;
                        case "switch": await engine.SwitchAsync(request.Profile ?? throw new FormatException("Профиль отсутствует."), cancellation.Token); break;
                        case "disconnect":
                            await engine.DisconnectAsync(cancellation.Token);
                            explicitlyStopped = true;
                            break;
                        case "restore-network":
                            await engine.DisconnectAsync(cancellation.Token);
                            WindowsDnsPolicy.CleanupOwned();
                            WindowsKillSwitchController.CleanupStaleFilters();
                            explicitlyStopped = true;
                            break;
                        default: throw new FormatException("Неизвестная операция сетевого модуля.");
                    }
                }
                await SendAsync(new(request.Id, engine.Status, engine.IsNetworkProtectionActive, result));
            }
            catch (Exception error)
            {
                var message = error is OperationCanceledException ? "Операция отменена." :
                    error is InvalidOperationException or FormatException or NotSupportedException or TimeoutException ? error.Message : "Сбой сетевого модуля Windows.";
                await SendAsync(new(request.Id, engine.Status, engine.IsNetworkProtectionActive, Error: message));
            }
            finally
            {
                if (commandLocked) commands.Release();
                requests.TryRemove(request.Id, out _);
                cancellation.Dispose();
            }
        }
        var ownerWatch = Task.Run(async () =>
        {
            try { await owner.WaitForExitAsync(lifetime.Token); lifetime.Cancel(); }
            catch (OperationCanceledException) { }
        });
        try
        {
            await pipe.ConnectAsync(15000, lifetime.Token);
            NetworkPeer.Check(pipe, owner.Id, server: true);
            while (await NetworkProtocol.ReadAsync<NetworkRequest>(pipe, lifetime.Token) is { } request)
            {
                if (request.Operation == "cancel")
                {
                    if (requests.TryGetValue(request.Id, out var active))
                        try { active.Cancel(); } catch (ObjectDisposedException) { }
                    continue;
                }
                if (request.Id <= 0 || requests.Count >= 64) throw new InvalidDataException("Слишком много сетевых операций.");
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                if (!requests.TryAdd(request.Id, cancellation)) { cancellation.Dispose(); throw new InvalidDataException("Повторный номер операции."); }
                tasks.RemoveAll(t => t.IsCompleted);
                tasks.Add(HandleAsync(request, cancellation));
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
        finally
        {
            lifetime.Cancel();
            // Cancel queued/in-flight requests before disconnecting. Cleanup itself must never be cancelled.
            await Task.WhenAll(tasks);
            // A probe-only broker can inherit armed filters before loading any policy.
            await engine.DisposeAsync(preserveProtection: !explicitlyStopped && engine.IsNetworkProtectionActive);
            statusFrames.Writer.TryComplete();
            await statusWriter;
            await ownerWatch;
            commands.Dispose();
        }
        return 0;
    }
}
