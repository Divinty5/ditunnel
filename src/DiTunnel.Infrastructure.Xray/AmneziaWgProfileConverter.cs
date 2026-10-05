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
    public IReadOnlyList<string> TunnelAddresses { get; internal init; } = [];
    public IReadOnlyList<string> AllowedIps { get; internal init; } = [];

    public bool SupportsAddressFamily(AddressFamily family) => addresses.Any(value => IPAddress.Parse(value).AddressFamily == family)
        && AllowedIps.Any(value => IPAddress.Parse(value.Split('/')[0]).AddressFamily == family);

    public IPAddress HandshakeProbeAddress()
    {
        foreach (var prefix in AllowedIps.OrderBy(value => value.Contains(':') ? 1 : 0))
        {
            var parts = prefix.Split('/');
            var address = IPAddress.Parse(parts[0]);
            if (!SupportsAddressFamily(address.AddressFamily)) continue;
            var bits = int.Parse(parts[1], CultureInfo.InvariantCulture);
            var bytes = address.GetAddressBytes();
            for (var index = 0; index < bytes.Length; index++)
            {
                var remaining = Math.Clamp(bits - index * 8, 0, 8);
                bytes[index] &= (byte)(0xff << (8 - remaining));
            }
            bool IsLocal(IPAddress value) => TunnelAddresses.Any(item => IPAddress.Parse(item.Split('/')[0]).Equals(value));
            var preferred = IPAddress.Parse(bytes.Length == 4 ? "1.1.1.1" : "2606:4700:4700::1111");
            var preferredBytes = preferred.GetAddressBytes();
            var preferredInside = true;
            for (var index = 0; index < bytes.Length; index++)
            {
                var remaining = Math.Clamp(bits - index * 8, 0, 8);
                if ((preferredBytes[index] & (byte)(0xff << (8 - remaining))) != bytes[index]) preferredInside = false;
            }
            if (preferredInside && !IsLocal(preferred)) return preferred;
            var networkAddress = new IPAddress(bytes);
            // An arbitrary address inside the peer's routes is sufficient to initiate the
            // encrypted handshake. A UDP response is neither required nor treated as success.
            if (bits < bytes.Length * 8) bytes[^1] |= 1;
            var candidate = new IPAddress(bytes);
            if (!IsLocal(candidate)) return candidate;
            if (!IsLocal(networkAddress)) return networkAddress;
            // At most N local addresses can occupy N candidates. Try N + 1,
            // stopping at the prefix boundary instead of returning another local address.
            for (var attempt = 0; attempt <= TunnelAddresses.Count; attempt++)
            {
                for (var index = bytes.Length - 1; index >= 0; index--)
                    if (++bytes[index] != 0) break;
                candidate = new IPAddress(bytes);
                if (!ContainsAddress(prefix, candidate)) break;
                if (!IsLocal(candidate)) return candidate;
            }
        }
        throw new NotSupportedException("В AllowedIPs AmneziaWG нет удалённого адреса для проверки handshake.");
    }

    public IReadOnlyList<string> TunnelDnsServers()
    {
        var servers = dns.Where(value => SupportsAddressFamily(IPAddress.Parse(value).AddressFamily)
            && AllowedIps.Any(prefix => ContainsAddress(prefix, IPAddress.Parse(value)))).ToArray();
        return servers.Length > 0 ? servers
            : throw new NotSupportedException("DNS-серверы профиля AmneziaWG недоступны через Address и AllowedIPs.");
    }

    private static bool ContainsAddress(string prefix, IPAddress address)
    {
        var parts = prefix.Split('/');
        var network = IPAddress.Parse(parts[0]);
        if (network.AddressFamily != address.AddressFamily) return false;
        var bits = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var left = network.GetAddressBytes();
        var right = address.GetAddressBytes();
        for (var index = 0; index < left.Length; index++)
        {
            var mask = (byte)(0xff << (8 - Math.Clamp(bits - index * 8, 0, 8)));
            if ((left[index] & mask) != (right[index] & mask)) return false;
        }
        return true;
    }

    public string BuildUserspaceConfiguration(string serverAddress)
    {
        if (!IPAddress.TryParse(serverAddress, out var address)) throw new ArgumentException("Endpoint должен быть IP-адресом.", nameof(serverAddress));
        var endpoint = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{ServerPort}" : $"{address}:{ServerPort}";
        return uapi.Replace("\nendpoint=__ENDPOINT__\n", $"\nendpoint={endpoint}\n", StringComparison.Ordinal);
    }

    internal AmneziaWgProfileConfiguration(string host, ushort port, int mtu, string uapi, string[] addresses, string[] dns)
    {
        ServerHost = host; ServerPort = port; Mtu = mtu;
        this.uapi = uapi; this.addresses = addresses; this.dns = dns;
    }

    public string BuildRuntimeConfiguration(string serverAddress, string username, string password, string readyFile = "", string? sourceAddress = null)
    {
        if (!IPAddress.TryParse(serverAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("Для AmneziaWG на Windows пока требуется IPv4 endpoint.");
        return BuildProxyRuntimeConfiguration(serverAddress, username, password, readyFile, sourceAddress);
    }

    public string BuildProxyRuntimeConfiguration(string serverAddress, string username, string password, string readyFile = "", string? sourceAddress = null)
    {
        return new JsonObject
        {
            ["uapi"] = BuildUserspaceConfiguration(serverAddress),
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
        }) { IsLocalProxy = true,
            SupportsIpv4 = SupportsAddressFamily(AddressFamily.InterNetwork),
            SupportsIpv6 = SupportsAddressFamily(AddressFamily.InterNetworkV6) };
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
        if (dns.Length == 0)
        {
            bool Supports(AddressFamily family) => addresses.Any(value => IPAddress.Parse(value).AddressFamily == family)
                && peer["AllowedIPs"].Split(',', StringSplitOptions.TrimEntries).Any(value => IPAddress.Parse(value.Split('/')[0]).AddressFamily == family);
            dns = (Supports(AddressFamily.InterNetwork) ? new[] { "1.1.1.1", "1.0.0.1" } : [])
                .Concat(Supports(AddressFamily.InterNetworkV6) ? ["2606:4700:4700::1111", "2606:4700:4700::1001"] : []).ToArray();
        }
        var mtu = device.TryGetValue("MTU", out var configuredMtu) ? int.Parse(configuredMtu, CultureInfo.InvariantCulture) : 1280;
        return new(config.EndpointHost, config.EndpointPort, mtu, lines.ToString(), addresses, dns)
        {
            TunnelAddresses = Array.AsReadOnly(device["Address"].Split(',', StringSplitOptions.TrimEntries)),
            AllowedIps = Array.AsReadOnly(peer["AllowedIPs"].Split(',', StringSplitOptions.TrimEntries))
        };
    }
}
