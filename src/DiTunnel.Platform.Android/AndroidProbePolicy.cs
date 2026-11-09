using DiTunnel.Core.Connection;
namespace DiTunnel.Platform.Android;
internal sealed class AndroidProbeException(string stage, string errorType) : ProbeFailureException(stage, errorType);
internal static class AndroidProbePolicy
{
    internal static Uri Primary => ProbePolicy.Primary;
    internal static Uri Fallback => ProbePolicy.Fallback;
    internal static ServerProbeResult Success(double milliseconds, string method, string stage, bool confirmed, string attempts = "") => ProbePolicy.Success(milliseconds, method, stage, confirmed, attempts);
    internal static ServerProbeResult Failure(Exception error, string stage, string method, double milliseconds) => ProbePolicy.Failure(error, stage, method, milliseconds);
    internal static string Classify(Exception error) => ProbePolicy.Classify(error);
    internal static void ValidateResponse(Uri target, int status) => ProbePolicy.ValidateResponse(target, status);
    internal static string TunnelMessage(ServerProbeResult result) => ProbePolicy.TunnelMessage(result);
    internal static Task<ServerProbeResult> CheckHttpsAsync(Func<Uri, CancellationToken, Task> request, CancellationToken token, TimeSpan? attemptBudget = null) => ProbePolicy.CheckHttpsAsync(request, token, attemptBudget);
    internal static Task<ServerProbeResult> CheckHttpClientAsync(HttpClient http, CancellationToken token) => ProbePolicy.CheckHttpClientAsync(http, token);
}
