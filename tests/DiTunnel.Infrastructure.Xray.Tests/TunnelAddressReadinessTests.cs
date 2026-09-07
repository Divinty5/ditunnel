using System.Diagnostics;
using System.Text;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class TunnelAddressReadinessTests
{
    [Fact]
    public async Task AddressAssignmentRetriesTransientNetshFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "Network", "Run-Tunnel.ps1"));
        var start = source.IndexOf("function Add-TunnelAddress", StringComparison.Ordinal);
        var end = source.IndexOf("function Add-OwnedRoute", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var setup = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $attempts = 0
            $index = 0
            $xrayProcess = [pscustomobject]@{ HasExited = $false }
            $netshRunner = {
                param([string[]]$arguments)
                $script:attempts++
                if ($script:attempts -eq 1) { return 1 }
                return 0
            }
            function Test-StopRequested { return $false }
            function Get-NetAdapter { [pscustomobject]@{ InterfaceIndex = 42 } }
            function Get-NetIPAddress { }
            function Start-Sleep { }
            """;
        var command = setup + "\n" + source[start..end] + "\nAdd-TunnelAddress '172.31.255.1' 30 'IPv4'\n[Console]::Out.WriteLine(\"READY_${attempts}_${index}\")";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("READY_2_42", (await output).Trim());
        var stderr = await error;
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
    }
}
