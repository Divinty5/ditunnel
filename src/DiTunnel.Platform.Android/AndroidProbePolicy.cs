using DiTunnel.Core.Connection;
namespace DiTunnel.Platform.Android;
internal sealed class AndroidProbeException(string stage, string errorType, string? message = null) : ProbeFailureException(stage, errorType, message);
internal static class AndroidProbePolicy
{
    internal static readonly TimeSpan XrayDnsBudget = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan XrayProfileBudget = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan XrayReplyBudget = XrayDnsBudget + XrayProfileBudget + TimeSpan.FromSeconds(5);
    internal static Uri Primary => ProbePolicy.Primary;
    internal static Uri Fallback => ProbePolicy.Fallback;
    internal static ServerProbeResult Success(double milliseconds, string method, string stage, bool confirmed, string attempts = "") => ProbePolicy.Success(milliseconds, method, stage, confirmed, attempts);
    internal static ServerProbeResult Failure(Exception error, string stage, string method, double milliseconds) => ProbePolicy.Failure(error, stage, method, milliseconds);
    internal static ServerProbeResult ServiceFailure(Exception error, double milliseconds) =>
        ProbePolicy.Incomplete(Failure(
            new AndroidProbeException("Служба проверки", ProbePolicy.Classify(error) == "Timeout" ? "Timeout" : "Runtime"),
            "Служба проверки", "HTTPS", milliseconds));
    internal static ServerProbeResult ActiveTunnelResult(ServerProbeResult result) =>
        result.Diagnostics?.ErrorType is "Internal" or "Runtime" ? ProbePolicy.Incomplete(result) : result;
    internal static string Classify(Exception error) => ProbePolicy.Classify(error);
    internal static void ValidateResponse(Uri target, int status) => ProbePolicy.ValidateResponse(target, status);
    internal static string TunnelMessage(ServerProbeResult result) => ProbePolicy.TunnelMessage(result);
    internal static Task<ServerProbeResult> CheckHttpsAsync(Func<Uri, CancellationToken, Task> request, CancellationToken token, TimeSpan? attemptBudget = null) => ProbePolicy.CheckHttpsAsync(request, token, attemptBudget);
    internal static Task<ServerProbeResult> CheckHttpClientAsync(HttpClient http, CancellationToken token) =>
        RetryTimeoutAsync(() => ProbePolicy.CheckHttpClientAsync(http, token, attemptBudget: TimeSpan.FromSeconds(5)));

    internal static async Task<ServerProbeResult> RetryTimeoutAsync(Func<Task<ServerProbeResult>> check)
    {
        var result = await check();
        if (result.Diagnostics?.ErrorType != "Timeout") return result;
        var retry = await check();
        return retry with { Diagnostics = retry.Diagnostics! with
        {
            Attempts = result.Diagnostics.Attempts + "\n" + retry.Diagnostics!.Attempts,
            DurationMilliseconds = result.Diagnostics.DurationMilliseconds + retry.Diagnostics.DurationMilliseconds
        } };
    }
}
