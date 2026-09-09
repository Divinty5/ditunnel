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

    [Fact]
    public async Task NetworkHostWaitsForWfpBeforeInstallingDefaultRoutes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "Network", "Run-Tunnel.ps1"));
        var interfaceReady = source.IndexOf("Emit ('TUNNEL_INTERFACE_' + $index)", StringComparison.Ordinal);
        var handshake = source.IndexOf("Test-Path -LiteralPath $KillSwitchReadyPath", interfaceReady, StringComparison.Ordinal);
        var routes = source.IndexOf("Enter-Stage 'ROUTES'", interfaceReady, StringComparison.Ordinal);
        Assert.True(interfaceReady >= 0 && handshake > interfaceReady && routes > handshake);
        Assert.Contains("--cleanup-wfp", source);
    }

    [Fact]
    public async Task KillSwitchUsesOwnedPersistentWfpObjects()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "WindowsKillSwitchController.cs"));
        Assert.Contains("FwpmProviderAdd0", source);
        Assert.Contains("FwpmSubLayerAdd0", source);
        Assert.Contains("FWPM_FILTER_FLAG_PERSISTENT", source);
        Assert.Contains("FWP_ACTION_BLOCK", source);
        Assert.Contains("FwpmFilterDeleteByKey0", source);
        Assert.Contains("PermitProbeEndpointAsync", source);
        Assert.Contains("AddDirectAddressesAsync", source);
        Assert.Contains("Permit selected direct", source);
    }

    [Fact]
    public async Task ProfileSwitchPreservesKillSwitchThroughRouteRollback()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var engine = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "WindowsVpnEngine.cs"));
        var host = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "Network", "Run-Tunnel.ps1"));
        Assert.Contains("PrepareTransitionAsync", engine);
        Assert.Contains("preserve-kill-switch", engine);
        Assert.Contains("GetDirectoryName($ConfigurationPath)", host);
        Assert.Contains("-not (Test-Path -LiteralPath $preserveKillSwitchPath)", host);
    }

    [Fact]
    public async Task NetworkLossStaysFailClosedDuringAutomaticRecovery()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var engine = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "WindowsVpnEngine.cs"));
        var host = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "Network", "Run-Tunnel.ps1"));
        Assert.Contains("Emit 'ERROR_NETWORK_CHANGED'", host);
        Assert.Contains("Set-Content -LiteralPath $preserveKillSwitchPath", host);
        Assert.Contains("CreateForActiveTunnel", engine);
        Assert.Contains("knownServerAddress: reconnectServerAddress", engine);
        Assert.Contains("policy.KillSwitchEnabled && IsNetworkProtectionActive", engine);
        Assert.Contains("preserveProtection ? reconnectServerAddress : null", engine);
        Assert.Contains("!cleanupFailed || recoveryReason != VpnRecoveryReason.None", engine);
    }

    [Fact]
    public async Task SplitRoutesDiscoverMatchingSubdomainsFromDnsCache()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "Network", "Run-Tunnel.ps1"));

        Assert.Contains("Get-DnsClientCache", source);
        Assert.Contains("$cachedName.EndsWith('.' + $domain", source);
        Assert.Contains("$_.RecordName", source);
        Assert.Contains("AddSeconds(1)", source);
        Assert.Contains("SPLIT_ADDRESS_", source);
    }
}
