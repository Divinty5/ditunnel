#!/usr/bin/env bash
set -euo pipefail
repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"
dotnet_command="${DOTNET_ROOT:+$DOTNET_ROOT/}dotnet"
configuration="${CONFIGURATION:-Release}"
build_root="${DITUNNEL_BUILD_ROOT:-$repository_root/artifacts/linux/build}"
runtime_root="$repository_root/.tools/linux-runtime/linux-x64"
if [[ ! -x "$runtime_root/xray" || ! -x "$runtime_root/ditunnel-awg" ]]; then
    echo 'Run python3 scripts/Prepare-LinuxRuntime.py --test first.' >&2
    exit 1
fi
"$dotnet_command" build src/DiTunnel.NetworkHost.Linux/DiTunnel.NetworkHost.Linux.csproj \
    --configuration "$configuration" -p:DITUNNEL_BUILD_ROOT="$build_root" --disable-build-servers -m:1 --verbosity minimal
"$dotnet_command" build tests/DiTunnel.Linux.Smoke/DiTunnel.Linux.Smoke.csproj \
    --configuration "$configuration" -p:DITUNNEL_BUILD_ROOT="$build_root" --disable-build-servers -m:1 --verbosity minimal
dbus-run-session -- "$dotnet_command" "$build_root/DiTunnel.Linux.Smoke/bin/$configuration/net10.0/DiTunnel.Linux.Smoke.dll" \
    "$dotnet_command" "$build_root/DiTunnel.NetworkHost.Linux/bin/$configuration/net10.0/DiTunnel.NetworkHost.Linux.dll" "$runtime_root"
