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
    public static IReadOnlyList<ImportedProfile> Parse(string input) => Parse(input, out _);

    public static IReadOnlyList<ImportedProfile> Parse(string input, out int skippedEntries)
    {
        if (Encoding.UTF8.GetByteCount(input) > MaximumBytes) throw new FormatException("Не удалось импортировать: конфигурация превышает 2 МБ.");
        input = input.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(input)) throw new FormatException("Не удалось импортировать: данные отсутствуют.");
        skippedEntries = 0;
        if (input.StartsWith('{')) return ParseJson(input);

        if (!input.Contains("://", StringComparison.Ordinal) && input.Contains('%'))
        {
            try { var decoded = Uri.UnescapeDataString(input); if (decoded.Contains("://", StringComparison.Ordinal)) input = decoded; }
            catch { }
        }
        if (!input.Contains("://", StringComparison.Ordinal))
        {
            try { input = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(string.Concat(input.Where(c => !char.IsWhiteSpace(c)))))); }
            catch (FormatException) { throw new FormatException("Не удалось импортировать: формат подписки не распознан."); }
        }

        var result = new List<ImportedProfile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = SplitServerEntries(input);
        foreach (var token in tokens)
        {
            if (result.Count >= 1000) break;
            if (!LooksLikeServerUri(token)) { skippedEntries++; continue; }
            if (!seen.Add(token)) continue;
            try { result.Add(ParseServerUri(token)); }
            catch (FormatException) { skippedEntries++; }
        }
        if (result.Count == 0) throw new FormatException("Не удалось импортировать: поддерживаемые серверы не найдены.");
        return result;
    }

    private static IEnumerable<string> SplitServerEntries(string input)
    {
        foreach (var rawLine in input.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.StartsWith('#') || rawLine.StartsWith("//", StringComparison.Ordinal)) continue;
            var starts = System.Text.RegularExpressions.Regex.Matches(rawLine,
                @"(?i)(?<![\p{L}\p{N}+.-])(?:vless|vmess|trojan|ss|hysteria2|hy2)://");
            if (starts.Count == 0) { yield return rawLine; continue; }
            for (var index = 0; index < starts.Count; index++)
            {
                var start = starts[index].Index;
                var end = index + 1 < starts.Count ? starts[index + 1].Index : rawLine.Length;
                var entry = rawLine[start..end].Trim();
                if (entry.Length > 0) yield return entry;
            }
        }
    }

    private static IReadOnlyList<ImportedProfile> ParseJson(string input)
    {
        try
        {
            using var json = JsonDocument.Parse(input);
            if (!json.RootElement.TryGetProperty("outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array || outbounds.GetArrayLength() == 0)
                throw new FormatException("Не удалось импортировать: JSON должен содержать непустой массив outbounds Xray.");
            return [new("Конфигурация Xray", "Xray JSON", input)];
        }
        catch (JsonException) { throw new FormatException("Не удалось импортировать: некорректный JSON."); }
    }

    private static bool LooksLikeServerUri(string value)
    {
        var separator = value.IndexOf("://", StringComparison.Ordinal);
        return separator is > 0 and <= 12 && value[..separator].ToLowerInvariant() is "vless" or "vmess" or "trojan" or "ss" or "hysteria2" or "hy2";
    }

    private static ImportedProfile ParseServerUri(string line)
    {
        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) throw new FormatException();
        var kind = uri.Scheme.ToLowerInvariant();
        string name;
        if (kind == "vmess")
        {
            try
            {
                using var vmess = JsonDocument.Parse(Convert.FromBase64String(PadBase64(line[8..].Split('#')[0])));
                if (!vmess.RootElement.TryGetProperty("add", out var address) || string.IsNullOrWhiteSpace(address.GetString()) ||
                    !vmess.RootElement.TryGetProperty("id", out var id) || !Guid.TryParse(id.GetString(), out _) ||
                    !vmess.RootElement.TryGetProperty("port", out var port) || !int.TryParse(port.ToString(), out var p) || p is < 1 or > 65535) throw new FormatException();
                name = vmess.RootElement.TryGetProperty("ps", out var ps) ? ps.GetString() ?? "VMess" : "VMess";
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { throw new FormatException(); }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535 || string.IsNullOrEmpty(uri.UserInfo)) throw new FormatException();
            if (kind == "vless" && !Guid.TryParse(uri.UserInfo, out _)) throw new FormatException();
            name = string.IsNullOrEmpty(uri.Fragment) ? kind.ToUpperInvariant() : Uri.UnescapeDataString(uri.Fragment[1..].Replace('+', ' '));
        }
        name = NormalizeProfileName(name);
        return new(string.IsNullOrWhiteSpace(name) ? kind.ToUpperInvariant() : name, kind is "hy2" or "hysteria2" ? "Hysteria 2" : kind.ToUpperInvariant(), line);
    }

    private static string NormalizeProfileName(string name)
    {
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*[|｜]\s*(?:(?:📊|📈)\s*)?\d+(?:[.,]\d+)?\s*(?:GB|ГБ)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return new string(name.Take(120).ToArray()).Trim();
    }

    private static string PadBase64(string text) => text.Replace('-', '+').Replace('_', '/').PadRight((text.Length + 3) / 4 * 4, '=');
}

public sealed class ProfileImporter
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    public int SkippedEntries { get; private set; }

    public async Task<IReadOnlyList<ImportedProfile>> ImportAsync(string input, CancellationToken cancellationToken = default)
    {
        SkippedEntries = 0;
        input = input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            var parsed = ProfileParser.Parse(input, out var skipped);
            SkippedEntries = skipped;
            return parsed;
        }
        if (uri.Scheme != "https") throw new FormatException("Не удалось импортировать: для подписки нужна ссылка HTTPS.");
        using var response = await GetFollowingRedirectsAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ProfileParser.MaximumBytes) throw new FormatException("Не удалось импортировать: подписка превышает 2 МБ.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, deadline.Token)) != 0)
        {
            if (buffer.Length + read > ProfileParser.MaximumBytes) throw new FormatException("Не удалось импортировать: подписка превышает 2 МБ.");
            buffer.Write(block, 0, read);
        }
        var usage = response.Headers.TryGetValues("Subscription-Userinfo", out var headers) ? SubscriptionUsage.Parse(string.Join(";", headers)) : null;
        var profiles = ProfileParser.Parse(Encoding.UTF8.GetString(buffer.ToArray()), out var skippedEntries);
        SkippedEntries = skippedEntries;
        return profiles.Select(p => p with { Usage = usage }).ToArray();
    }

    private static async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri initial, CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            var response = await Client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new FormatException("Не удалось импортировать: перенаправление не содержит адреса.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (current.Scheme != "https") throw new FormatException("Не удалось импортировать: перенаправление ведёт на небезопасный адрес.");
        }
        throw new FormatException("Не удалось импортировать: слишком много перенаправлений.");
    }
}
