using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.App.ViewModels;

internal sealed class ProfileProbeCoordinator(IServerProbe? probe, ReleaseTarget platform)
{
    public bool IsAvailable => probe is not null;

    public async Task<ServerProbeResult[]> RunAsync(ImportedProfile[] profiles, ServerProbeMode mode,
        Action<int, ServerProbeResult> applyResult, CancellationToken cancellationToken)
    {
        if (probe is IProgressiveServerProbe progressive)
            return await RunProgressiveAsync(progressive, profiles, mode, applyResult, cancellationToken);
        if (probe is IServerBatchProbe batch)
            return await RunBatchesAsync(batch, profiles, mode, applyResult, cancellationToken);
        if (probe is null) return [];

        var results = new ServerProbeResult[profiles.Length];
        using var concurrency = new SemaphoreSlim(mode == ServerProbeMode.Fast ? 8 : 2);
        await Task.WhenAll(profiles.Select(async (profile, index) =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                ServerProbeResult result;
                try { result = await probe.ProbeAsync(profile, cancellationToken, mode); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    var platformName = platform switch
                    {
                        ReleaseTarget.LinuxX64 => "Linux",
                        ReleaseTarget.AndroidArm64 => "Android",
                        _ => "Windows"
                    };
                    result = ProbePolicy.Failure(error, "Служба проверки", mode == ServerProbeMode.Https ? "HTTPS" : "TCP",
                        0, platformName);
                }
                cancellationToken.ThrowIfCancellationRequested();
                results[index] = result;
                applyResult(index, result);
            }
            finally { concurrency.Release(); }
        }));
        return results;
    }

    private static async Task<ServerProbeResult[]> RunProgressiveAsync(IProgressiveServerProbe probe,
        ImportedProfile[] profiles, ServerProbeMode mode, Action<int, ServerProbeResult> applyResult,
        CancellationToken cancellationToken)
    {
        var completed = new bool[profiles.Length];
        var progressGate = new object();
        var receivingProgress = true;

        void Apply(int index, ServerProbeResult result)
        {
            if (cancellationToken.IsCancellationRequested || (uint)index >= (uint)completed.Length || completed[index]) return;
            completed[index] = true;
            applyResult(index, result);
        }

        var progress = new Progress<(int Index, ServerProbeResult Result)>(item =>
        {
            lock (progressGate)
                if (receivingProgress) Apply(item.Index, item.Result);
        });

        IReadOnlyList<ServerProbeResult> results;
        try { results = await probe.ProbeManyAsync(profiles, progress, cancellationToken, mode); }
        finally { lock (progressGate) receivingProgress = false; }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureComplete(results, profiles.Length);
        for (var index = 0; index < results.Count; index++) Apply(index, results[index]);
        return results.ToArray();
    }

    private static async Task<ServerProbeResult[]> RunBatchesAsync(IServerBatchProbe probe,
        ImportedProfile[] profiles, ServerProbeMode mode, Action<int, ServerProbeResult> applyResult,
        CancellationToken cancellationToken)
    {
        const int batchSize = 5;
        var completed = new List<ServerProbeResult>(profiles.Length);
        for (var offset = 0; offset < profiles.Length; offset += batchSize)
        {
            var count = Math.Min(batchSize, profiles.Length - offset);
            var results = await probe.ProbeManyAsync(profiles.AsSpan(offset, count).ToArray(), cancellationToken, mode);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureComplete(results, count);
            for (var index = 0; index < count; index++) applyResult(offset + index, results[index]);
            completed.AddRange(results);
        }
        return completed.ToArray();
    }

    private static void EnsureComplete(IReadOnlyList<ServerProbeResult> results, int count)
    {
        if (results.Count != count) throw new InvalidOperationException("Проверка вернула неполный результат.");
    }
}
