using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using DiTunnel.Core;
using DiTunnel.Core.Connection;
using DiTunnel.Platform.Android;
using System.Text.Json;

namespace DiTunnel.Core.Tests;

public sealed class AndroidProbePolicyTests
{
    [Fact]
    public async Task FallbackStartsWhilePrimaryIsStillWaitingAndCancelsItsRequest()
    {
        var primaryStopped = false;
        var fallbackSawPrimaryPending = false;
        var result = await AndroidProbePolicy.CheckHttpsAsync(async (target, token) =>
        {
            if (target == AndroidProbePolicy.Primary)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { primaryStopped = true; }
            }
            else fallbackSawPrimaryPending = !primaryStopped;
        }, CancellationToken.None, TimeSpan.FromSeconds(5));
        Assert.True(fallbackSawPrimaryPending);
        Assert.True(primaryStopped);
        Assert.True(result.Diagnostics!.ProfileConfirmed);
        Assert.Contains("Superseded", result.Diagnostics.Attempts);
    }

    [Fact]
    public async Task TlsFailureRemainsSpecificWhenOtherSiteTimesOut()
    {
        var result = await AndroidProbePolicy.CheckHttpsAsync((target, _) => Task.FromException(
            target == AndroidProbePolicy.Primary ? new AuthenticationException("synthetic") : new TimeoutException()), CancellationToken.None);
        Assert.Equal("Ошибка TLS", result.Message);
        Assert.Equal("Tls", result.Diagnostics!.ErrorType);
        Assert.True(result.Message.Length <= 45);
    }

    [Fact]
    public async Task FailedPrimaryDoesNotHideWorkingProfile()
    {
        var calls = new List<Uri>();
        var result = await AndroidProbePolicy.CheckHttpsAsync((target, _) =>
        {
            calls.Add(target);
            return target == AndroidProbePolicy.Primary
                ? Task.FromException(new HttpRequestException("control service down", null, HttpStatusCode.ServiceUnavailable))
                : Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(new[] { AndroidProbePolicy.Primary, AndroidProbePolicy.Fallback }, calls);
        Assert.NotNull(result.Milliseconds);
        Assert.True(result.Diagnostics!.ProfileConfirmed);
        Assert.Contains("ControlService", result.Diagnostics.Attempts);
        Assert.Contains("gstatic.com: None", result.Diagnostics.Attempts);
    }

    [Fact]
    public async Task PrimaryTimeoutLeavesBudgetForFallback()
    {
        var result = await AndroidProbePolicy.CheckHttpsAsync(async (target, token) =>
        {
            if (target == AndroidProbePolicy.Primary) await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None, TimeSpan.FromMilliseconds(30));
        Assert.True(result.Diagnostics!.ProfileConfirmed);
        Assert.Contains("Timeout", result.Diagnostics.Attempts);
    }

    [Fact]
    public async Task SuccessfulPrimaryDoesNotContactFallback()
    {
        var calls = 0;
        var result = await AndroidProbePolicy.CheckHttpsAsync((target, _) =>
        {
            calls++;
            Assert.Equal(AndroidProbePolicy.Primary, target);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.True(result.Diagnostics!.ProfileConfirmed);
    }

    [Fact]
    public async Task PreciseProbeRetriesTransientTimeoutAndKeepsAttemptDiagnostics()
    {
        using var handler = new TimeoutThenSuccessHandler();
        using var client = new HttpClient(handler);
        var result = await AndroidProbePolicy.CheckHttpClientAsync(client, default);
        Assert.Equal(3, handler.Calls);
        Assert.True(result.Diagnostics!.ProfileConfirmed);
        Assert.Contains("Timeout", result.Diagnostics.Attempts);
        Assert.Contains("None", result.Diagnostics.Attempts);
    }

    private sealed class TimeoutThenSuccessHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => ++Calls <= 2 ? Task.FromException<HttpResponseMessage>(new TimeoutException())
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    [Fact]
    public async Task TwoControlFailuresAreInconclusiveAndDoNotDiagnoseSubscriptionExpiry()
    {
        var result = await AndroidProbePolicy.CheckHttpsAsync((_, _) => Task.FromException(new TimeoutException()), CancellationToken.None);
        Assert.Null(result.Milliseconds);
        Assert.False(result.Diagnostics!.ProfileConfirmed);
        Assert.Equal("Timeout", result.Diagnostics.ErrorType);
        Assert.Equal("HTTPS · Тайм-аут", result.Message);
        Assert.DoesNotContain("истек", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("недоступен", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Туннель запущен", AndroidProbePolicy.TunnelMessage(result));
        Assert.Equal(2, result.Diagnostics.Attempts.Split('\n').Length);
    }

    [Fact]
    public void FailureKeepsItsOwnStageAndCannotReuseSuccessfulDiagnostics()
    {
        var earlier = ProbePolicy.Success(389, "HTTPS", "HTTPS", true).Diagnostics;
        var failure = new ProbeFailureException("Handshake AWG", "Timeout") { Diagnostics = earlier };
        var result = ProbePolicy.Failure(failure, "AmneziaWG", "HTTPS", 18000);
        Assert.Equal("Handshake AWG · Тайм-аут", result.Message);
        Assert.Equal("Handshake AWG", result.Diagnostics!.Stage);
        Assert.Equal("Timeout", result.Diagnostics.ErrorType);
        Assert.False(result.Diagnostics.ProfileConfirmed);
    }

    [Fact]
    public async Task DisabledClientCanAcceptTcpButFailDataForwarding()
    {
        // A reachable TCP endpoint that accepts connections but never forwards data models
        // a disabled client just as well as a stalled server. Neither proves expiry.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(endpoint.Address, endpoint.Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        var fast = AndroidProbePolicy.Success(1, "TCP", "Подключение к серверу", false);
        Assert.False(fast.Diagnostics!.ProfileConfirmed);
        Assert.Equal("TCP · 1 мс", fast.Message);
        var precise = await AndroidProbePolicy.CheckHttpsAsync(async (_, token) =>
        {
            var buffer = new byte[1];
            await tcp.GetStream().ReadExactlyAsync(buffer, token);
        }, CancellationToken.None, TimeSpan.FromMilliseconds(30));
        Assert.False(precise.Diagnostics!.ProfileConfirmed);
        Assert.Equal("Timeout", precise.Diagnostics.ErrorType);
        Assert.DoesNotContain("Подписка истекла", precise.Message);
    }

    [Fact]
    public async Task CallerCancellationNeverStartsFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AndroidProbePolicy.CheckHttpsAsync((_, _) =>
        {
            calls++;
            cancellation.Cancel();
            throw new IOException("socket closed");
        }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("Dns", "DNS сервера")]
    [InlineData("Tls", "TLS")]
    [InlineData("Connection", "Подключение к серверу")]
    [InlineData("Configuration", "Xray")]
    public void GeneratedDiagnosticsRetainStageAndCategory(string errorType, string stage)
    {
        var result = AndroidProbePolicy.Failure(new AndroidProbeException(stage, errorType), "Проверка", "HTTPS", 123);
        Assert.Equal(stage, result.Diagnostics!.Stage);
        Assert.Equal(errorType, result.Diagnostics.ErrorType);
        Assert.Equal(123, result.Diagnostics.DurationMilliseconds);
        Assert.Matches(@"^\d+\.\d+\.\d+$", result.Diagnostics.ClientVersion);
    }

    [Fact]
    public void NestedErrorsDistinguishDnsTlsAndConnection()
    {
        Assert.Equal("Dns", AndroidProbePolicy.Classify(new HttpRequestException("request failed", new SocketException((int)SocketError.HostNotFound))));
        Assert.Equal("Tls", AndroidProbePolicy.Classify(new HttpRequestException("request failed", new AuthenticationException("bad certificate"))));
        Assert.Equal("Connection", AndroidProbePolicy.Classify(new SocketException((int)SocketError.ConnectionRefused)));
    }

    [Fact]
    public void ErrorTextAndDiagnosticsNeverEchoExceptionMessages()
    {
        const string secret = "vless://private-user@secret-server/?password=private-password&token=private-token";
        var result = AndroidProbePolicy.Failure(new IOException(secret, new Exception(secret)), "HTTPS", "HTTPS", 12);
        var json = JsonSerializer.Serialize(new[] { result }, DiTunnelJsonContext.Default.ServerProbeResultArray);
        Assert.DoesNotContain("private-", result.Message + result.Diagnostics!.ToDisplayString() + json);
        var restored = JsonSerializer.Deserialize(json, DiTunnelJsonContext.Default.ServerProbeResultArray)![0];
        Assert.Equal(result, restored);
    }

    [Fact]
    public async Task HttpErrorsAndRedirectsTryFallbackInsteadOfPassing()
    {
        using var http = new HttpClient(new Responses());
        var result = await AndroidProbePolicy.CheckHttpClientAsync(http, CancellationToken.None);
        Assert.True(result.Diagnostics!.ProfileConfirmed);
        Assert.Contains("ControlService", result.Diagnostics.Attempts);
    }

    private sealed class Responses : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(request.RequestUri == AndroidProbePolicy.Primary
                ? HttpStatusCode.Redirect : HttpStatusCode.NoContent));
    }
}
