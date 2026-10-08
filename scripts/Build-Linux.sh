#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"
dotnet_command="${DOTNET_ROOT:+$DOTNET_ROOT/}dotnet"
configuration="${CONFIGURATION:-Release}"
build_root="${DITUNNEL_BUILD_ROOT:-$repository_root/artifacts/linux/build}"

if [[ "${1:-}" != "" && "${1:-}" != "--test" ]]; then
    echo "Usage: bash scripts/Build-Linux.sh [--test]" >&2
    exit 2
fi

"$dotnet_command" build src/DiTunnel.Linux/DiTunnel.Linux.csproj \
    --configuration "$configuration" -p:DITUNNEL_BUILD_ROOT="$build_root" --disable-build-servers -m:1 --verbosity minimal

"$dotnet_command" build src/DiTunnel.NetworkHost.Linux/DiTunnel.NetworkHost.Linux.csproj \
    --configuration "$configuration" -p:DITUNNEL_BUILD_ROOT="$build_root" --disable-build-servers -m:1 --verbosity minimal

if [[ "${1:-}" == "--test" ]]; then
    for project in DiTunnel.Core.Tests DiTunnel.Infrastructure.Xray.Tests DiTunnel.App.Tests DiTunnel.Platform.Linux.Tests; do
        "$dotnet_command" test "tests/$project/$project.csproj" \
            --configuration "$configuration" -p:DITUNNEL_BUILD_ROOT="$build_root" --disable-build-servers -m:1 --verbosity minimal
    done
fi
