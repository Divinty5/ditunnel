namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class WfpPlatformProbeTests
{
    [Fact]
    public void ProbeUsesReadOnlyWfpEngineOpenAndClose()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "DiTunnel.Platform.Windows", "WfpPlatformProbe.cs"));
        Assert.Contains("FwpmEngineOpen0", source);
        Assert.Contains("FwpmEngineClose0", source);
        Assert.DoesNotContain("FwpmFilterAdd", source);
    }
}
