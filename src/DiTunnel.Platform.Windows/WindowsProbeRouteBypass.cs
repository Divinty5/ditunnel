using System.Diagnostics;
using System.Net;

namespace DiTunnel.Platform.Windows;

/// <summary>
/// Probes use the same physical uplink as a connection, including when another VPN is active.
/// An endpoint lease prevents concurrent probes from removing each other's temporary route.
/// </summary>
internal sealed class WindowsProbeRouteBypass : IAsyncDisposable
{
    private readonly string? scriptPath;
    private readonly IDisposable endpointLease;
    private static readonly Dictionary<IPAddress, EndpointGate> endpointGates = new();
    public string? SourceAddress { get; }

    private WindowsProbeRouteBypass(string? scriptPath, IDisposable endpointLease, string? sourceAddress = null)
    { this.scriptPath = scriptPath; this.endpointLease = endpointLease; SourceAddress = sourceAddress; }

    internal static async Task<IDisposable> AcquireEndpointAsync(IPAddress address, CancellationToken cancellationToken)
    {
        EndpointGate gate;
        lock (endpointGates)
        {
            if (!endpointGates.TryGetValue(address, out gate!)) endpointGates[address] = gate = new();
            gate.Users++;
        }
        try { await gate.Semaphore.WaitAsync(cancellationToken); }
        catch { ReleaseReference(address, gate); throw; }
        return new EndpointLease(address, gate);
    }

    private static void ReleaseReference(IPAddress address, EndpointGate gate)
    {
        lock (endpointGates)
        {
            if (--gate.Users == 0) { endpointGates.Remove(address); gate.Semaphore.Dispose(); }
        }
    }

    private sealed class EndpointGate { internal readonly SemaphoreSlim Semaphore = new(1); internal int Users; }
    private sealed class EndpointLease(IPAddress address, EndpointGate gate) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            gate.Semaphore.Release();
            ReleaseReference(address, gate);
        }
    }

    public static async Task<WindowsProbeRouteBypass> CreateAsync(IPAddress server, string sessionDirectory, CancellationToken cancellationToken)
    {
        var lease = await AcquireEndpointAsync(server, cancellationToken);
        if (server.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return new(null, lease);
        var path = Path.Combine(sessionDirectory, "probe-route.ps1");
        try
        {
            await File.WriteAllTextAsync(path, Script, cancellationToken);
            var output = await RunAsync(path, "add", server.ToString(), cancellationToken);
            if (output == "NONE") { File.Delete(path); return new(null, lease); }
            if ((!output.StartsWith("ADDED|", StringComparison.Ordinal) && !output.StartsWith("BOUND|", StringComparison.Ordinal)) || !IPAddress.TryParse(output[6..], out _))
                throw new InvalidOperationException("Не удалось подготовить маршрут для проверки сервера при активном VPN.");
            return new(path, lease, output[6..]);
        }
        catch
        {
            try { await RunAsync(path, "remove", "", CancellationToken.None); } catch { }
            try { File.Delete(path); } catch (IOException) { }
            lease.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (scriptPath is not null)
            {
                try { await RunAsync(scriptPath, "remove", "", CancellationToken.None); }
                finally { try { File.Delete(scriptPath); } catch (IOException) { } }
            }
        }
        finally { endpointLease.Dispose(); }
    }

    private static async Task<string> RunAsync(string path, string action, string address, CancellationToken cancellationToken)
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo { FileName = shell, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path, "-Action", action, "-Address", address }) start.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить проверку маршрута.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        // Let a short route transaction finish and write its ownership record before
        // observing cancellation, so cleanup cannot race a still running add command.
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        await errors;
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode == 0 ? (await output).Trim() : "ERROR";
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
if (-not $current) { 'NONE'; exit 0 }
$currentAdapter = Get-NetAdapter -InterfaceIndex $current.InterfaceIndex -ErrorAction SilentlyContinue
if ($currentAdapter -and $currentAdapter.HardwareInterface) { 'NONE'; exit 0 }
if (-not $currentAdapter -and $current.InterfaceAlias -notlike 'DiTunnel-*') { 'NONE'; exit 0 }
$physical = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | ForEach-Object {
  $adapter = Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue
  if ($adapter -and $adapter.HardwareInterface -and $adapter.Status -eq 'Up' -and $_.NextHop -ne '0.0.0.0') {
    $ipInterface = Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
    [pscustomobject]@{ InterfaceIndex=$_.InterfaceIndex; NextHop=$_.NextHop; Metric=[int]$_.RouteMetric + [int]($ipInterface.InterfaceMetric | Select-Object -First 1) }
  }
} | Sort-Object Metric | Select-Object -First 1
if (-not $physical) { throw 'Physical route unavailable' }
$sourceAddress = Get-NetIPAddress -InterfaceIndex $physical.InterfaceIndex -AddressFamily IPv4 -ErrorAction Stop |
  Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.AddressState -in @('Preferred','Deprecated') } |
  Select-Object -ExpandProperty IPAddress -First 1
if (-not $sourceAddress) { throw 'Physical source address unavailable' }
$prefix = "$Address/32"
$existing = Get-NetRoute -DestinationPrefix $prefix -InterfaceIndex $physical.InterfaceIndex -NextHop $physical.NextHop -PolicyStore ActiveStore -ErrorAction SilentlyContinue
if ($existing) { 'BOUND|' + $sourceAddress; exit 0 }
New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $physical.InterfaceIndex -NextHop $physical.NextHop -RouteMetric 1 -PolicyStore ActiveStore | Out-Null
@{ Prefix=$prefix; InterfaceIndex=$physical.InterfaceIndex; NextHop=$physical.NextHop } | ConvertTo-Json -Compress | Set-Content -LiteralPath $state -NoNewline
'ADDED|' + $sourceAddress
""";
}
