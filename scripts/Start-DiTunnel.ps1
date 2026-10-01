[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.WindowsVersion | Where-Object { $_ } | Select-Object -First 1
if (Get-Process -Name 'Di-Tunnel' -ErrorAction SilentlyContinue) {
    throw 'Di-Tunnel is already running. Disconnect the VPN and exit the application from the system tray, then run this script again. A new instance only shows the existing application window.'
}
$clientPath = Join-Path $repositoryRoot 'src\DiTunnel.Desktop\bin\Debug\net10.0-windows10.0.19041.0\Di-Tunnel.exe'
& (Join-Path $PSScriptRoot 'Install-Xray.ps1')
& (Join-Path $PSScriptRoot 'Build-AmneziaWG.ps1')
dotnet build (Join-Path $repositoryRoot 'src\DiTunnel.Desktop\DiTunnel.Desktop.csproj')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $clientPath)) { throw 'Di-Tunnel build failed. See the build output above.' }
if (Get-Process -Name 'Di-Tunnel' -ErrorAction SilentlyContinue) {
    throw 'Another Di-Tunnel instance started during the build. Exit it from the system tray and run this script again.'
}
Write-Output "Starting Di-Tunnel $version`: $clientPath"
Start-Process -FilePath $clientPath -WorkingDirectory $repositoryRoot
