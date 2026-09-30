using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Core.Tests;

public sealed class AmneziaVpnLinkTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static JsonObject Client => new()
    {
        ["client_priv_key"] = Key, ["server_pub_key"] = Key, ["psk_key"] = Key,
        ["client_ip"] = "192.0.2.2", ["hostName"] = "vpn.example.com", ["port"] = 51820,
        ["allowed_ips"] = new JsonArray("0.0.0.0/0", "::/0"), ["persistent_keep_alive"] = "25",
        ["Jc"] = "4", ["Jmin"] = 10, ["Jmax"] = 40, ["S1"] = "16", ["S2"] = "32",
        ["S3"] = "12", ["S4"] = "12", ["H1"] = "12345-12355", ["H2"] = "23456",
        ["H3"] = "34567", ["H4"] = "45678", ["I1"] = "<b 0x01020304><r 10>"
    };
    private static JsonObject Share(JsonNode? last, string container = "amnezia-awg2") => new()
    {
        ["description"] = "Test AWG 2", ["dns1"] = "192.0.2.53", ["dns2"] = "192.0.2.54",
        ["password"] = "synthetic-management-secret", ["containers"] = new JsonArray(new JsonObject
        { ["container"] = container, ["awg"] = new JsonObject { ["last_config"] = last } })
    };
    private static string Link(string json, bool compressed = true, int lengthAdjustment = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (compressed)
        {
            using var buffer = new MemoryStream();
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(bytes.Length + lengthAdjustment));
            buffer.Write(header);
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, true)) zlib.Write(bytes);
            bytes = buffer.ToArray();
        }
        return "vpn://" + Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    [Theory]
    [InlineData("amnezia-awg", true, true)]
    [InlineData("amnezia-awg2", true, true)]
    [InlineData("amnezia-awg2", false, true)]
    [InlineData("amnezia-awg2", true, false)]
    public void ImportsOfficialContainerAndPreservesAwg2Parameters(string type, bool compressed, bool jsonString)
    {
        JsonNode last = jsonString ? JsonValue.Create(Client.ToJsonString())! : Client;
        var profile = Assert.Single(ProfileParser.Parse(Link(Share(last, type).ToJsonString(), compressed), out var skipped));
        var config = AmneziaWgConfiguration.Parse(profile.Content);
        Assert.Equal("AmneziaWG", profile.Kind);
        Assert.Equal("Test AWG 2", profile.Name);
        Assert.Equal("192.0.2.2/32", config.Interface["Address"]);
        Assert.Equal("192.0.2.53, 192.0.2.54", config.Interface["DNS"]);
        Assert.Equal("12345-12355", config.Interface["H1"]);
        Assert.Equal("12", config.Interface["S3"]);
        Assert.Equal("<b 0x01020304><r 10>", config.Interface["I1"]);
        Assert.Equal("25", config.Peer["PersistentKeepalive"]);
        Assert.DoesNotContain("synthetic-management-secret", profile.Content);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void ImportsEmbeddedNativeConfigAndBase64WrappedLink()
    {
        var fields = Client;
        var profile = Assert.Single(ProfileParser.Parse(Link(Share(JsonValue.Create(fields.ToJsonString())).ToJsonString())));
        var native = profile.Content;
        var share = Share(new JsonObject { ["config"] = native });
        var link = Link(share.ToJsonString());
        Assert.Equal(native, Assert.Single(ProfileParser.Parse(link)).Content);
        Assert.Equal(native, Assert.Single(ProfileParser.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(link)))).Content);
        Assert.Equal(native, Assert.Single(ProfileParser.Parse(share.ToJsonString())).Content);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2097152)]
    public void RejectsIncorrectDecompressedLength(int adjustment)
    {
        var error = Assert.Throws<FormatException>(() => ProfileParser.Parse(Link(Share(JsonValue.Create(Client.ToJsonString())).ToJsonString(), lengthAdjustment: adjustment)));
        Assert.DoesNotContain(Key, error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ResolvesAmneziaNativeDnsPlaceholders(bool missingDns, bool rawLastConfig)
    {
        var original = Assert.Single(ProfileParser.Parse(Link(Share(JsonValue.Create(Client.ToJsonString())).ToJsonString()))).Content;
        var native = original.Replace("DNS = 192.0.2.53, 192.0.2.54", "DNS = $PRIMARY_DNS, $SECONDARY_DNS");
        var share = Share(rawLastConfig ? JsonValue.Create(native) : new JsonObject { ["config"] = native });
        if (missingDns) { share.Remove("dns1"); share.Remove("dns2"); }
        var profile = Assert.Single(ProfileParser.Parse(Link(share.ToJsonString())));
        var configuration = AmneziaWgConfiguration.Parse(profile.Content);
        Assert.Equal(missingDns ? "1.1.1.1, 1.0.0.1" : "192.0.2.53, 192.0.2.54", configuration.Interface["DNS"]);
        Assert.Equal(Key, configuration.Interface["PrivateKey"]);
        Assert.Equal("<b 0x01020304><r 10>", configuration.Interface["I1"]);
        Assert.DoesNotContain("$PRIMARY_DNS", profile.Content);
    }

    [Theory]
    [InlineData("vpn://not-a-link")]
    [InlineData("vpn://AAAJ6Xj")]
    [InlineData("vpn://")]
    public void RejectsMalformedLinksWithoutEchoingInput(string link) => Assert.Throws<FormatException>(() => ProfileParser.Parse(link));

    [Fact]
    public void RejectsMissingClientAndUnsupportedProtocolWithSpecificError()
    {
        foreach (var share in new[] { Share(null), Share(null, "amnezia-openvpn"), new JsonObject { ["api_key"] = "synthetic-api-key" } })
        {
            var error = Assert.Throws<FormatException>(() => ProfileParser.Parse(Link(share.ToJsonString())));
            Assert.Contains("готовой клиентской конфигурации AWG", error.Message);
            Assert.DoesNotContain("synthetic-api-key", error.Message);
        }
    }

    [Fact]
    public void RejectsDuplicateFieldsAndLineInjection()
    {
        var duplicate = Share(JsonValue.Create(Client.ToJsonString())).ToJsonString().Replace("\"description\":", "\"description\":\"Other\",\"description\":");
        Assert.Throws<FormatException>(() => ProfileParser.Parse(Link(duplicate)));
        var client = Client;
        client["I1"] = "<r 10>\nPostUp = command";
        Assert.Throws<FormatException>(() => ProfileParser.Parse(Link(Share(JsonValue.Create(client.ToJsonString())).ToJsonString())));
    }

    [Fact]
    public void PreservesExplicitProtocolVersionThroughStorageRoundTrip()
    {
        var share = Share(JsonValue.Create(Client.ToJsonString()));
        share["containers"]![0]!["awg"]!["protocol_version"] = "3.1";
        var profile = Assert.Single(ProfileParser.Parse(Link(share.ToJsonString())));
        var restored = System.Text.Json.JsonSerializer.Deserialize<ImportedProfile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
        Assert.Equal("3.1", restored.ProtocolVersion);
        Assert.Equal("AmneziaWG 3.1", restored.ProtocolName);
    }
}
