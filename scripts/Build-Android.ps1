[CmdletBinding()]
param(
    [string]$AndroidRoot = 'D:\DiTunnel-Android',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'android-arm64',
    [string]$JavaSdkDirectory = 'C:\Program Files\Android\Android Studio\jbr'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\DiTunnel.Android\DiTunnel.Android.csproj'
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$dotnetPath = Join-Path $AndroidRoot 'dotnet\dotnet.exe'
$publishRoot = Join-Path $AndroidRoot 'build-release\publish'
$releaseRoot = Join-Path $AndroidRoot 'releases'

if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw "dotnet не найден: $dotnetPath"
}

if (-not (Test-Path -LiteralPath $JavaSdkDirectory -PathType Container)) {
    throw "Android Studio JBR не найден: $JavaSdkDirectory"
}

[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$version = [string]($props.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Не удалось определить Version из $propsPath"
}

$architecture = switch ($RuntimeIdentifier) {
    'android-arm64' { 'arm64' }
    default { throw "Для $RuntimeIdentifier не задано имя архитектуры APK" }
}

$publishDirectory = Join-Path $publishRoot $version
$destinationApk = Join-Path $releaseRoot "Di-Tunnel-$version-$architecture.apk"

$env:ANDROID_HOME = Join-Path $AndroidRoot 'android-sdk'
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:ANDROID_USER_HOME = Join-Path $AndroidRoot 'android-user'
$env:DOTNET_CLI_HOME = Join-Path $AndroidRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $AndroidRoot 'nuget-packages'
$env:DITUNNEL_BUILD_ROOT = Join-Path $AndroidRoot 'build-release'
$env:DITUNNEL_LIBXRAY_AAR = Join-Path $AndroidRoot 'libxray\v26.7.28\libxray-android\libXray.aar'
$env:TEMP = Join-Path $AndroidRoot 'temp'
$env:TMP = $env:TEMP

New-Item -ItemType Directory -Force -Path $publishDirectory, $releaseRoot, $env:TEMP | Out-Null

& $dotnetPath publish $projectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --output $publishDirectory `
    --property:AndroidSdkDirectory=$env:ANDROID_HOME `
    --property:JavaSdkDirectory=$JavaSdkDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Сборка Android завершилась с кодом $LASTEXITCODE"
}

$sourceApk = Join-Path $publishDirectory 'com.divintyinteractive.ditunnel-Signed.apk'
if (-not (Test-Path -LiteralPath $sourceApk -PathType Leaf)) {
    throw "Подписанный APK не найден: $sourceApk"
}

Copy-Item -LiteralPath $sourceApk -Destination $destinationApk -Force
$hash = Get-FileHash -LiteralPath $destinationApk -Algorithm SHA256

Write-Host "APK: $destinationApk"
Write-Host "SHA256: $($hash.Hash)"
