using DiTunnel.Platform.Linux.Network;

namespace DiTunnel.Platform.Linux.Tests;

public sealed class LinuxNetworkProtocolTests
{
    private const string Profile = "vless://00000000-0000-4000-8000-000000000001@127.0.0.1:443#Synthetic";

    [Fact]
    public void Request_discards_subscription_and_display_metadata()
    {
        var parsed = LinuxNetworkProtocol.ParseProfile(LinuxNetworkProtocol.Version, Profile, 0);
        Assert.Equal("Probe", parsed.Name);
        Assert.Null(parsed.SourceUrl);
        Assert.Null(parsed.Usage);
        Assert.Equal(Profile, parsed.Content);
    }

    [Theory]
    [InlineData(0u, Profile, 0u)]
    [InlineData(1u, Profile, 0u)]
    [InlineData(3u, Profile, 0u)]
    [InlineData(2u, Profile, 2u)]
    [InlineData(2u, "", 0u)]
    [InlineData(2u, "{\"outbounds\":[{\"protocol\":\"freedom\"}]}", 0u)]
    [InlineData(2u, Profile + "\n" + "vless://00000000-0000-4000-8000-000000000002@127.0.0.1:444", 0u)]
    public void Request_rejects_unsupported_or_ambiguous_payload(uint version, string content, uint mode)
        => Assert.ThrowsAny<Exception>(() => LinuxNetworkProtocol.ParseProfile(version, content, mode));

    [Fact]
    public void Request_bounds_utf8_bytes()
        => Assert.Throws<ArgumentException>(() => LinuxNetworkProtocol.ParseProfile(LinuxNetworkProtocol.Version, new string('Ж', LinuxNetworkProtocol.MaximumProfileBytes / 2 + 1), 0));
}
