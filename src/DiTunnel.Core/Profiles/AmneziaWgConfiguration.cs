using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DiTunnel.Core.Profiles;

/// <summary>A structurally validated, single-peer client configuration. Parsing never executes hooks.</summary>
public sealed class AmneziaWgConfiguration
{
    public const string ProfileKind = "AmneziaWG";
    private static readonly HashSet<string> InterfaceFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "PrivateKey", "Address", "DNS", "MTU", "ListenPort",
        "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4",
        "I1", "I2", "I3", "I4", "I5", "HeaderProtectionKey", "ContentPaddingAddition",
        "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts",
        "RandomTrailers", "DisableCookies"
    };
    private static readonly HashSet<string> PeerFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "PublicKey", "PresharedKey", "Endpoint", "AllowedIPs", "PersistentKeepalive"
    };
    public IReadOnlyDictionary<string, string> Interface { get; }
    public IReadOnlyDictionary<string, string> Peer { get; }
    public string EndpointHost { get; }
    public ushort EndpointPort { get; }
    public string ProtocolVersion => Interface.ContainsKey("RandomTrailers") || Interface.ContainsKey("DisableCookies") ? "3.1"
        : Interface.Keys.Any(key => new[] { "HeaderProtectionKey", "ContentPaddingAddition", "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts" }.Contains(key, StringComparer.OrdinalIgnoreCase)) ? "3.0"
        : Interface.ContainsKey("S3") || Interface.ContainsKey("S4") || Interface.Any(entry => entry.Key.StartsWith('H') && entry.Value.Contains('-')) ? "2.0"
        : Interface.Keys.Any(key => new[] { "I1", "I2", "I3", "I4", "I5" }.Contains(key, StringComparer.OrdinalIgnoreCase)) ? "1.5" : "1.0";

    private AmneziaWgConfiguration(Dictionary<string, string> device, Dictionary<string, string> peer, string host, ushort port)
    {
        Interface = new ReadOnlyDictionary<string, string>(device);
        Peer = new ReadOnlyDictionary<string, string>(peer);
        EndpointHost = host;
        EndpointPort = port;
    }

    // Configuration keys and packet templates must never appear in diagnostic object formatting.
    public override string ToString() => "AmneziaWG configuration (secrets omitted)";

    public static bool LooksLikeConfiguration(string input) => input.Split(['\r', '\n'])
        .Any(line => line.Split('#', 2)[0].Trim().Equals("[Interface]", StringComparison.OrdinalIgnoreCase));

    public static AmneziaWgConfiguration Parse(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > ProfileParser.MaximumBytes) throw Invalid();
        input = input.Trim().TrimStart('\uFEFF');
        var device = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        var deviceSeen = false;
        var peerSeen = false;
        foreach (var raw in input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Split('#', 2)[0].Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase))
            {
                if (deviceSeen || peerSeen) throw Invalid();
                deviceSeen = true;
                current = device;
                continue;
            }
            if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase))
            {
                if (!deviceSeen || peerSeen) throw Invalid();
                peerSeen = true;
                current = peer;
                continue;
            }
            var separator = line.IndexOf('=');
            if (current is null || separator <= 0) throw Invalid();
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            // Reject hooks (PreUp/PostUp/etc.) and unknown fields rather than silently dropping them.
            if (!(ReferenceEquals(current, device) ? InterfaceFields : PeerFields).Contains(key) ||
                value.Length == 0 || value.Any(char.IsControl) || !current.TryAdd(key, value)) throw Invalid();
        }
        ValidateKey(Required(device, "PrivateKey"));
        ValidateKey(Required(peer, "PublicKey"));
        if (peer.TryGetValue("PresharedKey", out var psk)) ValidateKey(psk);
        if (device.TryGetValue("HeaderProtectionKey", out var hpk)) ValidateKey(hpk);
        ValidatePrefixes(Required(device, "Address"));
        ValidatePrefixes(Required(peer, "AllowedIPs"));
        if (device.TryGetValue("DNS", out var dns))
            foreach (var entry in dns.Split(','))
                if (!IPAddress.TryParse(entry.Trim(), out _) && Uri.CheckHostName(entry.Trim()) != UriHostNameType.Dns) throw Invalid();
        if (device.TryGetValue("MTU", out var mtu)) ValidateNumber(mtu, 576, 65535);
        if (device.TryGetValue("ListenPort", out var listenPort)) ValidateNumber(listenPort, 0, 65535);
        foreach (var key in new[] { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4" })
            if (device.TryGetValue(key, out var value)) ValidateNumber(value, 0, key.StartsWith('S') ? 65535u : uint.MaxValue);
        if (device.TryGetValue("Jmin", out var min) && device.TryGetValue("Jmax", out var max) &&
            uint.Parse(min, CultureInfo.InvariantCulture) > uint.Parse(max, CultureInfo.InvariantCulture)) throw Invalid();
        foreach (var key in new[] { "H1", "H2", "H3", "H4", "ContentPaddingAddition", "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts" })
            if (device.TryGetValue(key, out var value)) ValidateRange(value);
        if (peer.TryGetValue("PersistentKeepalive", out var keepalive)) ValidateRange(keepalive);
        foreach (var key in new[] { "RandomTrailers", "DisableCookies" })
            if (device.TryGetValue(key, out var value) && value is not ("0" or "1") && !bool.TryParse(value, out _)) throw Invalid();
        if (!device.Keys.Any(key => !new[] { "PrivateKey", "Address", "DNS", "MTU", "ListenPort" }.Contains(key, StringComparer.OrdinalIgnoreCase)))
            throw new FormatException("Не удалось импортировать: конфигурация не содержит параметров AmneziaWG.");
        var endpoint = Required(peer, "Endpoint");
        var colon = endpoint.LastIndexOf(':');
        if (colon <= 0) throw Invalid();
        var host = endpoint[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6 || host.Contains('%')) throw Invalid();
        }
        else if (host.Contains(':') || Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) throw Invalid();
        var port = (ushort)ValidateNumber(endpoint[(colon + 1)..], 1, 65535);
        return new(device, peer, host, port);
    }

    private static string Required(Dictionary<string, string> fields, string key) => fields.TryGetValue(key, out var value) ? value : throw Invalid();

    private static void ValidateKey(string value)
    {
        Span<byte> bytes = stackalloc byte[32];
        if (value.Length != 44 || !Convert.TryFromBase64String(value, bytes, out var written) || written != 32) throw Invalid();
    }

    private static void ValidatePrefixes(string value)
    {
        foreach (var entry in value.Split(','))
        {
            var parts = entry.Trim().Split('/');
            if (parts.Length != 2 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var address)) throw Invalid();
            ValidateNumber(parts[1], 0, address.AddressFamily == AddressFamily.InterNetwork ? 32u : 128u);
        }
    }

    private static void ValidateRange(string value)
    {
        if (value == "(off)") return;
        var parts = value.Split('-');
        if (parts.Length is < 1 or > 2) throw Invalid();
        var min = ValidateNumber(parts[0], 0, uint.MaxValue);
        if (parts.Length == 2 && min > ValidateNumber(parts[1], 0, uint.MaxValue)) throw Invalid();
    }

    private static uint ValidateNumber(string value, uint min, uint max)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max) throw Invalid();
        return number;
    }

    private static FormatException Invalid() => new("Не удалось импортировать: некорректная конфигурация AmneziaWG. Нужны секции Interface и Peer одного сервера без командных хуков.");
}
