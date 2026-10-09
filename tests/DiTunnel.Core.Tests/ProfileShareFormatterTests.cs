using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Tests;

public sealed class ProfileShareFormatterTests
{
    // Synthetic client keys and reserved documentation addresses only.
    private const string Key = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
    private static string Configuration => $"""
        # A comment with https://example.com must not be exported as a link itself.
        [Interface]
        PrivateKey = {Key}
        Address = 192.0.2.2/32, 2001:db8::2/128
        DNS = 192.0.2.53, 2001:db8::53
        MTU = 1280
        Jc = 4
        Jmin = 40
        Jmax = 70
        S1 = 15
        S2 = 20
        H1 = 12345
        H2 = 23456
        H3 = 34567
        H4 = 45678
        [Peer]
        PublicKey = {Key}
        PresharedKey = {Key}
        Endpoint = [2001:db8::1]:51820
        AllowedIPs = 0.0.0.0/0, ::/0
        PersistentKeepalive = 25
        """;

    [Theory]
    [InlineData(null, "")]
    [InlineData("1.5", "I1 = <b 0x01020304><r 10>")]
    [InlineData("2.0", "S3 = 12\nS4 = 12\nH1 = 12345-12355")]
    [InlineData("3.1", "RandomTrailers = true\nDisableCookies = false\nContentPaddingAddition = 10-20")]
    public void SharedAwgLinkPreservesClientConfigurationAndDeclaredVersion(string? version, string extra)
    {
        var content = Configuration;
        if (extra.Contains("H1 =")) content = content.Replace("H1 = 12345\n", "");
        content = content.Replace("[Peer]", extra + "\n[Peer]");
        var original = new ImportedProfile("Друг · ещё один сервер", AmneziaWgConfiguration.ProfileKind, content,
            "private-source", "Private subscription", "https://example.com/private-subscription") { ProtocolVersion = version };
        var link = ProfileShareFormatter.CreateLink(original)!;
        Assert.StartsWith("vpn://", link);
        Assert.DoesNotContain("\n", link);
        var imported = Assert.Single(ProfileParser.Parse(link, out var skipped));
        Assert.Equal(0, skipped);
        Assert.Equal(original.Name, imported.Name);
        Assert.Equal(original.Kind, imported.Kind);
        Assert.Equal(original.Content, imported.Content);
        Assert.Equal(original.ProtocolVersion, imported.ProtocolVersion);
        Assert.Equal(original.ProtocolName, imported.ProtocolName);
        Assert.Null(imported.SourceUrl);
        Assert.Equal("legacy", imported.SourceId);

        var bytes = Convert.FromBase64String(link[6..].Replace('-', '+').Replace('_', '/').PadRight((link.Length - 6 + 3) / 4 * 4, '='));
        using var compressed = new MemoryStream(bytes, 4, bytes.Length - 4);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var json = new MemoryStream();
        zlib.CopyTo(json);
        Assert.Equal(json.Length, BinaryPrimitives.ReadUInt32BigEndian(bytes));
        using var document = JsonDocument.Parse(json.ToArray());
        Assert.Equal(new[] { "description", "containers" }, document.RootElement.EnumerateObject().Select(field => field.Name));
        Assert.DoesNotContain("private-subscription", document.RootElement.GetRawText());
    }

    [Fact]
    public void DeclaredVersionSurvivesEvenWhenNotInferredFromFields()
    {
        var profile = new ImportedProfile("AWG", "AmneziaWG", Configuration) { ProtocolVersion = "3.1" };
        var imported = Assert.Single(ProfileParser.Parse(ProfileShareFormatter.CreateLink(profile)!));
        Assert.Equal("3.1", imported.ProtocolVersion);
        Assert.Equal("AmneziaWG 3.1", imported.ProtocolName);
    }

    [Fact]
    public void InvalidAwgAndUnsupportedJsonAreNotShareable()
    {
        Assert.Null(ProfileShareFormatter.CreateLink(null));
        Assert.Null(ProfileShareFormatter.CreateLink(new("Bad", "AmneziaWG", "[Interface]\nPrivateKey = invalid")));
        Assert.Null(ProfileShareFormatter.CreateLink(new("JSON", "Xray JSON", "{}")));
        Assert.Null(ProfileShareFormatter.CreateLink(new("Huge", "AmneziaWG", Configuration + "\n#" + new string('a', ProfileParser.MaximumBytes))));
    }

    [Theory]
    [InlineData("VLESS", "vless://test@192.0.2.1:443#Test")]
    [InlineData("Hysteria 2", "hy2://test@192.0.2.1:443#Test")]
    [InlineData("SS", "ss://test@192.0.2.1:8388#Test")]
    public void ExistingProtocolLinksRemainUnchanged(string kind, string content) =>
        Assert.Equal(content, ProfileShareFormatter.CreateLink(new("Test", kind, content)));
}