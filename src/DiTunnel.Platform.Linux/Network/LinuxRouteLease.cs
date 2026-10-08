using System.Text.Json.Nodes;

namespace DiTunnel.Platform.Linux.Network;

// The table, priorities and protocol are reserved only after a conflict check.
// No flush/replace, no saved global default route, and no changes to other links' DNS.
public sealed class LinuxRouteLease(ILinuxNetworkCommands commands, string interfaceName, string owner)
{
    public const uint Mark = 51820;
    public const uint DirectMark = 51821;
    private const string MarkSelector = "51820/4294967294";
    public const string Table = "51820";
    public const string Protocol = "198";
    private static readonly string[] Families = ["-4", "-6"];
    public int InterfaceIndex { get; private set; }

    public async Task PreflightAsync(CancellationToken token)
    {
        if (!Guid.TryParseExact(owner, "N", out _) || interfaceName != "dtn" + owner[..10]) throw new ArgumentException();
        var links = await LinksAsync(token).ConfigureAwait(false);
        if (links.Any(link => (string?)link?["ifname"] == interfaceName)) throw new InvalidOperationException("Имя TUN уже занято.");
        foreach (var family in Families)
        {
            var rules = await RulesAsync(family, token).ConfigureAwait(false);
            if (rules.Any(rule => (int?)rule?["priority"] is > 0 and <= 101 || Text(rule?["table"]) == Table))
                throw new InvalidOperationException("Обнаружены несовместимые policy routes. Сеть не изменена.");
            // 'table all' avoids ip's ENOENT error for a never-used table.
            if ((await RoutesAsync(family, token).ConfigureAwait(false)).Any(route => Text(route?["table"]) == Table))
                throw new InvalidOperationException("Таблица маршрутов Di-Tunnel уже занята.");
        }
    }

    public async Task ClaimLinkAsync(CancellationToken token)
    {
        var link = (await LinksAsync(token).ConfigureAwait(false)).SingleOrDefault(link => (string?)link?["ifname"] == interfaceName)
            ?? throw new InvalidOperationException("TUN ещё не готов.");
        if ((string?)link["linkinfo"]?["info_kind"] != "tun") throw new InvalidOperationException("Ожидался TUN интерфейс.");
        InterfaceIndex = (int)link["ifindex"]!;
        await commands.IpAsync(token, "link", "set", "dev", interfaceName, "alias", "ditunnel:" + owner).ConfigureAwait(false);
        await commands.IpAsync(token, "-4", "address", "add", "198.18.0.1/32", "dev", interfaceName).ConfigureAwait(false);
        await commands.IpAsync(token, "-6", "address", "add", "fd71:6469:7475::1/128", "dev", interfaceName, "nodad").ConfigureAwait(false);
    }

    public async Task ActivateAsync(IReadOnlyList<string> dns, CancellationToken token)
    {
        await VerifyLinkAsync(token).ConfigureAwait(false);
        foreach (var family in Families)
        {
            await commands.IpAsync(token, family, "route", "add", "default", "dev", interfaceName, "table", Table, "proto", Protocol).ConfigureAwait(false);
            await commands.IpAsync(token, family, "rule", "add", "priority", "100", "fwmark", MarkSelector, "lookup", "main", "protocol", Protocol).ConfigureAwait(false);
        }
        // Both routes and bypass rules are ready before application traffic is redirected.
        foreach (var family in Families)
            await commands.IpAsync(token, family, "rule", "add", "priority", "101", "not", "fwmark", MarkSelector, "lookup", Table, "protocol", Protocol).ConfigureAwait(false);
        // Redirect traffic before advertising VPN DNS, so its first query already uses TUN.
        await commands.ResolvedAsync(token, ["dns", interfaceName, .. dns]).ConfigureAwait(false);
        await commands.ResolvedAsync(token, "domain", interfaceName, "~.").ConfigureAwait(false);
        await commands.ResolvedAsync(token, "default-route", interfaceName, "yes").ConfigureAwait(false);
    }

