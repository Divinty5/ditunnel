[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WindowsInstaller,
    [Parameter(Mandatory)][string]$AndroidApk,
    [Parameter(Mandatory)][string]$LinuxDeb
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$properties = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup
$windowsVersion = @($properties.WindowsVersion | Where-Object { $_ })[0].Trim()
$androidVersion = @($properties.AndroidVersion | Where-Object { $_ })[0].Trim()
$linuxVersion = @($properties.LinuxVersion | Where-Object { $_ })[0].Trim()
if ($windowsVersion -ne $androidVersion -or $windowsVersion -ne $linuxVersion) { throw 'Версии Windows, Android и Linux не совпадают.' }
if ($windowsVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Версия релиза должна иметь формат major.minor.patch.' }

function Test-ReleaseFile([string]$Path, [string]$ExpectedName) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Файл не найден: $Path" }
    if ([IO.Path]::GetFileName($Path) -cne $ExpectedName) {
        throw "Для обновления требуется имя $ExpectedName. Получено: $([IO.Path]::GetFileName($Path))"
    }
    $checksumPath = "$Path.sha256"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw "Не найден SHA-256: $checksumPath" }
    $lines = @(Get-Content -LiteralPath $checksumPath | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -ne 1 -or $lines[0] -notmatch '^([0-9a-fA-F]{64})[ \t]+\*?(.+?)\s*$') {
        throw "В $checksumPath должна быть одна строка: SHA-256 и имя файла."
    }
    $expectedHash = $Matches[1]
    if ($Matches[2] -cne $ExpectedName) { throw "Имя внутри $checksumPath не соответствует $ExpectedName." }
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) { throw "Контрольная сумма не совпала: $ExpectedName" }
    [pscustomobject]@{ File = $ExpectedName; SHA256 = $actualHash.ToLowerInvariant(); Bytes = (Get-Item -LiteralPath $Path).Length }
}

Test-ReleaseFile $WindowsInstaller "Di-Tunnel-$windowsVersion-Setup-x64.exe"
Test-ReleaseFile $AndroidApk "Di-Tunnel-$androidVersion-arm64.apk"
Test-ReleaseFile $LinuxDeb "Di-Tunnel-$linuxVersion-linux-amd64.deb"
Write-Host "Файлы проверены для стабильного релиза v$windowsVersion."
