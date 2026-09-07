[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$clientPath = Join-Path $repositoryRoot 'src\DiTunnel.Desktop\bin\Debug\net10.0-windows10.0.19041.0\Di-Tunnel.exe'
& (Join-Path $PSScriptRoot 'Install-Xray.ps1')
dotnet build (Join-Path $repositoryRoot 'src\DiTunnel.Desktop\DiTunnel.Desktop.csproj')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $clientPath)) { throw 'Di-Tunnel build failed. See the build output above.' }
Start-Process -FilePath $clientPath -WorkingDirectory $repositoryRoot -Verb RunAs
