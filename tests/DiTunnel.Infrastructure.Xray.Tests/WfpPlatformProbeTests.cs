namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class WfpPlatformProbeTests
{
    [Fact]
    public void ProbeUsesReadOnlyWfpEngineOpenAndClose()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WfpPlatformProbe.cs"));
        Assert.Contains("FwpmEngineOpen0", source);
        Assert.Contains("FwpmEngineClose0", source);
        Assert.DoesNotContain("FwpmFilterAdd", source);
    }
}
