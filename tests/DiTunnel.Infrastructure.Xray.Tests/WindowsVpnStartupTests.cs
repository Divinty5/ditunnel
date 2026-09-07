namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class WindowsVpnStartupTests
{
    [Fact]
    public async Task TunConfigurationIsStartedOnlyByNetworkHost()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "WindowsVpnEngine.cs"));
        var tunBuild = source.IndexOf("configuration.Build(address.ToString(), true", StringComparison.Ordinal);
        var hostStart = source.IndexOf("host = Process.Start(start)", tunBuild, StringComparison.Ordinal);
        Assert.True(tunBuild >= 0 && hostStart > tunBuild);
        Assert.DoesNotContain("ValidateConfigurationAsync", source[tunBuild..hostStart]);
    }

    [Fact]
    public async Task DevelopmentLauncherAlwaysBuildsBeforeStartingClient()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "scripts", "Start-DiTunnel.ps1"));
        Assert.Contains("Install-Xray.ps1", source);
        Assert.Contains("dotnet build", source);
        Assert.DoesNotContain("if (-not (Test-Path -LiteralPath $clientPath))", source);
    }

    [Fact]
    public async Task GeneratedSocksProbeDoesNotStartAValidationProcessFirst()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Infrastructure.Xray", "XrayServerProbe.cs"));
        Assert.Contains("ValidateConfigurationBeforeStart = false", source);
    }
}
