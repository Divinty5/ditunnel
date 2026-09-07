using System.Diagnostics;
using System.Net;

namespace DiTunnel.Platform.Windows;

/// <summary>
/// A probe Xray must reach its server outside an already active DiTunnel split route.
/// This creates an exact, volatile host route only when the normal route is DiTunnel.
/// </summary>
internal sealed class WindowsProbeRouteBypass : IAsyncDisposable
{
    private readonly string? scriptPath;

    private WindowsProbeRouteBypass(string? scriptPath) => this.scriptPath = scriptPath;

    public static async Task<WindowsProbeRouteBypass> CreateAsync(IPAddress server, string sessionDirectory, CancellationToken cancellationToken)
    {
        if (server.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return new(null);
        var path = Path.Combine(sessionDirectory, "probe-route.ps1");
        try
        {
            await File.WriteAllTextAsync(path, Script, cancellationToken);
            var output = await RunAsync(path, "add", server.ToString(), cancellationToken);
            if (output == "NONE") { File.Delete(path); return new(null); }
            if (output != "ADDED") throw new InvalidOperationException("Не удалось подготовить маршрут для проверки сервера при активном VPN.");
            return new(path);
        }
        catch
        {
            try { await RunAsync(path, "remove", "", CancellationToken.None); } catch { }
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (scriptPath is null) return;
        try { await RunAsync(scriptPath, "remove", "", CancellationToken.None); }
        finally { try { File.Delete(scriptPath); } catch (IOException) { } }
    }

    private static async Task<string> RunAsync(string path, string action, string address, CancellationToken cancellationToken)
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo { FileName = shell, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path, "-Action", action, "-Address", address }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить проверку маршрута.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? output.Trim() : "ERROR";
    }

    private const string Script = """
param([ValidateSet('add','remove')][string]$Action,[string]$Address)
$ErrorActionPreference = 'Stop'
$state = Join-Path $PSScriptRoot 'probe-route.state'
if ($Action -eq 'remove') {
  if (Test-Path -LiteralPath $state) {
    $route = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
    Get-NetRoute -DestinationPrefix $route.Prefix -InterfaceIndex $route.InterfaceIndex -NextHop $route.NextHop -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $state -Force -ErrorAction SilentlyContinue
  }
  exit 0
}
$current = Find-NetRoute -RemoteIPAddress $Address | Where-Object { $_.PSObject.Properties.Name -contains 'NextHop' } | Select-Object -First 1
if (-not $current -or $current.InterfaceAlias -ne 'DiTunnel') { 'NONE'; exit 0 }
$physical = Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric,InterfaceMetric | ForEach-Object {
  $adapter = Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue
  if ($adapter -and $adapter.HardwareInterface -and $adapter.Status -eq 'Up' -and $_.NextHop -ne '0.0.0.0') { $_; break }
}
if (-not $physical) { throw 'Physical route unavailable' }
$prefix = "$Address/32"
New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $physical.InterfaceIndex -NextHop $physical.NextHop -RouteMetric 1 -PolicyStore ActiveStore | Out-Null
@{ Prefix=$prefix; InterfaceIndex=$physical.InterfaceIndex; NextHop=$physical.NextHop } | ConvertTo-Json -Compress | Set-Content -LiteralPath $state -NoNewline
'ADDED'
""";
}
