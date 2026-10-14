[CmdletBinding()]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $repositoryRoot '.tools\shell-icon\Release'
$sourceDirectory = Join-Path $repositoryRoot 'src\DiTunnel.ShellIcon'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ Build Tools and CMake to build the Windows taskbar component.' }
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'Visual Studio x64 C++ build tools were not found.' }
$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvars64.bat'
$cmake = (Get-Command cmake -ErrorAction Stop).Source
New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null
$testOption = if ($Test) { 'ON' } else { 'OFF' }
$batch = Join-Path $buildDirectory 'build.cmd'
@"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
"$cmake" -S "$sourceDirectory" -B "$buildDirectory" -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release -DDITUNNEL_SHELL_TESTS=$testOption
if errorlevel 1 exit /b 1
"$cmake" --build "$buildDirectory"
if errorlevel 1 exit /b 1
"@ | Set-Content -LiteralPath $batch -Encoding ascii
& $env:ComSpec /d /c "`"$batch`""
if ($LASTEXITCODE -ne 0) { throw 'Taskbar component build failed.' }
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'vendor\minhook\LICENSE.txt') -Destination (Join-Path $buildDirectory 'LICENSE-MINHOOK.txt')
Copy-Item -LiteralPath (Join-Path $sourceDirectory 'THIRD-PARTY-NOTICES.txt') -Destination $buildDirectory
if ($Test) {
    Push-Location $buildDirectory
    try {
        & (Join-Path $buildDirectory 'DiTunnel.ShellIcon.SelfTest.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Taskbar component self-test failed.' }
    }
    finally { Pop-Location }
}
