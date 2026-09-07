using System.Diagnostics;
using System.Text;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class TunnelDnsStageTests
{
    [Theory]
    [InlineData("rule", "FAILED_DNS_RULE")]
    [InlineData("cache", "FAILED_DNS_CACHE")]
    [InlineData("none", "READY_2")]
    public async Task DnsErrorsAreDistinctFromDelayedRouteSelection(string failure, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "Network", "Run-Tunnel.ps1"));
        var start = source.IndexOf("    if ($SplitTunnelMode -ne 'ProxySelected')", StringComparison.Ordinal);
        var end = source.IndexOf("    Enter-Stage 'PROBE'", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        // Execute the exact production DNS/route-check block with network cmdlets replaced by in-memory stubs.
        var setup = " $failure = '" + failure + "'\n" + """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $index = 99
            $dnsComment = 'test'
            $SplitTunnelMode = 'BypassSelected'
            $splitIps = @()
            $calls = 0
            function Enter-Stage($name) { $script:stage = $name }
            function Test-StopRequested { return $false }
            function Add-OwnedRoute { }
            function Update-SplitRoutes { }
            function Add-DnsClientNrptRule {
                param([string[]]$Namespace, [string[]]$NameServers, [string]$Comment, [switch]$PassThru)
                if ($Namespace.Count -ne 1 -or $Namespace[0] -ne '.' -or $NameServers.Count -ne 2) { throw 'bad arguments' }
                if ($failure -eq 'rule') { throw 'synthetic DNS policy failure' }
                return [pscustomobject]@{Name='test'}
            }
            function Clear-DnsClientCache { if ($failure -eq 'cache') { throw 'synthetic DNS cache failure' } }
            function Find-NetRoute {
                $script:calls++
                $selected = if ($script:calls -eq 1) { 2 } else { 99 }
                return @([pscustomobject]@{IPAddress='192.0.2.1'}, [pscustomobject]@{InterfaceIndex=$selected; NextHop='0.0.0.0'})
            }
            """;
        var command = setup + "\ntry {\n" + source[start..end] + "\n[Console]::Out.WriteLine('READY_' + $calls)\n} catch { [Console]::Out.WriteLine('FAILED_' + $stage) }";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(expected, (await output).Trim());
            var stderr = await error;
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
