using System.Text;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Tests;

public sealed class ProfileImporterTests
{
    private const string Link = "vless://00000000-0000-0000-0000-000000000001@example.com:443?security=tls#Test%20server";
    [Fact] public void ImportsLinkAndName()
    {
        var profile = Assert.Single(ProfileParser.Parse(Link));
        Assert.Equal("Test server", profile.Name);
        Assert.Equal("VLESS", profile.Kind);
        Assert.Equal(Link, profile.Content);
    }
    [Fact] public void ImportsBase64SubscriptionAndDeduplicates()
    {
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(Link + "\n" + Link));
        Assert.Single(ProfileParser.Parse(content));
    }
    [Fact]
    public void ImportsWhitespaceSeparatedSubscription()
    {
        var second = Link.Replace("#Test%20server", "#Second");
        Assert.Equal(2, ProfileParser.Parse(Link + " " + second).Count);
    }
    [Theory]
    [InlineData("")]
    [InlineData("not a configuration")]
    [InlineData("https://example.com")]
    [InlineData("vless://bad@example.com:443")]
    [InlineData("trojan://example.com:443")]
    [InlineData("{\"outbounds\":[]}")]
    [InlineData("{broken")]
    [InlineData("vmess://e30=")]
    public void RejectsInvalidInput(string input) => Assert.Throws<FormatException>(() => ProfileParser.Parse(input));
    [Fact]
    public void ImportsValidEntriesAndReportsBadOnes()
    {
        var profiles = ProfileParser.Parse(Link + "\n# comment\ninvalid\n" + Link, out var skipped);
        Assert.Single(profiles);
        Assert.Equal(1, skipped);
    }
    [Fact]
    public void DoesNotCountWordsInProfileNamesAsSkippedEntries()
    {
        var second = Link.Replace("#Test%20server", "#Another server name");
        var profiles = ProfileParser.Parse(Link + "\n" + second, out var skipped);
        Assert.Equal(2, profiles.Count);
        Assert.Equal(0, skipped);
    }
    [Fact] public void AcceptsXrayJson() => Assert.Equal("Xray JSON", Assert.Single(ProfileParser.Parse("{\"outbounds\":[{\"protocol\":\"freedom\"}]}" )).Kind);
    [Fact] public void RejectsLargeInput() => Assert.Throws<FormatException>(() => ProfileParser.Parse(new string('a', ProfileParser.MaximumBytes + 1)));
    [Fact] public async Task RejectsInsecureSubscription() => await Assert.ThrowsAsync<FormatException>(() => new ProfileImporter().ImportAsync("http://example.com/sub"));
    [Theory]
    [InlineData("hy2://password@example.com:443#HY2", "Hysteria 2")]
    [InlineData("trojan://password@example.com:443#Trojan", "TROJAN")]
    [InlineData("ss://YWVzLTEyOC1nY206cGFzcw@example.com:8388#SS", "SS")]
    public void AcceptsOtherProtocols(string input, string kind) => Assert.Equal(kind, Assert.Single(ProfileParser.Parse(input)).Kind);

    [Fact]
    public void RemovesSubscriptionQuotaDecorationFromServerName()
    {
        var profile = Assert.Single(ProfileParser.Parse("vless://00000000-0000-0000-0000-000000000001@example.com:443?security=tls#WS-8080-TestDiTunnel%7C%F0%9F%93%8A99.78GB"));
        Assert.Equal("WS-8080-TestDiTunnel", profile.Name);
    }
}
