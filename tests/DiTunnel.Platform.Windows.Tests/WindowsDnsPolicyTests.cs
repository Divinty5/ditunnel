using System.ComponentModel;
using DiTunnel.Platform.Windows.Network;

namespace DiTunnel.Platform.Windows.Tests;

public sealed class WindowsDnsPolicyTests
{
    [Fact]
    public void NullResponseIsAnEmptyResult()
    {
        Assert.Empty(WindowsDnsPolicy.ReadOutput(null).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingStatusAndEmptyRowsAreAccepted(bool databaseNull)
    {
        var output = new ProviderOutput(new(new Dictionary<string, object?>
        {
            ["ReturnValue"] = databaseNull ? DBNull.Value : null,
            ["cmdletOutput"] = databaseNull ? DBNull.Value : null
        }));
        Assert.Empty(WindowsDnsPolicy.ReadOutput(output).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingPolicyRecordsArePreserved(bool array)
    {
        var rule = new object();
        var output = new ProviderOutput(new(new Dictionary<string, object?>
        {
            ["ReturnValue"] = 0u,
            ["cmdletOutput"] = array ? new[] { rule } : rule
        }));
        Assert.Same(rule, Assert.Single(WindowsDnsPolicy.ReadOutput(output).Items));
    }

    [Fact]
    public void ExplicitAccessDenialIsNotTreatedAsAnEmptyPolicy()
    {
        var output = new ProviderOutput(new(new Dictionary<string, object?> { ["ReturnValue"] = 5u }));
        var error = Assert.Throws<Win32Exception>(() => WindowsDnsPolicy.ReadOutput(output));
        Assert.Equal(5, error.NativeErrorCode);
    }

    [Theory]
    [InlineData("tfs.corp.example", false)]
    [InlineData("tfs.corp.example", true)]
    [InlineData(".corp.example", true)]
    [InlineData("112.168.192.in-addr.arpa", true)]
    public void ScopedCorporateRuleDoesNotBlockVpnAndSurvivesCleanup(string dnsNamespace, bool effective)
    {
        var corporate = Row("corporate", "", new[] { dnsNamespace }, ["192.0.2.53"]);
        var provider = new PolicyProvider();
        provider.Local.Add(corporate);
        if (effective) provider.Effective.Add(Row(null, null, dnsNamespace, ["192.0.2.53"]));

        WindowsDnsPolicy.Precheck(provider.Invoke, provider.Flush);
        WindowsDnsPolicy.Install(["1.1.1.1", "1.0.0.1"], provider.Invoke);
        Assert.Contains(corporate, provider.Local);
        Assert.Equal(2, provider.Local.Count);

        WindowsDnsPolicy.CleanupOwned(provider.Invoke, provider.Flush);
        Assert.Same(corporate, Assert.Single(provider.Local));
        Assert.Equal(new[] { "ditunnel" }, provider.Removed);
        Assert.Equal(2, provider.FlushCount);
    }

    [Theory]
    [InlineData(".", false)]
    [InlineData(".", true)]
    [InlineData(null, false)]
    [InlineData("", true)]
    [InlineData("*", false)]
    public void DefaultOrUnknownForeignRuleIsRefusedWithoutRemovingIt(string? dnsNamespace, bool effective)
    {
        var rule = Row("foreign", "", dnsNamespace, ["192.0.2.53"]);
        var provider = new PolicyProvider();
        (effective ? provider.Effective : provider.Local).Add(rule);

        var error = Assert.Throws<InvalidOperationException>(() => WindowsDnsPolicy.Precheck(provider.Invoke, provider.Flush));
        Assert.StartsWith("Обнаружены существующие правила DNS", error.Message);
        Assert.Empty(provider.Removed);
        Assert.Contains(rule, effective ? provider.Effective : provider.Local);
        Assert.Equal(0, provider.AddCount);
    }

    [Fact]
    public void DefaultAmongSeveralNamespacesIsAlsoRefused()
    {
        var provider = new PolicyProvider();
        provider.Local.Add(Row("foreign", "", new[] { ".corp.example", "." }, ["192.0.2.53"]));
        Assert.Throws<InvalidOperationException>(() => WindowsDnsPolicy.Precheck(provider.Invoke, provider.Flush));
        Assert.Empty(provider.Removed);
    }

    [Fact]
    public void StaleOwnedDefaultIsCleanedBeforeCheckingCorporateRules()
    {
        var provider = new PolicyProvider();
        provider.Local.Add(Row("ditunnel", WindowsDnsPolicy.Comment, new[] { "." }, ["1.1.1.1"]));
        provider.Effective.Add(Row(null, null, ".", ["1.1.1.1"]));
        provider.Local.Add(Row("corporate", "", new[] { ".corp.example" }, ["192.0.2.53"]));

        WindowsDnsPolicy.Precheck(provider.Invoke, provider.Flush);
        Assert.Equal(new[] { "ditunnel" }, provider.Removed);
        Assert.Single(provider.Local);
    }

    [Fact]
    public void NewForeignDefaultBetweenPrecheckAndInstallIsRefused()
    {
        var provider = new PolicyProvider();
        WindowsDnsPolicy.Precheck(provider.Invoke, provider.Flush);
        provider.Local.Add(Row("another-vpn", "", new[] { "." }, ["192.0.2.53"]));

        Assert.Throws<InvalidOperationException>(() => WindowsDnsPolicy.Install(["1.1.1.1"], provider.Invoke));
        Assert.Equal(0, provider.AddCount);
        Assert.Empty(provider.Removed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void IneffectiveOrWrongEffectiveDefaultIsNotReportedAsInstalled(bool applyDefault, bool wrongServers)
    {
        var provider = new PolicyProvider { ApplyDefault = applyDefault, WrongServers = wrongServers };
        var corporate = Row("corporate", "", new[] { ".corp.example" }, ["192.0.2.53"]);
        provider.Local.Add(corporate);
        provider.Effective.Add(Row(null, null, ".corp.example", ["192.0.2.53"]));

        var error = Assert.Throws<DnsPolicyException>(() => WindowsDnsPolicy.Install(["1.1.1.1"], provider.Invoke));
        Assert.Equal("Windows не подтвердила установку DNS-правила Di-Tunnel.", error.Message);
        Assert.Equal(applyDefault ? "SERVERS_MISMATCH" : "EFFECTIVE_MISSING", error.Code);
        // The tunnel host calls CleanupDns in its finally block after an install failure.
        WindowsDnsPolicy.CleanupOwned(provider.Invoke, provider.Flush);
        Assert.Same(corporate, Assert.Single(provider.Local));
        Assert.Equal(new[] { "ditunnel" }, provider.Removed);
    }

    [Fact]
    public void ProviderAccessErrorStillCancelsPrecheck()
    {
        WindowsDnsPolicy.MethodResult Denied(string _, string __, IReadOnlyDictionary<string, object>? ___) =>
            throw new Win32Exception(5);
        var error = Assert.Throws<Win32Exception>(() => WindowsDnsPolicy.Precheck(Denied, () => { }));
        Assert.Equal(5, error.NativeErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IgnoredLocalDefaultUsesAdapterWithoutDeletingCorporateRules(bool effectiveCorporateRule)
    {
        var provider = new PolicyProvider { ApplyDefault = false };
        var corporate = Row("corporate", "", new[] { ".corp.example" }, ["192.0.2.53"]);
        provider.Local.Add(corporate);
        if (effectiveCorporateRule) provider.Effective.Add(Row(null, null, ".corp.example", ["192.0.2.53"]));
        bool configured = false;
        Assert.True(WindowsDnsPolicy.Install(["1.1.1.1"], provider.Invoke, provider.Flush, () =>
        {
            Assert.Same(corporate, Assert.Single(provider.Local));
            configured = true;
        }));
        Assert.True(configured);
        Assert.Equal(new[] { "ditunnel" }, provider.Removed);
        Assert.Equal(effectiveCorporateRule ? 1 : 0, provider.Effective.Count);
    }

    [Fact]
    public void EffectiveNrptDoesNotUseAdapterFallback()
    {
        var provider = new PolicyProvider();
        Assert.False(WindowsDnsPolicy.Install(["1.1.1.1"], provider.Invoke, provider.Flush,
            () => throw new Exception("Fallback must not run")));
        Assert.Empty(provider.Removed);
    }

    [Fact]
    public void WrongEffectiveDefaultIsNotBypassedWithAdapterDns()
    {
        var provider = new PolicyProvider { WrongServers = true };
        var error = Assert.Throws<DnsPolicyException>(() => WindowsDnsPolicy.Install(["1.1.1.1"], provider.Invoke,
            provider.Flush, () => throw new Exception("Fallback must not run")));
        Assert.Equal("SERVERS_MISMATCH", error.Code);
    }

    [Fact]
    public void AdapterFallbackFailureDoesNotReportSuccessOrLeaveAnOwnedNrptRule()
    {
        var provider = new PolicyProvider { ApplyDefault = false };
        var error = Assert.Throws<Win32Exception>(() => WindowsDnsPolicy.Install(["1.1.1.1"], provider.Invoke,
            provider.Flush, () => throw new Win32Exception(5)));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Empty(provider.Local);
    }

    private static ProviderOutput Row(string? name, string? comment, object? dnsNamespace, string[] servers) =>
        new(new(new Dictionary<string, object?>
        {
            ["Name"] = name, ["Comment"] = comment, ["Namespace"] = dnsNamespace, ["NameServers"] = servers
        }));

    private sealed class PolicyProvider
    {
        public List<ProviderOutput> Local { get; } = [];
        public List<ProviderOutput> Effective { get; } = [];
        public List<string> Removed { get; } = [];
        public bool ApplyDefault { get; init; } = true;
        public bool WrongServers { get; init; }
        public int AddCount { get; private set; }
        public int FlushCount { get; private set; }
        public void Flush() => FlushCount++;

        public WindowsDnsPolicy.MethodResult Invoke(string className, string method, IReadOnlyDictionary<string, object>? values)
        {
            if (className == "PS_DnsClientNrptPolicy")
            {
                Assert.Equal("Get", method);
                Assert.Equal(true, values!["Effective"]);
                return new(Effective.Cast<object>().ToArray());
            }
            Assert.Equal("PS_DnsClientNrptRule", className);
            if (method == "Get") return new(Local.Cast<object>().ToArray());
            if (method == "Remove")
            {
                var name = (string)values!["Name"];
                Removed.Add(name);
                var removed = Local.Single(row => Equals(row.Properties_.Item("Name")?.Value, name));
                Local.Remove(removed);
                if (Equals(removed.Properties_.Item("Comment")?.Value, WindowsDnsPolicy.Comment))
                    Effective.RemoveAll(row => Equals(row.Properties_.Item("Namespace")?.Value, "."));
                return new([]);
            }
            Assert.Equal("Add", method);
            AddCount++;
            Assert.Equal(new[] { "." }, (string[])values!["Namespace"]);
            Assert.Equal(WindowsDnsPolicy.Comment, values["Comment"]);
            var servers = (string[])values["NameServers"];
            Local.Add(Row("ditunnel", WindowsDnsPolicy.Comment, new[] { "." }, servers));
            if (ApplyDefault) Effective.Add(Row(null, null, ".", WrongServers ? ["192.0.2.53"] : servers.Reverse().ToArray()));
            return new([]);
        }
    }

    // Shape of the scripting provider reply, including its nullable output values.
    public sealed record ProviderOutput(PropertySet Properties_);
    public sealed record ProviderProperty(object? Value);
    public sealed class PropertySet(Dictionary<string, object?> properties)
    {
        public ProviderProperty? Item(string name) => properties.TryGetValue(name, out var value) ? new(value) : null;
    }
}
