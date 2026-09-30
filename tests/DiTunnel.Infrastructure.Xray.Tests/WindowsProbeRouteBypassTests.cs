using System.Diagnostics;
using System.Net;
using System.Reflection;
using DiTunnel.Platform.Windows;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class WindowsProbeRouteBypassTests
{
    [Fact]
    public async Task SameEndpointWaitsButOtherEndpointsRemainParallel()
    {
        var address = IPAddress.Parse("192.0.2.10");
        using var first = await WindowsProbeRouteBypass.AcquireEndpointAsync(address, default);
        var pending = WindowsProbeRouteBypass.AcquireEndpointAsync(address, default);
        Assert.False(pending.IsCompleted);
        using var other = await WindowsProbeRouteBypass.AcquireEndpointAsync(IPAddress.Parse("192.0.2.11"), default);
        first.Dispose();
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelledWaitDoesNotReleaseAnotherProbesRoute()
    {
        var address = IPAddress.Parse("192.0.2.12");
        using var first = await WindowsProbeRouteBypass.AcquireEndpointAsync(address, default);
        using var cancellation = new CancellationTokenSource();
        var cancelled = WindowsProbeRouteBypass.AcquireEndpointAsync(address, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var pending = WindowsProbeRouteBypass.AcquireEndpointAsync(address, default);
        Assert.False(pending.IsCompleted);
        first.Dispose();
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("AmneziaVPN", false, false, "CREATED\nADDED|192.0.2.5", "REMOVED")]
    [InlineData("DiTunnel-Test", false, false, "CREATED\nADDED|192.0.2.5", "REMOVED")]
    [InlineData("AmneziaVPN", false, true, "BOUND|192.0.2.5", "")]
    [InlineData("Wi-Fi", true, false, "NONE", "")]
    public async Task RouteTransactionBypassesVirtualAdaptersAndPreservesExistingRoutes(
        string alias, bool hardware, bool existing, string added, string removed)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "probe-route-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "probe-route.ps1");
        var source = (string)typeof(WindowsProbeRouteBypass).GetField("Script", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var mock = $$"""
            $syntheticHardware = ${{hardware.ToString().ToLowerInvariant()}}
            $syntheticExisting = ${{existing.ToString().ToLowerInvariant()}}
            function Find-NetRoute { param($RemoteIPAddress) [pscustomobject]@{ InterfaceIndex=55; InterfaceAlias='{{alias}}'; NextHop='0.0.0.0' } }
            function Get-NetAdapter { param($InterfaceIndex,$ErrorAction) [pscustomobject]@{ HardwareInterface=($InterfaceIndex -eq 2 -or $syntheticHardware); Status='Up' } }
            function Get-NetIPInterface { param($InterfaceIndex,$AddressFamily,$ErrorAction) [pscustomobject]@{ InterfaceMetric=10 } }
            function Get-NetIPAddress { param($InterfaceIndex,$AddressFamily,$ErrorAction) [pscustomobject]@{ IPAddress='192.0.2.5'; AddressState='Preferred' } }
            function Get-NetRoute {
                param($AddressFamily,$DestinationPrefix,$InterfaceIndex,$NextHop,$PolicyStore,$ErrorAction)
                if ($DestinationPrefix -eq '0.0.0.0/0') { [pscustomobject]@{ InterfaceIndex=2; NextHop='192.0.2.1'; RouteMetric=10 } }
                elseif ($syntheticExisting -or (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'probe-route.state'))) { [pscustomobject]@{ Synthetic=$true } }
            }
            function New-NetRoute { param($DestinationPrefix,$InterfaceIndex,$NextHop,$RouteMetric,$PolicyStore) [Console]::Out.WriteLine('CREATED') }
            function Remove-NetRoute { [CmdletBinding()] param([Parameter(ValueFromPipeline=$true)]$InputObject,[switch]$Confirm) process { [Console]::Out.WriteLine('REMOVED') } }
            """;
        // Every network cmdlet used by the production script is shadowed with synthetic data.
        source = source.Insert(source.IndexOf('\n') + 1, mock + "\n");
        try
        {
            await File.WriteAllTextAsync(path, source);
            Assert.Equal(added, await RunAsync(path, "add"));
            Assert.Equal(!hardware && !existing, File.Exists(Path.Combine(directory, "probe-route.state")));
            Assert.Equal(removed, await RunAsync(path, "remove"));
            Assert.False(File.Exists(Path.Combine(directory, "probe-route.state")));
        }
        finally { foreach (var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }

    [Fact]
    public async Task CancellationWaitsForOwnershipRecordBeforeReturning()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "probe-cancel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "synthetic.ps1");
        var started = Path.Combine(directory, "started");
        var finished = Path.Combine(directory, "finished");
        Task<string>? task = null;
        try
        {
            await File.WriteAllTextAsync(path, """
                param($Action,$Address)
                Set-Content -LiteralPath (Join-Path $PSScriptRoot 'started') -Value 'synthetic'
                Start-Sleep -Milliseconds 400
                Set-Content -LiteralPath (Join-Path $PSScriptRoot 'finished') -Value 'synthetic'
                'ADDED|192.0.2.5'
                """);
            using var cancellation = new CancellationTokenSource();
            var method = typeof(WindowsProbeRouteBypass).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            task = (Task<string>)method.Invoke(null, [path, "add", "192.0.2.10", cancellation.Token])!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(started)) await Task.Delay(10, deadline.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(File.Exists(finished));
        }
        finally
        {
            if (task is not null) { try { await task; } catch (OperationCanceledException) { } }
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static async Task<string> RunAsync(string path, string action)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path, "-Action", action, "-Address", "192.0.2.10" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        Assert.Equal("", await errors);
        Assert.Equal(0, process.ExitCode);
        return (await output).Trim().Replace("\r\n", "\n");
    }
}
