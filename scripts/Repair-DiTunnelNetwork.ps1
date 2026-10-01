# Run elevated only if the app reports incomplete network cleanup.
# Refuses to interfere while a DiTunnel TUN adapter is active.
$ErrorActionPreference = 'Stop'
if (Get-NetAdapter -Name 'DiTunnel*' -ErrorAction SilentlyContinue) { throw 'DiTunnel is active. Disconnect it before repair.' }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$candidates = @(
    (Join-Path $PSScriptRoot 'Di-Tunnel.NetworkHost.exe'),
    (Join-Path $repositoryRoot 'src\DiTunnel.Desktop\bin\Debug\net10.0-windows10.0.19041.0\Di-Tunnel.NetworkHost.exe'),
    (Join-Path $repositoryRoot 'src\DiTunnel.Desktop\bin\Release\net10.0-windows10.0.19041.0\Di-Tunnel.NetworkHost.exe')
)
$client = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $client) { throw 'Di-Tunnel.NetworkHost.exe was not found. Build the Windows client before repair.' }
& $client --cleanup-wfp
if ($LASTEXITCODE -ne 0) { throw 'Di-Tunnel DNS/WFP cleanup failed. The network host may still be running.' }
Write-Host 'DiTunnel DNS policy and WFP filters removed. Physical adapter settings were not changed.'
