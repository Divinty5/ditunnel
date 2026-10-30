using System.Net;
using System.Text;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Tests;

public sealed class SubscriptionDownloadTests
{
    private const string Link = "vless://00000000-0000-0000-0000-000000000001@example.com:443?security=tls#Test%20server";

    private sealed class Responses(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
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
