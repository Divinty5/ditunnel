using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DiTunnel.Core.Profiles;

// Amnezia self-hosted shares: Base64URL(qCompress(JSON)). qCompress prefixes
// the zlib stream with the uncompressed byte length as a big-endian uint32.
internal static class AmneziaVpnLink
{
    private static FormatException Invalid() => new("Не удалось импортировать: ссылка AmneziaVPN повреждена или содержит некорректную конфигурацию AWG.");
    private static FormatException MissingClient() => new("Ссылка AmneziaVPN не содержит готовой клиентской конфигурации AWG. Экспортируйте в AmneziaVPN конфигурацию AmneziaWG для подключения, а не доступ к управлению сервером или API.");

    internal static IReadOnlyList<ImportedProfile> Parse(string link)
    {
        try
        {
            var encoded = link[6..].Trim();
            if (encoded.Any(char.IsWhiteSpace)) throw Invalid();
            encoded = encoded.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            if (bytes.Length > ProfileParser.MaximumBytes) throw Invalid();
            // Older uncompressed JSON shares are also accepted by AmneziaVPN.
            if (bytes.Length > 0 && bytes[0] == (byte)'{') return ParseJson(bytes);
            if (bytes.Length < 6) throw Invalid();
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            if (length == 0 || length > ProfileParser.MaximumBytes) throw Invalid();
            using var compressed = new MemoryStream(bytes, 4, bytes.Length - 4);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var block = new byte[8192];
            int read;
            while ((read = zlib.Read(block)) != 0)
            {
                if (output.Length + read > length) throw Invalid();
                output.Write(block, 0, read);
            }
            if (output.Length != length) throw Invalid();
            return ParseJson(output.ToArray());
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        { throw Invalid(); }
        catch (FormatException error) when (!error.Message.StartsWith("Ссылка AmneziaVPN", StringComparison.Ordinal))
        { throw Invalid(); }
    }

    internal static IReadOnlyList<ImportedProfile> ParseJson(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        CheckObject(root);
        if (!root.TryGetProperty("containers", out var containers) || containers.ValueKind != JsonValueKind.Array) throw MissingClient();
        if (containers.GetArrayLength() > 1000) throw Invalid();
        var result = new List<ImportedProfile>();
        foreach (var container in containers.EnumerateArray())
        {
            CheckObject(container);
            var type = Text(container, "container");
            if (type is not ("amnezia-awg" or "amnezia-awg2" or "amnezia-awg-legacy")) continue;
            if (!container.TryGetProperty("awg", out var protocol) && !container.TryGetProperty("amnezia-awg", out protocol)) continue;
            CheckObject(protocol);
            if (!protocol.TryGetProperty("last_config", out var last)) continue;
            if (last.ValueKind == JsonValueKind.Null) continue;
            JsonDocument? nested = null;
            try
            {
                if (last.ValueKind == JsonValueKind.String)
                {
                    var value = last.GetString()!;
                    if (AmneziaWgConfiguration.LooksLikeConfiguration(value)) { Add(ResolveDnsTemplates(value, root), Text(protocol, "protocol_version")); continue; }
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    nested = JsonDocument.Parse(value);
                    last = nested.RootElement;
                }
                CheckObject(last);
                var config = Text(last, "config");
                Add(string.IsNullOrWhiteSpace(config) ? BuildConfiguration(last, root) : ResolveDnsTemplates(config, root), Text(protocol, "protocol_version"));
            }
            finally { nested?.Dispose(); }
        }
        if (result.Count == 0) throw MissingClient();
        return result;

        void Add(string content, string declaredVersion)
        {
            var config = AmneziaWgConfiguration.Parse(content);
            var name = Text(root, "description");
            if (string.IsNullOrWhiteSpace(name)) name = $"AmneziaWG · {config.EndpointHost}";
            name = new string(name.Where(c => !char.IsControl(c)).Take(120).ToArray()).Trim();
            result.Add(new ImportedProfile(name, AmneziaWgConfiguration.ProfileKind, content) { ProtocolVersion = NormalizeVersion(declaredVersion) });
        }
    }

    internal static string? NormalizeVersion(string? version) => version switch
    {
        "1" or "1.0" => "1.0", "1.5" => "1.5", "2" or "2.0" => "2.0",
        "3" or "3.0" => "3.0", "3.1" => "3.1", _ => null
    };

    private static string ResolveDnsTemplates(string content, JsonElement root)
    {
        // Amnezia exports native configs before its DNS placeholders are expanded.
        // Resolve only whole DNS field entries; packet templates and keys are untouched.
        return System.Text.RegularExpressions.Regex.Replace(content, @"(?im)^([ \t]*DNS[ \t]*=[ \t]*)([^\r\n]*)$", match =>
        {
            var values = match.Groups[2].Value.Split(',').Select(value => value.Trim() switch
            {
                "$PRIMARY_DNS" => Text(root, "dns1") is { Length: > 0 } primary ? primary : "1.1.1.1",
                "$SECONDARY_DNS" => Text(root, "dns2") is { Length: > 0 } secondary ? secondary : "1.0.0.1",
                _ => value.Trim()
            });
            return match.Groups[1].Value + string.Join(", ", values);
        });
    }

    private static string BuildConfiguration(JsonElement client, JsonElement root)
    {
        var text = new StringBuilder("[Interface]\n");
        void Field(string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (value.Any(char.IsControl)) throw Invalid();
            text.Append(name).Append(" = ").Append(value).Append('\n');
        }
        Field("PrivateKey", Required(client, "client_priv_key"));
        var addresses = Required(client, "client_ip").Split(',', StringSplitOptions.TrimEntries);
        Field("Address", string.Join(", ", addresses.Select(address => address.Contains('/') ? address
            : IPAddress.TryParse(address, out var ip) ? address + (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128") : throw Invalid())));
        var dns = new[] { Text(root, "dns1"), Text(root, "dns2") }.Where(value => value.Length > 0);
        Field("DNS", string.Join(", ", dns));
        Field("MTU", Text(client, "mtu"));
        foreach (var field in new[] { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5", "HeaderProtectionKey", "ContentPaddingAddition", "RekeyAfterTime", "RekeyTimeout", "RejectAfterTime", "KeepaliveTimeout", "MaxHandshakeAttempts", "RandomTrailers", "DisableCookies" })
            Field(field, Text(client, field));
        text.Append("[Peer]\n");
        Field("PublicKey", Required(client, "server_pub_key"));
        Field("PresharedKey", Text(client, "psk_key"));
        var host = Required(client, "hostName");
        if (host.Contains(':') && !host.StartsWith('[')) host = "[" + host + "]";
        Field("Endpoint", host + ":" + Required(client, "port"));
        if (!client.TryGetProperty("allowed_ips", out var allowed)) throw MissingClient();
        var prefixes = allowed.ValueKind == JsonValueKind.Array ? string.Join(", ", allowed.EnumerateArray().Select(value => value.GetString())) : allowed.GetString()!;
        Field("AllowedIPs", prefixes);
        Field("PersistentKeepalive", Text(client, "persistent_keep_alive"));
        return text.ToString();
    }

    private static string Required(JsonElement value, string key) => Text(value, key) is { Length: > 0 } text ? text : throw MissingClient();
    private static string Text(JsonElement value, string key) => !value.TryGetProperty(key, out var field) ? "" : field.ValueKind switch
    {
        JsonValueKind.String => field.GetString()!, JsonValueKind.Number => field.GetRawText(),
        JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Null => "", _ => throw Invalid()
    };
    private static void CheckObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) if (!keys.Add(field.Name)) throw Invalid();
    }
}