    public async Task RollbackAsync(CancellationToken token)
    {
        var failures = new List<Exception>();
        async Task Attempt(Func<Task> action) { try { await action().ConfigureAwait(false); } catch (Exception ex) { failures.Add(ex); } }
        foreach (var family in Families)
        {
            await Attempt(async () =>
            {
                foreach (var rule in (await RulesAsync(family, token).ConfigureAwait(false)).Where(IsOwnedRule).OrderByDescending(rule => (int)rule!["priority"]!))
                {
                    var priority = (int)rule!["priority"]!;
                    await commands.IpAsync(token, [family, "rule", "del", "priority", priority.ToString(),
                        .. (priority == 101 ? new[] { "not" } : Array.Empty<string>()),
                        "fwmark", Text(rule["fwmask"]) is "0xfffffffe" or "4294967294" ? MarkSelector : Mark.ToString(), "lookup", priority == 101 ? Table : "main", "protocol", Protocol]).ConfigureAwait(false);
                }
            });
            await Attempt(async () =>
            {
                var link = (await LinksAsync(token).ConfigureAwait(false)).SingleOrDefault(link => (string?)link?["ifname"] == interfaceName);
                if (link is not null && (string?)link["ifalias"] != "ditunnel:" + owner)
                    throw new InvalidOperationException("Владелец интерфейса изменился.");
                foreach (var route in (await RoutesAsync(family, token).ConfigureAwait(false)).Where(route => Text(route?["table"]) == Table
                    && Text(route?["protocol"]) == Protocol && (string?)route?["dev"] == interfaceName && (string?)route?["dst"] == "default"))
                    await commands.IpAsync(token, family, "route", "del", "default", "dev", interfaceName, "table", Table, "proto", Protocol).ConfigureAwait(false);
            });
        }
        await Attempt(async () =>
        {
            var link = (await LinksAsync(token).ConfigureAwait(false)).SingleOrDefault(link => (string?)link?["ifname"] == interfaceName);
            if (link is null) return; // Closing a nonpersistent TUN also removes its per-link resolved state.
            if ((string?)link["ifalias"] != "ditunnel:" + owner) throw new InvalidOperationException("Владелец интерфейса изменился.");
            await commands.ResolvedAsync(token, "revert", interfaceName).ConfigureAwait(false);
        });
        if (failures.Count > 0) throw new AggregateException("Очистка сети не завершена; журнал сохранён для повторной попытки.", failures);
    }

    public async Task RemoveOwnedLinkAsync(CancellationToken token)
    {
        var link = (await LinksAsync(token).ConfigureAwait(false)).SingleOrDefault(link => (string?)link?["ifname"] == interfaceName);
        if (link is null) return;
        if ((string?)link["ifalias"] != "ditunnel:" + owner || (string?)link["linkinfo"]?["info_kind"] != "tun")
            throw new InvalidOperationException("Владелец интерфейса изменился.");
        await commands.IpAsync(token, "link", "del", "dev", interfaceName).ConfigureAwait(false);
    }

    private static bool IsOwnedRule(JsonNode? rule)
    {
        var priority = (int?)rule?["priority"];
        var mark = Text(rule?["fwmark"]);
        var table = Text(rule?["table"]);
        if (rule is not JsonObject values || values.Any(pair => pair.Key is not ("priority" or "src" or "fwmark" or "fwmask" or "table" or "protocol" or "not"))
            || Text(rule["src"]) is not ("" or "all" or "0.0.0.0/0" or "::/0")) return false;
        // iproute2 emits the presence-only 'not' flag as JSON null, not boolean true.
        var inverted = values.ContainsKey("not") && (rule["not"] is null || (bool?)rule["not"] == true);
        return Text(rule?["protocol"]) == Protocol && mark is "0xca6c" or "51820"
            && (Text(rule?["fwmask"]) is "" or "0xffffffff" or "4294967295" or "0xfffffffe" or "4294967294")
            && (priority == 100 && table is "main" or "254" && !inverted
                || priority == 101 && table == Table && inverted);
    }
    private async Task VerifyLinkAsync(CancellationToken token)
    {
        if (!(await LinksAsync(token).ConfigureAwait(false)).Any(link => (string?)link?["ifname"] == interfaceName
            && (int?)link?["ifindex"] == InterfaceIndex && (string?)link?["ifalias"] == "ditunnel:" + owner))
            throw new InvalidOperationException("TUN интерфейс изменился.");
    }
    private async Task<JsonArray> LinksAsync(CancellationToken token) => Parse(await commands.IpAsync(token, "-j", "-d", "link", "show").ConfigureAwait(false));
    private async Task<JsonArray> RulesAsync(string family, CancellationToken token) => Parse(await commands.IpAsync(token, family, "-j", "rule", "show").ConfigureAwait(false));
    private async Task<JsonArray> RoutesAsync(string family, CancellationToken token) => Parse(await commands.IpAsync(token, family, "-j", "route", "show", "table", "all").ConfigureAwait(false));
    private static JsonArray Parse(string json) => JsonNode.Parse(json)?.AsArray() ?? throw new InvalidOperationException();
    private static string Text(JsonNode? node) => node?.ToString() ?? "";
}
