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

    // Shape of the scripting provider reply, including its nullable output values.
    public sealed record ProviderOutput(PropertySet Properties_);
    public sealed record ProviderProperty(object? Value);
    public sealed class PropertySet(Dictionary<string, object?> properties)
    {
        public ProviderProperty? Item(string name) => properties.TryGetValue(name, out var value) ? new(value) : null;
    }
}
