using System.Diagnostics;
using System.Text;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class AmneziaWgHostTests
{
    private static async Task<string> SourceAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "Network", "Run-Tunnel.ps1"));
    }
    private static async Task<string> RunAsync(string command)
    {
        command = "$ProgressPreference='SilentlyContinue'\n" + command;
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        Assert.Equal("", await errors);
        Assert.Equal(0, process.ExitCode);
        return (await output).Trim();
    }
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    [Theory]
    [InlineData("Up", "192.0.2.5", "192.0.2.1", false)]
    [InlineData("Down", "192.0.2.5", "192.0.2.1", true)]
    [InlineData("Up", "192.0.2.6", "192.0.2.1", true)]
    [InlineData("Up", "192.0.2.5", "192.0.2.2", true)]
    public async Task PhysicalNetworkChangesRequireAwgTransportToRestart(string status, string address, string gateway, bool changed)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = await SourceAsync();
        var start = source.IndexOf("function Test-PhysicalNetworkChanged", StringComparison.Ordinal);
        var end = source.IndexOf("function Add-TunnelAddress", start, StringComparison.Ordinal);
        var setup = $"$ErrorActionPreference='Stop'\n$AmneziaRuntimePath='synthetic'\n$physicalSourceAddress='192.0.2.5'\n$upstream=[pscustomobject]@{{ InterfaceIndex=42; NextHop='192.0.2.1' }}\nfunction Get-NetAdapter {{ [pscustomobject]@{{ Status={Quote(status)} }} }}\nfunction Get-NetIPAddress {{ [pscustomobject]@{{ IPAddress={Quote(address)}; AddressState='Preferred' }} }}\nfunction Get-NetRoute {{ [pscustomobject]@{{ InterfaceIndex=42; NextHop={Quote(gateway)} }} }}\n";
        Assert.Equal(changed.ToString(), await RunAsync(setup + source[start..end] + "\n[Console]::Out.WriteLine((Test-PhysicalNetworkChanged))"));
    }

    [Theory]
    [InlineData("AllTraffic")]
    [InlineData("BypassSelected")]
    [InlineData("ProxySelected")]
    public async Task PrivateDnsRoutesAndNrptUseTheProfileResolvers(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = await SourceAsync();
        var start = source.IndexOf("    if ($SplitTunnelMode -ne 'ProxySelected')", StringComparison.Ordinal);
        var end = source.IndexOf("    Enter-Stage 'DNS_CACHE'", start, StringComparison.Ordinal);
        var setup = "$ErrorActionPreference='Stop'\n$SplitTunnelMode=" + Quote(mode) + "\n" + """
            $index=42
            $dnsServers=@('192.0.2.53','2001:db8::53')
            $dnsComment='synthetic'
            $splitIps=@()
            $routes=[Collections.Generic.List[string]]::new()
            function Add-OwnedRoute($prefix,$interface,$nextHop) {
                if ($interface -ne 42) { throw 'incorrect interface' }
                $routes.Add($prefix)
            }
            function Enter-Stage { }
            function Update-SplitRoutes { }
            function Add-DnsClientNrptRule {
                param($Namespace,$NameServers,$Comment,[switch]$PassThru)
                if (($NameServers -join ',') -ne '192.0.2.53,2001:db8::53') { throw 'wrong DNS resolvers' }
            }
            """;
        var assertions = """
            if (-not $routes.Contains('192.0.2.53/32') -or -not $routes.Contains('2001:db8::53/128')) { throw 'DNS route missing' }
            [Console]::Out.WriteLine('READY')
            """;
        Assert.Equal("READY", await RunAsync(setup + "\n" + source[start..end] + "\n" + assertions));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceBindingKeepsAwgProxyOnLoopbackAndDirectOutboundOnPhysicalAddress(bool awg)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = await SourceAsync();
        var start = source.IndexOf("function Set-XrayOutboundSource", StringComparison.Ordinal);
        var end = source.IndexOf("function Start-AmneziaCore", start, StringComparison.Ordinal);
        var directory = Path.Combine(Path.GetTempPath(), "awg-host-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "config.json");
        var awgConfig = Path.Combine(directory, "amneziawg.json");
        try
        {
            await File.WriteAllTextAsync(config, """{"inbounds":[{"tag":"tun","settings":{"autoOutboundsInterface":"auto"}}],"outbounds":[{"tag":"proxy","protocol":"socks","settings":{"servers":[{"address":"127.0.0.1","port":0}]}},{"tag":"direct","protocol":"freedom"}]}""");
            await File.WriteAllTextAsync(awgConfig, """{"sourceAddress":"","sourceInterface":0}""");
            var setup = $"$ErrorActionPreference='Stop'\n$ConfigurationPath={Quote(config)}\n$amneziaConfigurationPath={Quote(awgConfig)}\n$AmneziaRuntimePath=" + (awg ? "'synthetic'" : "''") + "\n" + """
                function Get-NetIPAddress { [pscustomobject]@{ IPAddress='192.0.2.5'; AddressState='Preferred' } }
                """;
            var assertions = """
                Set-XrayOutboundSource 42
                $config = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
                $proxy = $config.outbounds[0]
                if ($config.inbounds[0].settings.PSObject.Properties.Name -contains 'autoOutboundsInterface') { throw 'automatic binding remains' }
                if ($config.outbounds[1].sendThrough -ne '192.0.2.5') { throw 'direct outbound is unbound' }
                if ($AmneziaRuntimePath) {
                    if ($proxy.PSObject.Properties.Name -contains 'sendThrough') { throw 'loopback bound to physical address' }
                    $awg = Get-Content -LiteralPath $amneziaConfigurationPath -Raw | ConvertFrom-Json
                    if ($awg.sourceAddress -ne '192.0.2.5') { throw 'AWG transport is unbound' }
                    if ($awg.sourceInterface -ne 42) { throw 'AWG interface is unbound' }
                } elseif ($proxy.sendThrough -ne '192.0.2.5') { throw 'ordinary outbound is unbound' }
                [Console]::Out.WriteLine('READY')
                """;
            Assert.Equal("READY", await RunAsync(setup + "\n" + source[start..end] + "\n" + assertions));
        }
        finally { File.Delete(config); File.Delete(awgConfig); Directory.Delete(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AwgFailurePreservesProtectionOnlyAfterTunnelWasConnected(bool connected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = await SourceAsync();
        var start = source.IndexOf("function Test-AmneziaCoreExited", StringComparison.Ordinal);
        var end = source.IndexOf("function Add-TunnelAddress", start, StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), "awg-preserve-" + Guid.NewGuid().ToString("N"));
        try
        {
            var setup = "$ErrorActionPreference='Stop'\n$amneziaProcess=[pscustomobject]@{HasExited=$true;ExitCode=1}\n$KillSwitchReadyPath='synthetic'\n$preserveKillSwitchPath=" + Quote(path) + "\n$tunnelConnected=" + (connected ? "$true" : "$false") + "\nfunction Emit($line) { [Console]::Out.WriteLine($line) }\n";
            Assert.Equal("ERROR_AWG_EXIT_1", await RunAsync(setup + source[start..end] + "\nif (-not (Test-AmneziaCoreExited)) { throw 'failure not detected' }"));
            Assert.Equal(connected, File.Exists(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
