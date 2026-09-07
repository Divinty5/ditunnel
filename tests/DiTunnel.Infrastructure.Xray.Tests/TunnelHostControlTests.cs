using System.Diagnostics;
using System.Text;

namespace DiTunnel.Infrastructure.Xray.Tests;

public sealed class TunnelHostControlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionHostReachesPrecheckWithoutWaitingForStdinAndRollsBack(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DiTunnel.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var script = Path.Combine(root.FullName, "src", "DiTunnel.Platform.Windows", "Network", "Run-Tunnel.ps1");
        var directory = Path.Combine(Path.GetTempPath(), "ditunnel-host-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "config.json");
        await File.WriteAllTextAsync(config, "{}");
        if (cancel) await File.WriteAllTextAsync(config + ".stop", "stop");
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        // Only mock OS network access. Execute the real production control/cleanup code in Windows PowerShell 5.
        var command = "function Get-DnsClientNrptRule {}\nfunction Remove-DnsClientNrptRule {}\nfunction Clear-DnsClientCache {}\n" +
            "function Find-NetRoute { throw [InvalidOperationException]::new('synthetic precheck failure') }\n" +
            $"& {Quote(script)} -RuntimePath 'unused' -ConfigurationPath {Quote(config)} -ServerAddress '192.0.2.1' -OwnerProcessId {Environment.ProcessId}";
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            var output = await stdout;
            Assert.True(output.Contains("STAGE_PRECHECK"), output + await stderr);
            Assert.Contains(cancel ? "CANCELLED" : "ERROR_STAGE_PRECHECK", output);
            Assert.Contains("STOPPED", output);
            Assert.False(File.Exists(config));
            Assert.False(File.Exists(config + ".stop"));
            Assert.True(string.IsNullOrWhiteSpace(await stderr));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            if (File.Exists(config)) File.Delete(config);
            if (File.Exists(config + ".stop")) File.Delete(config + ".stop");
            Directory.Delete(directory);
        }
    }
}
