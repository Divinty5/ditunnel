using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;

namespace DiTunnel.Core.Connection;

public class ProbeFailureException(string stage, string errorType, string? message = null) : InvalidOperationException(message ?? ProbePolicy.ShortFailure(errorType, stage))
{
    public ProbeDiagnostics? Diagnostics { get; init; }
    public string Stage { get; } = stage;
    public string ErrorType { get; } = errorType;
}

// Common result vocabulary and control targets; platform-specific routing stays in each backend.
public static class ProbePolicy
{
    // The primary matches the desktop HTTPS check. The independent fallback also tests DNS.
    public static readonly Uri Primary = new("https://1.1.1.1/cdn-cgi/trace");
    public static readonly Uri Fallback = new("https://www.gstatic.com/generate_204");
    public static TimeSpan ActiveHttpsAttemptBudget { get; } = TimeSpan.FromSeconds(5);
    public static ServerProbeResult ActiveTunnelDeferred(string platform) => new(null,
        "Проверка через туннель недоступна", IsDeferred: true,
        Diagnostics: new("HTTPS", "TUN", "Unconfirmed", 0, false, VersionFor(platform), Platform: platform));
    public static string VersionFor(string platform) => typeof(ProbePolicy).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == platform + "Version")?.Value ?? "unknown";

    public static ServerProbeResult Success(double milliseconds, string method, string stage, bool confirmed,
        string attempts = "", string platform = "Android") => new(milliseconds,
            $"{method} · {milliseconds:F0} мс",
            Diagnostics: new(method, stage, "None", milliseconds, confirmed, VersionFor(platform), attempts, platform));

    public static ServerProbeResult Failure(Exception error, string stage, string method, double milliseconds, string platform = "Android")
    {
        var kind = Classify(error);
        if (stage == "HTTPS" && kind is "Tls" or "Dns") stage = kind == "Tls" ? "TLS" : "DNS контрольного адреса";
        if (error is ProbeFailureException known) { stage = known.Stage; kind = known.ErrorType; }
        if (stage is "Профиль" or "Profile" && error is InvalidOperationException) kind = "Configuration";
        if (stage is "AmneziaWG" or "Xray" && kind == "Internal") kind = "Runtime";
        var attempts = (error as ProbeFailureException)?.Diagnostics?.Attempts ?? "";
        return new(null, ShortFailure(kind, stage),
            Diagnostics: new(method, stage, kind, milliseconds, false, VersionFor(platform), attempts, platform));
    }

    public static string ShortFailure(string kind, string stage)
    {
        var reason = kind switch
        {
            "Timeout" => "Тайм-аут",
            "Dns" => "Ошибка DNS",
            "Tls" => "Ошибка TLS",
            "Connection" => "Ошибка соединения",
            "ControlService" => "Ошибка контрольного сайта",
            "Configuration" => "Ошибка конфигурации",
            "Runtime" => "Ошибка запуска",
            _ => "Ошибка проверки"
        };
        var label = stage switch
        {
            "DNS сервера" => "DNS сервера",
            "DNS контрольного адреса" => "DNS теста",
            "Подключение к серверу" => "Соединение",
            "Профиль" => "Профиль",
            "Служба проверки" => "Служба проверки",
            "Транспорт AWG" => "Транспорт AWG",
            "AmneziaWG" => "AmneziaWG",
            "Handshake AWG" => "Handshake AWG",
            "HTTPS" or "HTTP" or "TLS" or "TUN" or "Xray" => stage,
            _ => ""
        };
        return label.Length == 0 || reason.Contains(label, StringComparison.OrdinalIgnoreCase) ? reason : $"{label} · {reason}";
    }

    public static string Classify(Exception error)
    {
        // Match known types only; native/Java messages can contain credentials and server addresses.
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is ProbeFailureException known) return known.ErrorType;
            if (current is AuthenticationException || current.GetType().Name.Contains("SSL", StringComparison.Ordinal)) return "Tls";
            if (current is TimeoutException or OperationCanceledException or SocketException { SocketErrorCode: SocketError.TimedOut }
                || current.GetType().Name == "SocketTimeoutException") return "Timeout";
            if (current is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }
                || current.GetType().Name == "UnknownHostException") return "Dns";
            if (current is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }) return "Dns";
            if (current is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return "Tls";
        }
        return error switch
        {
            HttpRequestException { StatusCode: not null } => "ControlService",
            SocketException or IOException or HttpRequestException => "Connection",
            FormatException or NotSupportedException => "Configuration",
            _ when error.GetType().Name is "ConnectException" or "NoRouteToHostException" or "SocketException" => "Connection",
            _ => "Internal"
        };
    }

    public static void ValidateResponse(Uri target, int status)
    {
        if (status != (target == Fallback ? 204 : 200))
            throw new ProbeFailureException("HTTPS", "ControlService");
    }

    public static string TunnelMessage(ServerProbeResult result) =>
        (result.Diagnostics?.ProfileConfirmed == true ? "VPN подключён." : $"Туннель запущен. {result.Message}")
        + "\n" + result.Diagnostics?.ToDisplayString();

    // One budget per site. Start the independent fallback after a short head start,
    // rather than waiting for two full sequential timeouts on every dead profile.
    public static async Task<ServerProbeResult> CheckHttpsAsync(Func<Uri, CancellationToken, Task> request,
        CancellationToken cancellationToken, TimeSpan? attemptBudget = null, string platform = "Android",
        IReadOnlyList<Uri>? targets = null)
    {
        targets ??= [Primary, Fallback];
        if (targets.Count is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(targets));
        var total = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task<ServerProbeResult> Attempt(Uri target)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(attemptBudget ?? TimeSpan.FromSeconds(3));
            var watch = Stopwatch.StartNew();
            ServerProbeResult result;
            try
            {
                await request(target, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                result = Success(watch.Elapsed.TotalMilliseconds, "HTTPS", "HTTPS", true, platform: platform);
            }
            catch (Exception error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = Failure(timeout.IsCancellationRequested ? new TimeoutException() : error,
                    "HTTPS", "HTTPS", watch.Elapsed.TotalMilliseconds, platform);
                if (stop.IsCancellationRequested)
                    result = result with { Diagnostics = result.Diagnostics! with { ErrorType = "Superseded" } };
            }
            return result with { Diagnostics = result.Diagnostics! with
            {
                Attempts = $"{target.Host}: {result.Diagnostics.ErrorType} ({watch.Elapsed.TotalMilliseconds:F0} мс)"
            } };
        }
        var primary = Attempt(targets[0]);
        if (targets.Count == 1) return await primary;
        using var headStart = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(primary, Task.Delay(TimeSpan.FromMilliseconds(600), headStart.Token));
        cancellationToken.ThrowIfCancellationRequested();
        if (primary.IsCompletedSuccessfully && primary.Result.Milliseconds is not null)
        {
            headStart.Cancel();
            return primary.Result;
        }
        headStart.Cancel();
        var fallback = Attempt(targets[1]);
        var remaining = new List<Task<ServerProbeResult>> { primary, fallback };
        ServerProbeResult? winner = null;
        while (remaining.Count > 0)
        {
            var completed = await Task.WhenAny(remaining);
            remaining.Remove(completed);
            var result = await completed;
            if (result.Milliseconds is not null) { winner = result; stop.Cancel(); break; }
        }
        // Both real request implementations honour cancellation and close their sockets.
        var attempts = await Task.WhenAll(primary, fallback);
        cancellationToken.ThrowIfCancellationRequested();
        winner ??= attempts.FirstOrDefault(item => item.Diagnostics!.ErrorType is not ("Timeout" or "Connection")) ?? attempts[0];
        return winner with { Diagnostics = winner.Diagnostics! with
        {
            DurationMilliseconds = total.Elapsed.TotalMilliseconds,
            Attempts = string.Join("\n", attempts.Select(item => item.Diagnostics!.Attempts))
        } };
    }

    public static async Task<ServerProbeResult> CheckHttpClientAsync(HttpClient http, CancellationToken token, string platform = "Android",
        TimeSpan? attemptBudget = null) =>
        await CheckHttpsAsync(async (target, cancellation) =>
        {
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellation);
            ValidateResponse(target, (int)response.StatusCode);
        }, token, attemptBudget, platform);
}
