[CmdletBinding()]
param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -Raw (Join-Path $repositoryRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
$publishDir = Join-Path $repositoryRoot "artifacts\windows\$version\client"
if (-not $CompilerPath) {
    $candidates = @((Join-Path $repositoryRoot '.tools\inno\ISCC.exe'), "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe")
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $CompilerPath) { throw 'Install Inno Setup (https://jrsoftware.org/isdl.php) or pass -CompilerPath to ISCC.exe.' }
& (Join-Path $PSScriptRoot 'Install-Xray.ps1')
dotnet publish (Join-Path $repositoryRoot 'src\DiTunnel.Desktop\DiTunnel.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
foreach ($required in @('Di-Tunnel.exe', 'Runtime\xray.exe', 'Runtime\wintun.dll', 'Runtime\geoip.dat', 'Network\Run-Tunnel.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $required))) { throw "Missing published file: $required" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Repair-DiTunnelNetwork.ps1') -Destination $publishDir
& $CompilerPath "/DAppVersion=$version" "/DPublishDir=$publishDir" (Join-Path $repositoryRoot 'installer\DiTunnel.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $repositoryRoot "artifacts\installer\Di-Tunnel-$version-Setup-x64.exe"
$digest = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$installer.sha256" -Value "$digest  $([IO.Path]::GetFileName($installer))" -Encoding ascii
Write-Output "Installer: $installer"
