using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DiTunnel.Core.Connection;
using DiTunnel.Core.Profiles;

namespace DiTunnel.Infrastructure.Xray;

public sealed class AmneziaWgProfileConfiguration
{
    private readonly string uapi;
    private readonly string[] addresses;
    private readonly string[] dns;
    public string ServerHost { get; }
    public ushort ServerPort { get; }
    public int Mtu { get; }
    public IReadOnlyList<string> DnsServers => Array.AsReadOnly(dns);

    internal AmneziaWgProfileConfiguration(string host, ushort port, int mtu, string uapi, string[] addresses, string[] dns)
    {
        ServerHost = host; ServerPort = port; Mtu = mtu;
        this.uapi = uapi; this.addresses = addresses; this.dns = dns;
    }

    public string BuildRuntimeConfiguration(string serverAddress, string username, string password, string readyFile = "", string? sourceAddress = null)
    {
        if (!IPAddress.TryParse(serverAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("Для AmneziaWG на Windows пока требуется IPv4 endpoint.");
        return new JsonObject
        {
            ["uapi"] = uapi.Replace("\nendpoint=__ENDPOINT__\n", $"\nendpoint={address}:{ServerPort.ToString(CultureInfo.InvariantCulture)}\n", StringComparison.Ordinal),
            ["addresses"] = new JsonArray(addresses.Select(value => (JsonNode?)value).ToArray()),
            ["dns"] = new JsonArray(dns.Select(value => (JsonNode?)value).ToArray()),
            ["mtu"] = Mtu, ["username"] = username, ["password"] = password,
            ["sourceAddress"] = sourceAddress ?? "", ["sourceInterface"] = 0, ["readyFile"] = readyFile
        }.ToJsonString();
    }

    public XrayProfileConfiguration CreateProxyConfiguration(int port, string username, string password)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new(ServerHost, ServerPort, KillSwitchTransportProtocol.Udp, new JsonObject
        {
            ["tag"] = "proxy", ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject
            {
                ["address"] = "127.0.0.1", ["port"] = port,
                ["users"] = new JsonArray(new JsonObject { ["user"] = username, ["pass"] = password })
            }) }
        }) { IsLocalProxy = true };
    }

    public override string ToString() => "AmneziaWG runtime configuration (secrets omitted)";
}

public static class AmneziaWgProfileConverter
{
    public static bool IsAmneziaWg(ImportedProfile profile) => profile.Kind.Equals(AmneziaWgConfiguration.ProfileKind, StringComparison.OrdinalIgnoreCase)
        || AmneziaWgConfiguration.LooksLikeConfiguration(profile.Content);

    public static AmneziaWgProfileConfiguration Convert(ImportedProfile profile)
    {
        var config = AmneziaWgConfiguration.Parse(profile.Content);
        var device = config.Interface;
        var peer = config.Peer;
        var lines = new StringBuilder();
        void Add(string key, string value) => lines.Append(key).Append('=').Append(value).Append('\n');
        static string HexKey(string value) => System.Convert.ToHexString(System.Convert.FromBase64String(value)).ToLowerInvariant();
        Add("private_key", HexKey(device["PrivateKey"]));
        if (device.TryGetValue("ListenPort", out var listenPort)) Add("listen_port", listenPort);
        foreach (var field in new[] { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5" })
            if (device.TryGetValue(field, out var value)) Add(field.ToLowerInvariant(), value);
        if (device.TryGetValue("HeaderProtectionKey", out var headerKey)) Add("header_protection_key", HexKey(headerKey));
        foreach (var (field, key) in new[]
        {
            ("ContentPaddingAddition", "content_padding_addition"), ("RekeyAfterTime", "rekey_after_time"),
            ("RekeyTimeout", "rekey_timeout"), ("RejectAfterTime", "reject_after_time"),
            ("KeepaliveTimeout", "keepalive_timeout"), ("MaxHandshakeAttempts", "max_handshake_attempts"),
            ("RandomTrailers", "random_trailers"), ("DisableCookies", "disable_cookies")
        }) if (device.TryGetValue(field, out var value)) Add(key, value.ToLowerInvariant());
        Add("replace_peers", "true");
        Add("public_key", HexKey(peer["PublicKey"]));
        if (peer.TryGetValue("PresharedKey", out var presharedKey)) Add("preshared_key", HexKey(presharedKey));
        Add("endpoint", "__ENDPOINT__");
        Add("replace_allowed_ips", "true");
        foreach (var prefix in peer["AllowedIPs"].Split(',', StringSplitOptions.TrimEntries)) Add("allowed_ip", prefix);
        if (peer.TryGetValue("PersistentKeepalive", out var keepalive)) Add("persistent_keepalive_interval", keepalive);
        var addresses = device["Address"].Split(',', StringSplitOptions.TrimEntries).Select(value => value.Split('/')[0]).ToArray();
        var dns = device.TryGetValue("DNS", out var resolvers)
            ? resolvers.Split(',', StringSplitOptions.TrimEntries).Where(value => IPAddress.TryParse(value, out _)).ToArray() : [];
        if (dns.Length == 0) dns = ["1.1.1.1", "1.0.0.1"];
        var mtu = device.TryGetValue("MTU", out var configuredMtu) ? int.Parse(configuredMtu, CultureInfo.InvariantCulture) : 1280;
        return new(config.EndpointHost, config.EndpointPort, mtu, lines.ToString(), addresses, dns);
    }
}
