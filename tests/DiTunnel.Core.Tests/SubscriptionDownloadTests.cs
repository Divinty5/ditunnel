using System.Net;
using System.Text;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Tests;

public sealed class SubscriptionDownloadTests
{
    private const string Link = "vless://00000000-0000-0000-0000-000000000001@example.com:443?security=tls#Test%20server";

    private static string NativeAwgLink(string fields)
    {
        var key = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
        var content = $"""
            [Interface]
            PrivateKey = {key}
            Address = 192.0.2.2/32
            H1 = 12345-12355
            S3 = 12
            {fields}
            [Peer]
            PublicKey = {key}
            Endpoint = awg.example.com:51820
            AllowedIPs = 0.0.0.0/0
            """;
        return "vpn://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(content)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private sealed class Responses(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SlowRedirects : HttpMessageHandler
    {
        public List<CancellationToken> Tokens { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            if (Tokens.Count == 1)
                return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/next") } };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class BrokenResponse : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Response interrupted."));
    }

    [Fact]
    public async Task RedirectChainIsCancelledByTheSingleDownloadDeadline()
    {
        using var handler = new SlowRedirects();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProfileImporter(client, TimeSpan.FromMilliseconds(200)).ImportAsync("https://example.com/start"));
        Assert.Equal(2, handler.Tokens.Count);
        Assert.True(handler.Tokens[^1].IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentifiesClientAndRequestsLinkSubscription(bool base64)
    {
        using var client = new HttpClient(new Responses(request =>
        {
            // Reproduce providers that return HTTP 500 when User-Agent is missing.
            if (request.Headers.UserAgent.Count == 0)
                return new(HttpStatusCode.InternalServerError);
            Assert.Contains(request.Headers.UserAgent, item => item.Product?.Name == "DiTunnel");
            Assert.Contains("v2rayN", request.Headers.UserAgent.ToString());
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "text/plain");
            var content = Link + "\ninvalid\n" + Link;
            if (base64) content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
            response.Headers.TryAddWithoutValidation("Subscription-Userinfo", "upload=10; download=30; total=100");
            return response;
        }));
        var importer = new ProfileImporter(client);

        var profile = Assert.Single(await importer.ImportAsync("https://example.com/sub"));

        Assert.Equal(Link, profile.Content);
        Assert.Equal("Test server", profile.Name);
        Assert.Equal(60m, profile.Usage!.RemainingBytes);
        Assert.Equal(1, importer.SkippedEntries);
        Assert.Empty(client.DefaultRequestHeaders.UserAgent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportsBothAwgVersionsAlongsideOtherProtocols(bool base64)
    {
        var awg2 = NativeAwgLink("I1 = <r 10>\nI2 =");
        var awg31 = NativeAwgLink("RandomTrailers = on\nDisableCookies = on");
        var content = $"{awg2}\nhy2://test@example.com:443#Test {Link}\n{awg31}\n{NativeAwgLink("DisableCookies = invalid")}\n{awg2}";
        if (base64) content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        using var client = new HttpClient(new Responses(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
            response.Headers.TryAddWithoutValidation("Subscription-Userinfo", "upload=0; download=30; total=100");
            return response;
        }));
        var importer = new ProfileImporter(client);

        var profiles = await importer.ImportAsync("https://example.com/sub");

        Assert.Equal(new[] { "AmneziaWG 2.0", "Hysteria 2", "VLESS", "AmneziaWG 3.1" }, profiles.Select(profile => profile.ProtocolName));
        Assert.All(profiles, profile => Assert.Equal(70m, profile.Usage!.RemainingBytes));
        Assert.Equal(1, importer.SkippedEntries);
    }

    [Fact]
    public async Task MalformedAwgResponseReportsAConfigurationError()
    {
        var link = NativeAwgLink("RandomTrailers = invalid");
        using var client = new HttpClient(new Responses(_ => new(HttpStatusCode.OK) { Content = new StringContent(link) }));

        var error = await Assert.ThrowsAsync<FormatException>(() => new ProfileImporter(client).ImportAsync("https://example.com/sub"));

        Assert.Contains("некорректную конфигурацию AWG", error.Message);
        Assert.DoesNotContain(link, error.Message);
    }

    [Fact]
    public async Task InterruptedBodyReportsADownloadError()
    {
        using var client = new HttpClient(new Responses(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenResponse()) }));

        await Assert.ThrowsAsync<HttpRequestException>(() => new ProfileImporter(client).ImportAsync("https://example.com/sub"));
    }

    [Theory]
    [InlineData("/subscription")]
    [InlineData("https://other.example.com/subscription")]
    public async Task SendsSubscriptionHeadersAfterHttpsRedirect(string location)
    {
        var calls = 0;
        string? userAgent = null;
        using var client = new HttpClient(new Responses(request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "text/plain");
            if (++calls == 1)
            {
                userAgent = request.Headers.UserAgent.ToString();
                return new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };
            }
            Assert.Equal(new Uri(new Uri("https://example.com/start"), location), request.RequestUri);
            Assert.Equal(userAgent, request.Headers.UserAgent.ToString());
            return new(HttpStatusCode.OK) { Content = new StringContent(Link) };
        }));

        Assert.Single(await new ProfileImporter(client).ImportAsync("https://example.com/start"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RejectsRedirectToHttpBeforeSendingAnotherRequest()
    {
        var calls = 0;
        using var client = new HttpClient(new Responses(_ =>
        {
            calls++;
            return new(HttpStatusCode.Found) { Headers = { Location = new Uri("http://example.com/sub") } };
        }));

        await Assert.ThrowsAsync<FormatException>(() => new ProfileImporter(client).ImportAsync("https://example.com/sub"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreservesHttpFailureInsteadOfParsingErrorPageAsServers()
    {
        using var client = new HttpClient(new Responses(_ => new(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(Link)
        }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new ProfileImporter(client).ImportAsync("https://example.com/sub"));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task RejectsOversizedSubscription()
    {
        using var client = new HttpClient(new Responses(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('a', ProfileParser.MaximumBytes + 1))
        }));

        await Assert.ThrowsAsync<FormatException>(() => new ProfileImporter(client).ImportAsync("https://example.com/sub"));
    }

    [Fact]
    public async Task LocalImportDoesNotSendHttpRequest()
    {
        using var client = new HttpClient(new Responses(_ => throw new Exception("Local profiles must not use HTTP.")));
        Assert.Equal(Link, Assert.Single(await new ProfileImporter(client).ImportAsync(Link)).Content);
    }
}
