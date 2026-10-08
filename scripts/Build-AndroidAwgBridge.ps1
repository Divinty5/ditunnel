[CmdletBinding()]
param([string]$EnvironmentRoot)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($EnvironmentRoot)) {
    $EnvironmentRoot = Join-Path $repositoryRoot '.tools/android-build-local/environment'
}
$environmentRoot = [IO.Path]::GetFullPath($EnvironmentRoot)
[xml]$buildProps = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$minimumApi = [int]($buildProps.Project.PropertyGroup.AndroidMinimumApiLevel | Where-Object { $_ } | Select-Object -First 1)
if ($minimumApi -lt 21) { throw 'AndroidMinimumApiLevel должен быть не ниже 21 для ARM64.' }
$manifest = Get-Content (Join-Path $repositoryRoot 'eng/amneziawg-runtime.json') -Raw | ConvertFrom-Json
$androidManifest = Get-Content (Join-Path $repositoryRoot 'eng/android-runtime.json') -Raw | ConvertFrom-Json
$go = Join-Path $repositoryRoot ".tools/go/$($manifest.goVersion)/go/bin/go.exe"
if (-not (Test-Path -LiteralPath $go)) { throw 'Сначала подготовьте закреплённый Go с помощью scripts/Build-AmneziaWG.ps1.' }
$ndkRoot = Join-Path $environmentRoot "android-ndk-$($androidManifest.ndk.version)"
$compilerRoot = Join-Path $ndkRoot 'toolchains/llvm/prebuilt/windows-x86_64/bin'
if (-not (Test-Path -LiteralPath (Join-Path $compilerRoot 'clang.exe'))) {
    $archive = Join-Path $environmentRoot "downloads/android-ndk-$($androidManifest.ndk.version)-windows.zip"
    if (-not (Test-Path -LiteralPath $archive)) {
        & curl.exe --fail --location --retry 2 --output $archive $androidManifest.ndk.url
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось загрузить Android NDK.' }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash.ToLowerInvariant() -ne $androidManifest.ndk.sha1) { throw 'Контрольная сумма NDK не совпала.' }
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $environmentRoot, $true)
}
$outputRoot = Join-Path $environmentRoot 'amneziawg/native'
$variables = @{
    GOTOOLCHAIN='local'; GOCACHE=(Join-Path $repositoryRoot '.tools/go-build-cache')
    GOMODCACHE=(Join-Path $repositoryRoot '.tools/go-mod-cache'); GOPATH=(Join-Path $repositoryRoot '.tools/go-workspace')
    CGO_ENABLED='1'; GOOS='android'; GOARCH='arm64'; CC=''
    CGO_LDFLAGS='-Wl,-z,max-page-size=16384 -Wl,-Bsymbolic'
}
$previous = @{}
foreach ($name in $variables.Keys) { $previous[$name]=[Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name,$variables[$name]) }
Push-Location (Join-Path $repositoryRoot 'runtime/amneziawg')
try {
    $module = (& $go list -m -json $manifest.module | Out-String | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $module.Version -ne $manifest.moduleVersion -or $module.Sum -ne $manifest.moduleSum) { throw 'Закреплённая версия ядра AWG не совпала.' }
    & $go mod verify
    if ($LASTEXITCODE -ne 0) { throw 'Проверка зависимостей AmneziaWG не прошла.' }
    foreach ($target in @(@{Abi='arm64-v8a';Arch='arm64';Triple="aarch64-linux-android$minimumApi"}, @{Abi='x86_64';Arch='amd64';Triple="x86_64-linux-android$minimumApi"})) {
        $env:GOARCH = $target.Arch
        $env:CC = "$(Join-Path $compilerRoot 'clang.exe') --target=$($target.Triple)"
        $directory = Join-Path $outputRoot $target.Abi
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        & $go build -mod=readonly -trimpath -buildmode=c-shared -ldflags '-s -w -buildid=' -o (Join-Path $directory 'libditunnel-awg.so') .
        if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать AWG для $($target.Abi)." }
        (Get-FileHash (Join-Path $directory 'libditunnel-awg.so') -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content (Join-Path $directory 'libditunnel-awg.so.sha256') -Encoding ascii
    }
    & $go list -m -f '{{.Path}} {{.Version}} {{.Sum}}' all | Set-Content (Join-Path $outputRoot 'DEPENDENCIES.txt') -Encoding utf8
    Write-Output "Android AWG SOCKS bridge: $outputRoot"
} finally {
    Pop-Location
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name,$previous[$name]) }
}
