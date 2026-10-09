using DiTunnel.Core.Connection;
using DiTunnel.Platform.Android;

namespace DiTunnel.Core.Tests;

public sealed class AndroidProbeFailureIsolationTests
{
    [Fact]
    public async Task FailedTcpPeerPreservesSuccessfulResultAndAllowsFollowingUdpCheck()
    {
        var tcp = await Task.WhenAll(
            AndroidProbeFailureIsolation.RunAsync(() => Task.FromException<ServerProbeResult>(new IOException()),
                _ => new(null, "TCP: ошибка подключения"), CancellationToken.None),
            AndroidProbeFailureIsolation.RunAsync(() => Task.FromResult(new ServerProbeResult(25, "TCP · 25 мс")),
                _ => new(null, "Ошибка"), CancellationToken.None));
        var udpRan = false;
        var udp = await AndroidProbeFailureIsolation.RunAsync(() =>
        {
            udpRan = true;
            return Task.FromResult(new ServerProbeResult(80, "HTTP · 80 мс"));
        }, _ => new(null, "Ошибка"), CancellationToken.None);

        Assert.Null(tcp[0].Milliseconds);
        Assert.Equal(25, tcp[1].Milliseconds);
        Assert.True(udpRan);
        Assert.Equal(80, udp.Milliseconds);
    }

    [Fact]
    public async Task InternalDeadlineIsAResultAndDoesNotCancelTheBatch()
    {
        var result = await AndroidProbeFailureIsolation.RunAsync(
            () => Task.FromCanceled<ServerProbeResult>(new CancellationToken(true)),
            _ => new(null, "TCP: таймаут"), CancellationToken.None);
        Assert.Equal("TCP: таймаут", result.Message);
    }

    [Fact]
    public async Task UserCancellationPropagatesEvenWhenAndroidReportsSocketClosure()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AndroidProbeFailureIsolation.RunAsync<ServerProbeResult>(() =>
        {
            cancellation.Cancel();
            throw new IOException("Socket closed");
        }, _ => throw new Xunit.Sdk.XunitException("Cancellation must not become a probe result."), cancellation.Token));
    }

    [Fact]
    public async Task CancelledBatchDoesNotStartAnotherProbe()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AndroidProbeFailureIsolation.RunAsync<ServerProbeResult>(
            () => throw new Xunit.Sdk.XunitException("A cancelled probe must not start."),
            _ => new(null, "Ошибка"), new CancellationToken(true)));
    }

    [Fact]
    public async Task SynchronousSetupFailureIsIsolatedToo()
    {
        var result = await AndroidProbeFailureIsolation.RunAsync<ServerProbeResult>(
            () => throw new InvalidOperationException("Нет сети"),
            error => new(null, error.Message), CancellationToken.None);
        Assert.Equal("Нет сети", result.Message);
    }
}