[CmdletBinding()]
param(
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot 'eng\xray-version.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repositoryRoot ".tools\xray\$($manifest.version)\windows-x64"
}

$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$xrayPath = Join-Path $destinationPath 'xray.exe'

if (Test-Path -LiteralPath $xrayPath -PathType Leaf) {
    Write-Host "Xray-core $($manifest.version) уже установлен: $xrayPath"
    return
}

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ditunnel-xray-" + [guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $temporaryDirectory $manifest.asset
$extractPath = Join-Path $temporaryDirectory 'extracted'

try {
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    Write-Host "Загрузка Xray-core $($manifest.version)..."
    Invoke-WebRequest -Uri $manifest.url -OutFile $archivePath

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $manifest.sha256.ToLowerInvariant()) {
        throw "Контрольная сумма Xray-core не совпала. Ожидалась $($manifest.sha256), получена $actualHash."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath
    $extractedExecutable = Join-Path $extractPath 'xray.exe'
    if (-not (Test-Path -LiteralPath $extractedExecutable -PathType Leaf)) {
        throw 'Архив Xray-core не содержит xray.exe.'
    }

    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
    Copy-Item -Path (Join-Path $extractPath '*') -Destination $destinationPath -Recurse -Force
    Write-Host "Xray-core установлен: $xrayPath"
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
