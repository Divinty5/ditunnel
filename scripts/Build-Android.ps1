[CmdletBinding()]
param(
    [string]$AndroidRoot = 'D:\DiTunnel-Android',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'android-arm64',
    [string]$JavaSdkDirectory = 'C:\Program Files\Android\Android Studio\jbr',
    [string]$SigningConfig
)

$ErrorActionPreference = 'Stop'

$AndroidRoot = [IO.Path]::GetFullPath($AndroidRoot)
$JavaSdkDirectory = [IO.Path]::GetFullPath($JavaSdkDirectory)
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\DiTunnel.Android\DiTunnel.Android.csproj'
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$dotnetPath = Join-Path $AndroidRoot 'dotnet\dotnet.exe'
$buildRoot = Join-Path $AndroidRoot ("build-release\" + [Guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $buildRoot 'publish'
$releaseRoot = Join-Path $AndroidRoot 'releases'

if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw "dotnet не найден: $dotnetPath"
}

if (-not (Test-Path -LiteralPath $JavaSdkDirectory -PathType Container)) {
    throw "Android Studio JBR не найден: $JavaSdkDirectory"
}

[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$version = [string]($props.Project.PropertyGroup.AndroidVersion | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Не удалось определить AndroidVersion из $propsPath"
}

$architecture = switch ($RuntimeIdentifier) {
    'android-arm64' { 'arm64' }
    default { throw "Для $RuntimeIdentifier не задано имя архитектуры APK" }
}

$publishDirectory = Join-Path $publishRoot $version
$suffix = if ($Configuration -eq 'Debug') { '-debug' } else { '' }
$destinationApk = Join-Path $releaseRoot "Di-Tunnel-$version-$architecture$suffix.apk"
$publishSigning = @()
$apkChecks = @{}
if ($Configuration -eq 'Release') {
    if (-not $SigningConfig) { $SigningConfig = Join-Path $AndroidRoot 'signing/signing.local.json' }
    $signingFile = Get-Item -LiteralPath $SigningConfig
    $signing = Get-Content -LiteralPath $signingFile.FullName -Raw | ConvertFrom-Json
    foreach ($field in @('KeyStore', 'StorePasswordFile', 'KeyPasswordFile')) {
        if (-not $signing.$field) { throw "В конфигурации подписи отсутствует $field." }
        $path = [IO.Path]::GetFullPath($signing.$field, $signingFile.DirectoryName)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Файл $field из конфигурации подписи не найден." }
        $signing.$field = $path
    }
    if (-not $signing.KeyAlias -or $signing.CertificateSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'В конфигурации подписи требуются KeyAlias и CertificateSha256.'
    }
    $publishSigning = @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$($signing.KeyStore)",
        "-p:AndroidSigningKeyAlias=$($signing.KeyAlias)",
        "-p:AndroidSigningStorePass=file:$($signing.StorePasswordFile)",
        "-p:AndroidSigningKeyPass=file:$($signing.KeyPasswordFile)"
    )
    $apkChecks = @{ SigningCertificateSha256 = $signing.CertificateSha256; RequireCompressedNativeLibraries = $true }
}

$env:ANDROID_HOME = Join-Path $AndroidRoot 'android-sdk'
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:ANDROID_USER_HOME = Join-Path $AndroidRoot 'android-user'
$env:DOTNET_CLI_HOME = Join-Path $AndroidRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $AndroidRoot 'nuget-packages'
$env:DITUNNEL_BUILD_ROOT = $buildRoot
$env:DITUNNEL_LIBXRAY_AAR = Join-Path $AndroidRoot 'libxray\v26.7.28\libxray-android\libXray.aar'
$env:DITUNNEL_ANDROID_AWG_ROOT = Join-Path $AndroidRoot 'amneziawg\native'
$env:TEMP = Join-Path $AndroidRoot 'temp'
$env:TMP = $env:TEMP
$env:JAVA_HOME = $JavaSdkDirectory

New-Item -ItemType Directory -Force -Path $publishDirectory, $releaseRoot, $env:TEMP | Out-Null

& $dotnetPath publish $projectPath `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --output $publishDirectory `
    --property:AndroidSdkDirectory=$env:ANDROID_HOME `
    --property:JavaSdkDirectory=$JavaSdkDirectory `
    -p:AndroidSdkBuildToolsVersion=36.0.0 @publishSigning

if ($LASTEXITCODE -ne 0) {
    throw "Сборка Android завершилась с кодом $LASTEXITCODE"
}

$sourceApk = Join-Path $publishDirectory 'com.divintyinteractive.ditunnel-Signed.apk'
if (-not (Test-Path -LiteralPath $sourceApk -PathType Leaf)) {
    throw "Подписанный APK не найден: $sourceApk"
}
& (Join-Path $PSScriptRoot 'Test-AndroidApk.ps1') -Apk $sourceApk `
    -BuildTools (Join-Path $env:ANDROID_HOME 'build-tools/36.0.0') @apkChecks

Copy-Item -LiteralPath $sourceApk -Destination $destinationApk -Force
$hash = Get-FileHash -LiteralPath $destinationApk -Algorithm SHA256
$digest = $hash.Hash.ToLowerInvariant()
Set-Content -LiteralPath "$destinationApk.sha256" -Value "$digest  $([IO.Path]::GetFileName($destinationApk))" -Encoding ascii

Write-Host "APK: $destinationApk"
Write-Host "SHA256: $($hash.Hash)"
Write-Host "Checksum: $destinationApk.sha256"
