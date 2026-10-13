using DiTunnel.Core.Connection;

namespace DiTunnel.Core.Tests;

public sealed class SplitTunnelPolicyTests
{
    [Theory]
    [InlineData("рф", "xn--p1ai")]
    [InlineData(" .РФ. ", "xn--p1ai")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("https://ПРИМЕР.РФ/path?q=1#section", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("пример.рф/path", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("www.пример.рф", "www.xn--e1afmkfd.xn--p1ai")]
    [InlineData("BÜCHER.de", "xn--bcher-kva.de")]
    [InlineData("xn--e1afmkfd.xn--p1ai", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("RU", "ru")]
    [InlineData("https://EXAMPLE.org/path", "example.org")]
    [InlineData("192.0.2.40", "192.0.2.40")]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    [InlineData("localhost", "localhost")]
    [InlineData("", "")]
    [InlineData("example..org", "example..org")]
    public void RoutingUsesAsciiDomainsWithoutChangingAddresses(string input, string expected)
    {
        Assert.Equal(expected, SplitTunnelPolicy.NormalizeDomain(input));
        Assert.Equal(expected, SplitTunnelPolicy.NormalizeDomain(expected));
    }

    [Theory]
    [InlineData("xn--p1ai", "рф")]
    [InlineData(" .РФ. ", "рф")]
    [InlineData("xn--e1afmkfd.xn--p1ai", "пример.рф")]
    [InlineData("https://xn--e1afmkfd.xn--p1ai/path", "пример.рф")]
    [InlineData("https://ПРИМЕР.РФ/path", "пример.рф")]
    [InlineData("ru", "ru")]
    [InlineData("192.0.2.40", "192.0.2.40")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("example..org", "example..org")]
    public void EditorKeepsInternationalDomainsReadable(string input, string expected) =>
        Assert.Equal(expected, SplitTunnelPolicy.NormalizeDomainForDisplay(input));
}
