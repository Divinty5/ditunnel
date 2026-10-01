using System.Collections;
using System.Runtime.InteropServices;

namespace DiTunnel.Platform.Windows.Network;

/// <summary>Calls the same Windows DNS CIM provider as DnsClient cmdlets, without PowerShell.</summary>
internal static class WindowsDnsPolicy
{
    internal sealed record MethodResult(IReadOnlyList<object> Items);
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

    private static List<(string Name, string Comment)> Rules()
    {
        var output = Invoke(RuleClass, "Get");
        var result = new List<(string, string)>();
        foreach (dynamic rule in output.Items)
            result.Add((Convert.ToString(rule.Properties_.Item("Name").Value)!,
                Convert.ToString(rule.Properties_.Item("Comment").Value) ?? ""));
        return result;
    }

    internal static void CleanupOwned()
    {
        foreach (var rule in Rules().Where(r => r.Comment == Comment))
            Invoke(RuleClass, "Remove", new Dictionary<string, object> { ["Name"] = rule.Name, ["Force"] = true });
        Flush();
    }

    internal static void Precheck()
    {
        CleanupOwned();
        if (HasForeignPolicy())
            throw new InvalidOperationException("Обнаружены существующие правила DNS. Подключение отменено, чтобы не изменять их.");
    }

    internal static bool HasForeignPolicy()
    {
        var output = Invoke("PS_DnsClientNrptPolicy", "Get", new Dictionary<string, object> { ["Effective"] = true });
        return Rules().Any(r => r.Comment != Comment) || output.Items.Count != 0;
    }

    internal static void Install(string[] servers)
    {
        Invoke(RuleClass, "Add", new Dictionary<string, object>
        {
            ["Namespace"] = new[] { "." }, ["NameServers"] = servers, ["Comment"] = Comment,
            ["PassThru"] = true
        });
        // Never treat a missing mutation response as proof that the policy was installed.
        if (!Rules().Any(r => r.Comment == Comment))
            throw new InvalidOperationException("Windows не подтвердила установку DNS-правила Di-Tunnel.");
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
