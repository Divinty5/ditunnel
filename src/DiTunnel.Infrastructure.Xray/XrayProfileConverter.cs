using System.Text.Json.Nodes;
using DiTunnel.Core.Profiles;
using DiTunnel.Core.Connection;

namespace DiTunnel.Infrastructure.Xray;

public sealed record XrayProfileConfiguration(string ServerHost, JsonObject Outbound)
{
    public string Build(string serverAddress, bool tun, int proxyPort = 18080, SplitTunnelPolicy? splitTunnel = null, string? tunnelName = null)
    {
        var outbound = (JsonObject)Outbound.DeepClone();
        var settings = outbound["settings"]!.AsObject();
        if (outbound["protocol"]!.GetValue<string>() == "hysteria") settings["address"] = serverAddress;
        else if (settings["vnext"] is JsonArray vnext) vnext[0]!["address"] = serverAddress;
        else settings["servers"]![0]!["address"] = serverAddress;
        splitTunnel ??= SplitTunnelPolicy.Default;
        var inbound = tun
            ? new JsonObject { ["protocol"] = "tun", ["tag"] = "tun", ["port"] = 0, ["settings"] = new JsonObject { ["name"] = tunnelName ?? "DiTunnel", ["MTU"] = 1400, ["autoOutboundsInterface"] = "auto" }, ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic"), ["routeOnly"] = true } }
            : new JsonObject { ["protocol"] = "socks", ["listen"] = "127.0.0.1", ["port"] = proxyPort, ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true } };
        var outbounds = new JsonArray();
        if (splitTunnel.Mode == SplitTunnelMode.ProxySelected) outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });
        outbounds.Add(outbound);
        if (splitTunnel.Mode == SplitTunnelMode.BypassSelected) outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });
        var rules = new JsonArray();
        var domains = splitTunnel.Domains.Where(d => !string.IsNullOrWhiteSpace(d)).Select(SplitTunnelPolicy.NormalizeDomain).Where(d => d.Length > 0).Select(d => (JsonNode?)$"domain:{d}").ToArray();
        var processes = splitTunnel.Processes.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => (JsonNode?)p.Trim()).ToArray();
        var selectedTag = splitTunnel.Mode == SplitTunnelMode.BypassSelected ? "direct" : "proxy";
        if (domains.Length > 0) rules.Add(new JsonObject { ["domain"] = new JsonArray(domains), ["outboundTag"] = selectedTag });
        if (processes.Length > 0) rules.Add(new JsonObject { ["process"] = new JsonArray(processes), ["outboundTag"] = selectedTag });
        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = tun ? "info" : "none" },
            ["inbounds"] = new JsonArray(inbound),
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject { ["domainStrategy"] = "AsIs", ["rules"] = rules }
        }.ToJsonString();
    }
}

public static class XrayProfileConverter
{
    public static XrayProfileConfiguration Convert(ImportedProfile profile)
    {
        if (!Uri.TryCreate(profile.Content, UriKind.Absolute, out var uri) || uri.Scheme is not ("hy2" or "hysteria2" or "vless" or "trojan" or "ss"))
            throw new NotSupportedException("Подключение доступно для Hysteria 2, VLESS, Trojan и Shadowsocks. VMess и произвольный JSON пока доступны только для хранения.");
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (!query.TryAdd(Uri.UnescapeDataString(pair[0]), pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : ""))
                throw new FormatException("Повторяющийся параметр ссылки.");
        }
        string Get(string key, string fallback = "") => query.GetValueOrDefault(key, fallback);
        if (Get("insecure") is "1" or "true" || Get("allowInsecure") is "1" or "true")
            throw new NotSupportedException("Профиль отключает проверку TLS. Для подключения используйте сертификат сервера с корректным SNI.");
        if (Get("obfs") != "" || Get("mport") != "" || Get("plugin") != "")
            throw new NotSupportedException("Obfs, смена портов и плагины пока не поддерживаются.");
        var scheme = uri.Scheme;
        var hy2 = scheme is "hy2" or "hysteria2";
        var stream = new JsonObject();
        var outbound = new JsonObject { ["tag"] = "proxy", ["protocol"] = hy2 ? "hysteria" : scheme == "ss" ? "shadowsocks" : scheme };
        var credential = Uri.UnescapeDataString(uri.UserInfo);
        if (hy2)
        {
            outbound["settings"] = new JsonObject { ["version"] = 2, ["address"] = uri.Host, ["port"] = uri.Port };
            stream["network"] = "hysteria";
            stream["hysteriaSettings"] = new JsonObject { ["version"] = 2, ["auth"] = credential };
        }
        else if (scheme == "vless")
        {
            outbound["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = uri.Host, ["port"] = uri.Port,
                ["users"] = new JsonArray(new JsonObject { ["id"] = credential, ["encryption"] = Get("encryption", "none"), ["flow"] = Get("flow") }) }) };
        }
        else
        {
            var server = new JsonObject { ["address"] = uri.Host, ["port"] = uri.Port };
            if (scheme == "ss")
            {
                if (!credential.Contains(':'))
                {
                    var encoded = credential.Replace('-', '+').Replace('_', '/');
                    credential = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
                }
                var split = credential.Split(':', 2);
                if (split.Length != 2) throw new FormatException("Некорректный ключ Shadowsocks.");
                server["method"] = split[0]; server["password"] = split[1];
            }
            else server["password"] = credential;
            outbound["settings"] = new JsonObject { ["servers"] = new JsonArray(server) };
        }
        var security = hy2 || scheme == "trojan" ? Get("security", "tls") : Get("security", "none");
        if (security is not ("tls" or "none")) throw new NotSupportedException("В этом прототипе доступны TLS и обычный транспорт; REALITY ещё не подключён.");
        if (hy2 && security != "tls") throw new NotSupportedException("Hysteria 2 требует TLS.");
        stream["security"] = security;
        if (security == "tls")
        {
            var serverName = Get("sni");
            // Some HY2 links use the protocol label as a placeholder. An IP SAN must be checked against the actual IP.
            if (string.IsNullOrWhiteSpace(serverName) || (hy2 && serverName == "hysteria" && System.Net.IPAddress.TryParse(uri.Host, out _)))
                serverName = uri.Host;
            var tls = new JsonObject { ["serverName"] = serverName, ["allowInsecure"] = false };
            if (Get("alpn") != "") tls["alpn"] = new JsonArray(Get("alpn").Split(',').Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            stream["tlsSettings"] = tls;
        }
        if (!hy2)
        {
            var transport = Get("type", "tcp");
            if (transport is not ("tcp" or "raw" or "ws" or "httpupgrade")) throw new NotSupportedException("Транспорт этого профиля ещё не поддерживается.");
            stream["network"] = transport;
            if (transport is "ws" or "httpupgrade") stream[transport == "ws" ? "wsSettings" : "httpupgradeSettings"] = new JsonObject { ["path"] = Get("path", "/"), ["host"] = Get("host") };
        }
        outbound["streamSettings"] = stream;
        return new(uri.Host, outbound);
    }
}
