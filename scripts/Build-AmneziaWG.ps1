[CmdletBinding()]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'eng\amneziawg-runtime.json') -Raw | ConvertFrom-Json
$toolRoot = Join-Path $repositoryRoot '.tools\go'
$go = Join-Path $toolRoot "$($manifest.goVersion)\go\bin\go.exe"
if (-not (Test-Path -LiteralPath $go)) {
    New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
    $archive = Join-Path $toolRoot $manifest.goArchive
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $manifest.goUrl -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.goSha256) { throw 'Go archive checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolRoot $manifest.goVersion) -Force
}
$env:GOTOOLCHAIN = 'local'
$env:GOCACHE = Join-Path $repositoryRoot '.tools\go-build-cache'
$env:GOMODCACHE = Join-Path $repositoryRoot '.tools\go-mod-cache'
$env:GOPATH = Join-Path $repositoryRoot '.tools\go-workspace'
$env:CGO_ENABLED = '0'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
$output = Join-Path $repositoryRoot ".tools\amneziawg\$($manifest.bridgeVersion)\windows-x64"
New-Item -ItemType Directory -Force -Path $output | Out-Null
Push-Location (Join-Path $repositoryRoot 'runtime\amneziawg')
try {
    & $go mod download
    if ($LASTEXITCODE -ne 0) { throw 'AmneziaWG dependency restore failed.' }
    $kernel = (& $go list -m -json $manifest.module | Out-String | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $kernel.Version -ne $manifest.moduleVersion -or $kernel.Sum -ne $manifest.moduleSum) { throw 'AmneziaWG source version/checksum mismatch.' }
    & $go mod verify
    if ($LASTEXITCODE -ne 0) { throw 'AmneziaWG dependency checksums failed.' }
    if ($Test) {
        & $go test ./... -count=1 -timeout=60s
        if ($LASTEXITCODE -ne 0) { throw 'AmneziaWG bridge tests failed.' }
        & $go vet ./...
        if ($LASTEXITCODE -ne 0) { throw 'AmneziaWG bridge analysis failed.' }
    }
    & $go build -mod=readonly -trimpath -ldflags '-s -w -buildid=' -o (Join-Path $output 'ditunnel-awg.exe') .
    if ($LASTEXITCODE -ne 0) { throw 'AmneziaWG bridge build failed.' }
    $modules = & $go list -m -f '{{.Path}} {{.Version}} {{.Sum}}' all | Out-String
    # Preserve the complete dependency list and Go checksum lock in release notices.
    Set-Content -LiteralPath (Join-Path $output 'DEPENDENCIES.txt') -Value $modules -Encoding utf8
    $oldDependencyList = Join-Path $output 'DEPENDENCIES.json'
    if (Test-Path -LiteralPath $oldDependencyList) { Remove-Item -LiteralPath $oldDependencyList -Force }
    Copy-Item -LiteralPath 'go.sum' -Destination $output
    $dependencyNotice = @('Di-Tunnel AmneziaWG bridge: MIT (see LICENSE-DITUNNEL)', 'AmneziaWG source: https://github.com/amnezia-vpn/amneziawg-go', "Pinned commit: $($manifest.commit)")
    $dependencyNotice += "`n--- Go toolchain LICENSE ---`n" + (Get-Content -LiteralPath (Join-Path (Split-Path -Parent (Split-Path -Parent $go)) 'LICENSE') -Raw)
    foreach ($license in (Get-ChildItem -LiteralPath $env:GOMODCACHE -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)(\..*)?$' })) {
        $dependencyNotice += "`n--- $($license.FullName.Substring($env:GOMODCACHE.Length + 1)) ---`n"
        $dependencyNotice += Get-Content -LiteralPath $license.FullName -Raw
    }
    Set-Content -LiteralPath (Join-Path $output 'THIRD-PARTY-NOTICES.txt') -Value $dependencyNotice -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $output 'LICENSE-DITUNNEL')
    $hash = (Get-FileHash -LiteralPath (Join-Path $output 'ditunnel-awg.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $output 'ditunnel-awg.exe.sha256') -Value $hash -Encoding ascii
    Write-Output "AmneziaWG bridge: $output"
} finally { Pop-Location }
