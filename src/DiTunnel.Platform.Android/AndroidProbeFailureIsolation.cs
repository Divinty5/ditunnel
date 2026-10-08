namespace DiTunnel.Platform.Android;

// A failed profile/chunk must not discard successful peers or skip later protocols.
internal static class AndroidProbeFailureIsolation
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> probe, Func<Exception, T> onFailure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await probe();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error)
        {
            // Android may report socket closure rather than OperationCanceledException.
            // User cancellation still stops the batch; a profile deadline does not.
            cancellationToken.ThrowIfCancellationRequested();
            return onFailure(error);
        }
    }
}