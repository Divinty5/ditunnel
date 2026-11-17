[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$BuildTools,
    [string]$SigningCertificateSha256
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apkPath = (Get-Item -LiteralPath $Apk).FullName
$properties = ([xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup
$project = ([xml](Get-Content -Raw (Join-Path $root 'src/DiTunnel.Android/DiTunnel.Android.csproj'))).Project.PropertyGroup
$version = ([string]$properties.AndroidVersion).Trim()
$minimumSdk = ([string]$properties.AndroidMinimumApiLevel).Trim()
$targetSdk = ([string]$project.TargetFramework -split '-android')[1].Split('.')[0]

$badging = & (Join-Path $BuildTools 'aapt.exe') dump badging $apkPath
if ($LASTEXITCODE -ne 0) { throw 'Could not read APK metadata.' }
foreach ($expected in @(
    "name='$($project.ApplicationId)'", "versionCode='$($project.ApplicationVersion)'",
    "versionName='$version'", "sdkVersion:'$minimumSdk'",
    "targetSdkVersion:'$targetSdk'"
)) {
    if (-not ($badging -cmatch [regex]::Escape($expected))) { throw "Missing APK metadata: $expected" }
}
if (-not ($badging -cmatch "^native-code: 'arm64-v8a'\s*$")) { throw 'Expected only the arm64-v8a ABI.' }

$archive = [IO.Compression.ZipFile]::OpenRead($apkPath)
try {
    foreach ($entry in @('assets/geoip.dat', 'assets/geosite.dat', 'lib/arm64-v8a/libgojni.so', 'lib/arm64-v8a/libditunnel-awg.so')) {
        if (-not $archive.GetEntry($entry)) { throw "Missing APK entry: $entry" }
    }
} finally {
    $archive.Dispose()
}

& (Join-Path $BuildTools 'zipalign.exe') -c -P 16 4 $apkPath
if ($LASTEXITCODE -ne 0) { throw 'APK zipalign verification failed.' }

$signature = & (Join-Path $BuildTools 'apksigner.bat') verify --verbose --print-certs $apkPath
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
$signature | Write-Output
if ($PSBoundParameters.ContainsKey('SigningCertificateSha256')) {
    $certificate = ($SigningCertificateSha256 -replace '[:\s]', '').ToLowerInvariant()
    $signers = @($signature -match '^Signer #\d+ certificate SHA-256 digest: ')
    if ($certificate -notmatch '^[a-f0-9]{64}$' -or $signers.Count -ne 1 -or
        $signers[0] -ne "Signer #1 certificate SHA-256 digest: $certificate") {
        throw 'APK is not signed with the configured release certificate.'
    }
}
