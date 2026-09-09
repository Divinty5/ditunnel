# Run elevated only if the app reports incomplete network cleanup.
# Refuses to interfere while a DiTunnel TUN adapter is active.
$ErrorActionPreference = 'Stop'
if (Get-NetAdapter -Name 'DiTunnel*' -ErrorAction SilentlyContinue) { throw 'DiTunnel is active. Disconnect it before repair.' }
$client = Join-Path $PSScriptRoot 'Di-Tunnel.exe'
if (Test-Path -LiteralPath $client) {
    & $client --cleanup-wfp
    if ($LASTEXITCODE -ne 0) { throw 'Di-Tunnel WFP cleanup failed.' }
}
$networkMutex = [Threading.Mutex]::new($false, 'Global\DiTunnel.NetworkHost.v1')
$locked = $false
try {
    try { $locked = $networkMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'The DiTunnel network host is still running.' }
    Get-DnsClientNrptRule | Where-Object { $_.Comment -eq 'DiTunnel managed DNS v1' } | Remove-DnsClientNrptRule -Force
    Clear-DnsClientCache
    Write-Host 'DiTunnel DNS policy removed. Physical adapter settings were not changed.'
} finally { if ($locked) { $networkMutex.ReleaseMutex() }; $networkMutex.Dispose() }
