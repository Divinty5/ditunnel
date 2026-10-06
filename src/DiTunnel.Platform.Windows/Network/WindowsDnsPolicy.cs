using System.Collections;
using System.Runtime.InteropServices;

namespace DiTunnel.Platform.Windows.Network;

internal sealed class DnsPolicyException(string code) : InvalidOperationException("Windows не подтвердила установку DNS-правила Di-Tunnel.")
{
    internal string Code { get; } = code;
}

/// <summary>Calls the same Windows DNS CIM provider as DnsClient cmdlets, without PowerShell.</summary>
internal static class WindowsDnsPolicy
{
    internal sealed record MethodResult(IReadOnlyList<object> Items);
    internal delegate MethodResult InvokeMethod(string className, string method, IReadOnlyDictionary<string, object>? values = null);
    private sealed record Rule(string Name, string Comment, string[] Namespaces);
    internal const string Comment = "DiTunnel managed DNS v1";
    private const string RuleClass = "PS_DnsClientNrptRule";

    internal static MethodResult Invoke(string className, string method, IReadOnlyDictionary<string, object>? values = null)
    {
        dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!)!;
        dynamic services = locator.ConnectServer(".", @"root\Microsoft\Windows\DNS");
        dynamic definition = services.Get(className);
        dynamic input = definition.Methods_.Item(method).InParameters.SpawnInstance_();
        if (values is not null)
            foreach (var pair in values) input.Properties_.Item(pair.Key).Value = pair.Value;
        object? output = services.ExecMethod(className, method, input);
        return ReadOutput(output);
    }

    internal static MethodResult ReadOutput(object? response)
    {
        // DnsClient's provider can successfully return no output when Get finds no rules,
        // or when a mutation has PassThru disabled. COM failures still throw in ExecMethod.
        if (response is null) return new([]);
        dynamic output = response;
        object? returnValue = output.Properties_.Item("ReturnValue")?.Value;
        if (returnValue is not null and not DBNull)
        {
            int code = Convert.ToInt32(returnValue);
            if (code != 0) throw new System.ComponentModel.Win32Exception(code);
        }
        object? items = output.Properties_.Item("cmdletOutput")?.Value;
        return new(items is IEnumerable list ? list.Cast<object>().ToArray()
            : items is null or DBNull ? [] : [items]);
    }

    private static string[] Strings(object? value) => value switch
    {
        null or DBNull => [],
        string text => [text],
        IEnumerable list => list.Cast<object?>().Select(item => item as string ?? "").ToArray(),
        _ => [""]
    };

    private static object? Property(dynamic row, string name) => row.Properties_.Item(name)?.Value;

    private static List<Rule> Rules(InvokeMethod invoke)
    {
        var output = invoke(RuleClass, "Get");
        var result = new List<Rule>();
        foreach (dynamic rule in output.Items)
            result.Add(new(Convert.ToString(Property(rule, "Name"))!,
                Convert.ToString(Property(rule, "Comment")) ?? "", Strings(Property(rule, "Namespace"))));
        return result;
    }

    internal static void CleanupOwned() => CleanupOwned(Invoke, Flush);

    internal static void CleanupOwned(InvokeMethod invoke, Action flush)
    {
        foreach (var rule in Rules(invoke).Where(r => r.Comment == Comment))
            invoke(RuleClass, "Remove", new Dictionary<string, object> { ["Name"] = rule.Name, ["Force"] = true });
        flush();
    }

    internal static void Precheck() => Precheck(Invoke, Flush);

    internal static void Precheck(InvokeMethod invoke, Action flush)
    {
        CleanupOwned(invoke, flush);
        if (HasConflictingPolicy(invoke))
            throw new InvalidOperationException("Обнаружены существующие правила DNS. Подключение отменено, чтобы не изменять их.");
    }

    // More specific host/suffix rules take precedence over our default namespace (".").
    // They must remain untouched. A second default rule can disable both policies;
    // unknown/empty namespaces cannot be proved compatible either.
    private static bool ConflictsWithDefault(string[] namespaces) => namespaces.Length == 0 ||
        namespaces.Any(name => string.IsNullOrWhiteSpace(name) || name.Trim().Trim('.').Length == 0 || name.Trim() == "*");

    internal static bool HasConflictingPolicy() => HasConflictingPolicy(Invoke);

    private static bool HasConflictingPolicy(InvokeMethod invoke)
    {
        var output = invoke("PS_DnsClientNrptPolicy", "Get", new Dictionary<string, object> { ["Effective"] = true });
        return Rules(invoke).Any(r => r.Comment != Comment && ConflictsWithDefault(r.Namespaces)) ||
            output.Items.Any(row => ConflictsWithDefault(Strings(Property(row, "Namespace"))));
    }

    internal static bool Install(string[] servers, Action? installInterface = null) => Install(servers, Invoke, Flush, installInterface);

    internal static bool Install(string[] servers, InvokeMethod invoke, Action? flush = null, Action? installInterface = null)
    {
        // Recheck immediately before the mutation in case another VPN installed a default rule.
        if (HasConflictingPolicy(invoke))
            throw new InvalidOperationException("Обнаружены существующие правила DNS. Подключение отменено, чтобы не изменять их.");
        invoke(RuleClass, "Add", new Dictionary<string, object>
        {
            ["Namespace"] = new[] { "." }, ["NameServers"] = servers, ["Comment"] = Comment,
            ["PassThru"] = true
        });
        flush?.Invoke();
        // A local rule may be ignored by domain Group Policy, including an empty policy store.
        // The fallback configures only our newly created tunnel adapter, never a policy store.
        var effective = invoke("PS_DnsClientNrptPolicy", "Get", new Dictionary<string, object> { ["Effective"] = true });
        var defaults = effective.Items.Where(row => Strings(Property(row, "Namespace")).Contains(".")).ToArray();
        var rules = Rules(invoke);
        if (!rules.Any(r => r.Comment == Comment && r.Namespaces.SequenceEqual(new[] { "." })))
            throw new DnsPolicyException("LOCAL_MISSING");
        if (rules.Any(r => r.Comment != Comment && ConflictsWithDefault(r.Namespaces)) || defaults.Length > 1)
            throw new DnsPolicyException("DEFAULT_CONFLICT");
        if (defaults.Length == 0)
        {
            if (installInterface is null || effective.Items.Any(row => ConflictsWithDefault(Strings(Property(row, "Namespace")))))
                throw new DnsPolicyException("EFFECTIVE_MISSING");
            CleanupOwned(invoke, flush ?? (() => { }));
            installInterface(); // Must verify the adapter's DNS settings before returning.
            return true;
        }
        if (!Strings(Property(defaults[0], "NameServers")).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(servers))
            throw new DnsPolicyException("SERVERS_MISMATCH");
        return false;
    }

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();

    internal static void Flush()
    {
        if (!DnsFlushResolverCache()) throw new InvalidOperationException("Не удалось обновить кэш DNS Windows.");
    }

    internal static IReadOnlyList<string> CachedNames()
    {
        dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!)!;
        dynamic services = locator.ConnectServer(".", @"root\StandardCimv2");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic row in services.ExecQuery("SELECT Entry, Name FROM MSFT_DNSClientCache"))
            foreach (var property in new[] { "Entry", "Name" })
            {
                string? name = Convert.ToString(row.Properties_.Item(property).Value);
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name.TrimEnd('.'));
            }
        return result.ToArray();
    }
}
