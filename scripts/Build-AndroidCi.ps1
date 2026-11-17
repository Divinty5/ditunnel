[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$AndroidSdkDirectory,
    [Parameter(Mandatory)][string]$JavaSdkDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runtime = Get-Content -Raw (Join-Path $root 'eng/android-runtime.json') | ConvertFrom-Json
$runtimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
$libXrayDirectory = Join-Path $runtimeDirectory 'libxray'
$aar = Join-Path $libXrayDirectory $runtime.libXray.aar
$archive = Join-Path $runtimeDirectory $runtime.libXray.asset
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $aar -PathType Leaf)) {
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        & curl.exe --fail --location --retry 3 --remove-on-error --output $archive $runtime.libXray.url
        if ($LASTEXITCODE -ne 0) { throw 'Could not download libXray.' }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $runtime.libXray.sha256) {
        Remove-Item -LiteralPath $archive -Force
        throw 'libXray archive SHA-256 mismatch.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $libXrayDirectory -Force
}

& (Join-Path $PSScriptRoot 'Install-Xray.ps1') -ManifestPath (Join-Path $root 'eng/xray-android-geodata-version.json')
& (Join-Path $PSScriptRoot 'Build-AmneziaWG.ps1')
& (Join-Path $PSScriptRoot 'Build-AndroidAwgBridge.ps1') -EnvironmentRoot $runtimeDirectory

$output = Join-Path $root 'artifacts/android/ci'
$publishDirectory = Join-Path $output 'publish'
$buildTools = Join-Path $AndroidSdkDirectory 'build-tools/36.0.0'
$settingsDirectory = Join-Path $runtimeDirectory 'android-app-settings'
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
$env:JAVA_HOME = $JavaSdkDirectory
$env:DITUNNEL_LIBXRAY_AAR = $aar
$env:DITUNNEL_ANDROID_AWG_ROOT = Join-Path $runtimeDirectory 'amneziawg/native'

dotnet publish (Join-Path $root 'src/DiTunnel.Android/DiTunnel.Android.csproj') -c Release -r android-arm64 -o $publishDirectory `
    "-p:AndroidSdkDirectory=$AndroidSdkDirectory" "-p:JavaSdkDirectory=$JavaSdkDirectory" `
    -p:AndroidSdkBuildToolsVersion=36.0.0 -p:AndroidKeyStore=false "-p:AppSettingsDirectory=$settingsDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Android publish failed.' }

$project = ([xml](Get-Content -Raw (Join-Path $root 'src/DiTunnel.Android/DiTunnel.Android.csproj'))).Project.PropertyGroup
$appId = $project.ApplicationId
$signedApk = Join-Path $publishDirectory "$appId-Signed.apk"
& (Join-Path $PSScriptRoot 'Test-AndroidApk.ps1') -Apk $signedApk -BuildTools $buildTools

$properties = ([xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup
$version = ([string]$properties.AndroidVersion).Trim()
$name = "Di-Tunnel-$version-arm64-ci.apk"
$apk = Join-Path $output $name
Copy-Item -LiteralPath $signedApk -Destination $apk -Force
$hash = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$apk.sha256" -Value "$hash  $name" -Encoding ascii
