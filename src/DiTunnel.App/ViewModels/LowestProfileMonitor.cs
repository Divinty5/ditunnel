namespace DiTunnel.App.ViewModels;

internal sealed class LowestProfileMonitor
{
    private CancellationTokenSource? loop;

    public void Start(CancellationToken lifetime, bool checkImmediately, Func<CancellationToken, Task<bool>> check,
        Action<int> announceNextCheck)
    {
        Stop();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        loop = cancellation;
        _ = RunAsync(cancellation, checkImmediately, check, announceNextCheck);
    }

    public void Stop()
    {
        var cancellation = loop;
        loop = null;
        cancellation?.Cancel();
    }

    private async Task RunAsync(CancellationTokenSource cancellation, bool checkImmediately,
        Func<CancellationToken, Task<bool>> check, Action<int> announceNextCheck)
    {
        var token = cancellation.Token;
        var step = 0;
        try
        {
            if (checkImmediately)
            {
                var changed = await check(token);
                step = changed ? 0 : 1;
            }

            while (!token.IsCancellationRequested)
            {
                var minutes = step switch { 0 => 5, 1 => 10, _ => 15 };
                announceNextCheck(minutes);
                await Task.Delay(TimeSpan.FromMinutes(minutes), token);
                var changed = await check(token);
                step = changed ? 0 : Math.Min(step + 1, 2);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(loop, cancellation)) loop = null;
            cancellation.Dispose();
        }
    }

    public static ServerItemViewModel? SelectBest(IEnumerable<ServerItemViewModel> profiles, Func<ServerItemViewModel, bool> eligible) =>
        profiles.Where(eligible).OrderBy(server => server.ProbeMilliseconds)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
}
