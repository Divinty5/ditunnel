using System.Text;
using System.Text.Json;

namespace DiTunnel.Core.Profiles;

public sealed record ImportedProfile(string Name, string Kind, string Content, string SourceId = "legacy", string SourceName = "Ранее импортированные", string? SourceUrl = null, SubscriptionUsage? Usage = null)
{
    public string Summary => Kind == "Xray JSON" ? "Конфигурация Xray" : $"{Kind} · конфигурация сервера";
}

public static class ProfileParser
{
    public const int MaximumBytes = 2 * 1024 * 1024;

    public static IReadOnlyList<ImportedProfile> Parse(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > MaximumBytes)
            throw new FormatException("Конфигурация превышает 2 МБ.");
        input = input.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(input)) throw new FormatException("Вставьте ссылку или конфигурацию.");
        if (input.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(input);
                if (!json.RootElement.TryGetProperty("outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array || outbounds.GetArrayLength() == 0)
                    throw new FormatException("JSON должен содержать непустой массив outbounds Xray.");
                return [new("Конфигурация Xray", "Xray JSON", input)];
            }
            catch (JsonException) { throw new FormatException("Некорректный JSON."); }
        }
        if (!input.Contains("://", StringComparison.Ordinal))
        {
            try { input = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(string.Concat(input.Where(c => !char.IsWhiteSpace(c)))))); }
            catch (FormatException) { throw new FormatException("Ожидается ссылка сервера, подписка Base64 или JSON Xray."); }
        }
        var result = new List<ImportedProfile>();
        foreach (var line in input.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct())
        {
            if (result.Count >= 1000) throw new FormatException("В одной подписке допускается не более 1000 серверов.");
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) throw new FormatException("Некорректная ссылка сервера.");
            var kind = uri.Scheme.ToLowerInvariant();
            if (kind is not ("vless" or "vmess" or "trojan" or "ss" or "hysteria2" or "hy2"))
                throw new FormatException("Поддерживаются VLESS, VMess, Trojan, Shadowsocks и Hysteria 2.");
            string name;
            if (kind == "vmess")
            {
                try
                {
                    using var vmess = JsonDocument.Parse(Convert.FromBase64String(PadBase64(line[8..].Split('#')[0])));
                    if (!vmess.RootElement.TryGetProperty("add", out var address) || string.IsNullOrWhiteSpace(address.GetString()) ||
                        !vmess.RootElement.TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(), out _) ||
                        !vmess.RootElement.TryGetProperty("port", out var port) || !int.TryParse(port.ToString(), out var p) || p is < 1 or > 65535)
                        throw new FormatException();
                    name = vmess.RootElement.TryGetProperty("ps", out var ps) ? ps.GetString() ?? "VMess" : "VMess";
                }
                catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
                { throw new FormatException("Некорректная конфигурация VMess."); }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535 || string.IsNullOrEmpty(uri.UserInfo))
                    throw new FormatException("В ссылке нужны адрес, порт и данные авторизации.");
                if (kind == "vless" && !Guid.TryParse(uri.UserInfo, out _)) throw new FormatException("Некорректный UUID VLESS.");
                name = string.IsNullOrEmpty(uri.Fragment) ? kind.ToUpperInvariant() : Uri.UnescapeDataString(uri.Fragment[1..]);
            }
            name = NormalizeProfileName(name);
            result.Add(new(string.IsNullOrWhiteSpace(name) ? kind.ToUpperInvariant() : name, kind is "hy2" or "hysteria2" ? "Hysteria 2" : kind.ToUpperInvariant(), line));
        }
        if (result.Count == 0) throw new FormatException("В подписке нет серверов.");
        return result;
    }

    private static string NormalizeProfileName(string name)
    {
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        // Some subscription generators append traffic quota decorations to every URI fragment.
        // Usage belongs to subscription metadata, not to the server's display name.
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*[|｜]\s*(?:(?:📊|📈)\s*)?\d+(?:[.,]\d+)?\s*(?:GB|ГБ)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return new string(name.Take(120).ToArray()).Trim();
    }

    private static string PadBase64(string text)
    {
        text = text.Replace('-', '+').Replace('_', '/');
        return text.PadRight((text.Length + 3) / 4 * 4, '=');
    }
}

public sealed class ProfileImporter
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };

    public async Task<IReadOnlyList<ImportedProfile>> ImportAsync(string input, CancellationToken cancellationToken = default)
    {
        input = input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return ProfileParser.Parse(input);
        if (uri.Scheme != "https") throw new FormatException("Для подписки нужна ссылка HTTPS.");
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400) throw new FormatException("Подписка перенаправляет запрос. Вставьте конечную HTTPS-ссылку.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ProfileParser.MaximumBytes) throw new FormatException("Подписка превышает 2 МБ.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, deadline.Token)) != 0)
        {
            if (buffer.Length + read > ProfileParser.MaximumBytes) throw new FormatException("Подписка превышает 2 МБ.");
            buffer.Write(block, 0, read);
        }
        var usage = response.Headers.TryGetValues("Subscription-Userinfo", out var headers) ? SubscriptionUsage.Parse(string.Join(";", headers)) : null;
        return ProfileParser.Parse(Encoding.UTF8.GetString(buffer.ToArray())).Select(p => p with { Usage = usage }).ToArray();
    }
}
