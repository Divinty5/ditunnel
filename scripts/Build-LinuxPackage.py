#!/usr/bin/env python3
"""Build a self-contained Ubuntu 24.04 amd64 .deb without installing anything."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(*args, **kwargs):
    subprocess.run(list(map(str, args)), check=True, **kwargs)


def verify_runtime(directory):
    for name in ("xray", "ditunnel-awg", "geoip.dat", "geosite.dat"):
        if digest(directory / name) != (directory / (name + ".sha256")).read_text().strip():
            raise RuntimeError("Linux runtime checksum mismatch: " + name)
    metadata = json.loads((directory / "XRAY-SOURCE.json").read_text())
    if metadata.get("linuxProcessLookup") != 2 or metadata.get("binarySha256") != digest(directory / "xray"):
        raise RuntimeError("Linux process lookup metadata mismatch")
    for name in ("LICENSE-XRAY", "LICENSE-DITUNNEL", "THIRD-PARTY-NOTICES.txt", "xray-process-source/process_lookup.py"):
        if not (directory / name).is_file():
            raise RuntimeError("Missing runtime source/license: " + name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--test", action="store_true", help="run managed tests and runtime tests")
    parser.add_argument("--runtime-directory", type=Path, help="reuse a previously prepared and verified Linux runtime")
    parser.add_argument("--output", type=Path, help="artifact output directory")
    args = parser.parse_args()
    if sys.platform != "linux" or platform.machine() != "x86_64":
        parser.error("Linux amd64 is required")
    root = Path(__file__).resolve().parent.parent
    version = ET.parse(root / "Directory.Build.props").findtext(".//LinuxVersion")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise RuntimeError("Invalid LinuxVersion")
    output = (args.output or root / "artifacts/linux/release").resolve()
    output.mkdir(parents=True, exist_ok=True)
    dotnet = str(Path(os.environ["DOTNET_ROOT"]) / "dotnet") if os.environ.get("DOTNET_ROOT") else "dotnet"
    runtime = (args.runtime_directory or root / ".tools/linux-runtime/linux-x64").resolve()
    if args.runtime_directory is None:
        run(sys.executable, root / "scripts/Prepare-LinuxRuntime.py", *(["--test"] if args.test else []))
    verify_runtime(runtime)
    build = output / "build"
    property_arg = "-p:DITUNNEL_BUILD_ROOT=" + str(build)
    if args.test:
        for project in ("DiTunnel.Core.Tests", "DiTunnel.Infrastructure.Xray.Tests", "DiTunnel.App.Tests", "DiTunnel.Platform.Linux.Tests"):
            run(dotnet, "test", root / "tests" / project / (project + ".csproj"), "-c", "Release", property_arg, "--disable-build-servers", "-m:1", "-v:minimal")
    # Stage only inside a newly created private build directory. A failed build
    # cannot delete a caller-supplied output or an installed application.
    with tempfile.TemporaryDirectory(prefix="package-", dir=output) as temporary:
        stage = Path(temporary)
        for project, name in (("DiTunnel.Linux", "ui"), ("DiTunnel.NetworkHost.Linux", "host")):
            destination = stage / "usr/lib/ditunnel" / name
            run(dotnet, "publish", root / "src" / project / (project + ".csproj"), "-c", "Release", "-r", "linux-x64",
                "--self-contained", "true", "-p:PublishTrimmed=false", "-p:PublishSingleFile=false", property_arg,
                "--disable-build-servers", "-m:1", "-o", destination, "-v:minimal")
            for pdb in destination.glob("*.pdb"):
                pdb.unlink()
        shutil.copytree(runtime, stage / "usr/lib/ditunnel/runtime")
        files = {
            "di-tunnel": "usr/bin/di-tunnel",
            "di-tunnel.desktop": "usr/share/applications/di-tunnel.desktop",
            "ditunnel-network.service": "usr/lib/systemd/system/ditunnel-network.service",
            "org.divinty5.DiTunnel.Network1.conf": "usr/share/dbus-1/system.d/org.divinty5.DiTunnel.Network1.conf",
            "org.divinty5.ditunnel.policy": "usr/share/polkit-1/actions/org.divinty5.ditunnel.policy",
        }
        for name, relative in files.items():
            target = stage / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(root / "packaging/linux" / name, target)
        icon = stage / "usr/share/pixmaps/di-tunnel.png"
        icon.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root / "icon.png", icon)
        doc = stage / "usr/share/doc/ditunnel"
        doc.mkdir(parents=True)
        shutil.copyfile(root / "LICENSE", doc / "copyright")
        shutil.copyfile(root / "docs/linux-installation.md", doc / "README.md")
        for name in ("linux-vm-testing.md", "linux-protection-stage.md"):
            shutil.copyfile(root / "docs" / name, doc / name)
        # Preserve upstream package license files and notices, including the
        # self-contained .NET runtime's legal notices.
        notices = doc / "third-party"
        manifests = []
        for project in ("DiTunnel.Linux", "DiTunnel.NetworkHost.Linux"):
            assets = json.loads((build / project / "obj/project.assets.json").read_text())
            for key, library in assets["libraries"].items():
                if library.get("type") != "package":
                    continue
                manifests.append(key)
                for folder in assets["packageFolders"]:
                    package = Path(folder) / library["path"]
                    if not package.is_dir():
                        continue
                    for source in package.iterdir():
                        if source.is_file() and ("license" in source.name.lower() or "notice" in source.name.lower() or source.suffix == ".nuspec"):
                            target = notices / library["path"] / source.name
                            target.parent.mkdir(parents=True, exist_ok=True)
                            shutil.copyfile(source, target)
        (doc / "NUGET-DEPENDENCIES.txt").write_text("\n".join(sorted(set(manifests))) + "\n")
        control = stage / "DEBIAN"
        control.mkdir()
        size = sum(path.stat().st_size for path in stage.rglob("*") if path.is_file()) // 1024
        dependencies = "libc6 (>= 2.38), libgcc-s1, libstdc++6, libicu74, libssl3t64, zlib1g, libfontconfig1, libfreetype6, libx11-6, libxcb1, libxext6, libxrender1, libxrandr2, libxi6, libxcursor1, libxfixes3, libgl1, libice6, libsm6, libdbus-1-3, libsecret-tools, gnome-keyring, polkitd, pkexec, dbus, systemd, systemd-resolved, iproute2, nftables, apt"
        (control / "control").write_text(f"Package: ditunnel\nVersion: {version}\nArchitecture: amd64\nSection: net\nPriority: optional\nMaintainer: Di Vinty Interactive <divinty5@users.noreply.github.com>\nInstalled-Size: {size}\nDepends: {dependencies}\nHomepage: https://github.com/Divinty5/ditunnel\nDescription: Di-Tunnel VPN client for Ubuntu 24.04\n Avalonia interface, encrypted profiles, Linux TUN, split tunneling and kill switch.\n Includes self-contained .NET and pinned Xray/AmneziaWG runtimes.\n")
        for name in ("preinst", "postinst", "prerm", "postrm"):
            shutil.copyfile(root / "packaging/linux" / name, control / name)
        for path in stage.rglob("*"):
            if path.is_dir():
                path.chmod(0o755)
            elif path.is_file():
                executable = path.name in ("Di-Tunnel.Linux", "DiTunnel.NetworkHost.Linux", "di-tunnel", "xray", "ditunnel-awg", "preinst", "postinst", "prerm", "postrm")
                path.chmod(0o755 if executable else 0o644)
        run("desktop-file-validate", stage / "usr/share/applications/di-tunnel.desktop")
        stage.chmod(0o755)
        artifact = output / f"Di-Tunnel-{version}-linux-amd64.deb"
        run("dpkg-deb", "--root-owner-group", "-Zxz", "-z6", "--build", stage, artifact)
    (output / (artifact.name + ".sha256")).write_text(digest(artifact) + "  " + artifact.name + "\n")
    run(sys.executable, root / "scripts/Test-LinuxPackage.py", artifact)
    print("Linux package:", artifact, flush=True)


if __name__ == "__main__":
    main()
