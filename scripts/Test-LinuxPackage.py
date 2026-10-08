#!/usr/bin/env python3
"""Read-only validation of the Linux release artifact and its checksum."""
import argparse
import hashlib
from pathlib import Path
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    args = parser.parse_args()
    version = ET.parse(Path(__file__).resolve().parent.parent / "Directory.Build.props").findtext(".//LinuxVersion")
    name = f"Di-Tunnel-{version}-linux-amd64.deb"
    if args.package.name != name:
        raise RuntimeError("Unexpected release filename")
    with args.package.open("rb") as file:
        digest = hashlib.file_digest(file, "sha256").hexdigest()
    if Path(str(args.package) + ".sha256").read_text().strip() != digest + "  " + name:
        raise RuntimeError("Package checksum mismatch")
    for field, value in (("Package", "ditunnel"), ("Version", version), ("Architecture", "amd64")):
        if subprocess.check_output(["dpkg-deb", "--field", args.package, field], text=True).strip() != value:
            raise RuntimeError("Unexpected package " + field)
    binaries = {"./usr/lib/ditunnel/ui/Di-Tunnel.Linux", "./usr/lib/ditunnel/host/DiTunnel.NetworkHost.Linux", "./usr/lib/ditunnel/runtime/xray", "./usr/lib/ditunnel/runtime/ditunnel-awg"}
    found = set()
    # Stream the payload without extracting paths into the caller's filesystem.
    with subprocess.Popen(["dpkg-deb", "--fsys-tarfile", args.package], stdout=subprocess.PIPE) as process:
        with tarfile.open(fileobj=process.stdout, mode="r|") as archive:
            for member in archive:
                if member.uid != 0 or member.gid != 0 or member.mode & 0o022:
                    raise RuntimeError("Unexpected payload ownership or write permissions")
                if member.isdir() and member.mode & 0o555 != 0o555:
                    raise RuntimeError("Payload directories must remain traversable")
                if member.name in binaries:
                    if not member.isfile() or member.mode & 0o111 != 0o111 or archive.extractfile(member).read(4) != b"\x7fELF":
                        raise RuntimeError("Missing executable Linux ELF: " + member.name)
                    found.add(member.name)
        if process.wait() != 0:
            raise RuntimeError("Cannot read payload")
    if found != binaries:
        raise RuntimeError("Incomplete executable payload")
    with tempfile.TemporaryDirectory(prefix="ditunnel-control-") as directory:
        subprocess.run(["dpkg-deb", "--control", args.package, directory], check=True)
        for name in ("preinst", "postinst", "prerm", "postrm"):
            subprocess.run(["/bin/sh", "-n", Path(directory) / name], check=True)
    print("PASS_LINUX_PACKAGE_VERSION_CHECKSUM_ELF_OWNERSHIP_MAINTAINER_SCRIPTS", flush=True)


if __name__ == "__main__":
    main()
