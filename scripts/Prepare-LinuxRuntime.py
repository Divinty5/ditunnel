#!/usr/bin/env python3
"""Prepare pinned Linux amd64 runtimes locally, without changing the system."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
import zipfile


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def download(spec, cache):
    archive = cache / spec["asset"]
    if archive.is_file() and digest(archive) == spec["sha256"]:
        return archive
    for attempt in range(3):
        partial = None
        try:
            with tempfile.NamedTemporaryFile(dir=cache, suffix=".partial", delete=False) as output:
                partial = Path(output.name)
                with urllib.request.urlopen(spec["url"], timeout=60) as response:
                    shutil.copyfileobj(response, output)
            if digest(partial) != spec["sha256"]:
                raise RuntimeError("Runtime archive checksum mismatch")
            partial.replace(archive)
            return archive
        except OSError:
            if attempt == 2:
                raise
            time.sleep(2)
        finally:
            if partial:
                partial.unlink(missing_ok=True)
    raise RuntimeError("Download failed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--test", action="store_true")
    args = parser.parse_args()
    if sys.platform != "linux" or platform.machine() != "x86_64":
        parser.error("Linux amd64 is required")
    root = Path(__file__).resolve().parent.parent
    manifest = json.loads((root / "eng/linux-runtime.json").read_text())
    awg = json.loads((root / "eng/amneziawg-runtime.json").read_text())
    if manifest["go"]["version"] != awg["goVersion"]:
        raise RuntimeError("Go manifest versions disagree")
    tools = root / ".tools/linux-runtime"
    build_cache = Path(os.environ.get("DITUNNEL_LINUX_CACHE", str(tools))).expanduser().resolve()
    build_cache.mkdir(parents=True, exist_ok=True)
    cache, output = tools / "downloads", tools / "linux-x64"
    cache.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(download(manifest["xray"], cache)) as archive:
        # Copy named release files only, never use archive paths as destinations.
        for name in ("xray", "geoip.dat", "geosite.dat", "LICENSE"):
            target = output / ("LICENSE-XRAY" if name == "LICENSE" else name)
            with archive.open(name) as source, target.open("wb") as dest:
                shutil.copyfileobj(source, dest)
    (output / "xray").chmod(0o755)
    go_root = build_cache / manifest["go"]["version"]
    go_root.mkdir(exist_ok=True)
    go_archive = download(manifest["go"], cache)
    marker = go_root / "archive.sha256"
    if not marker.is_file() or marker.read_text().strip() != manifest["go"]["sha256"]:
        with tarfile.open(go_archive, "r:gz") as archive:
            archive.extractall(go_root, filter="data")
        marker.write_text(manifest["go"]["sha256"] + "\n")
    go = go_root / "go/bin/go"
    env = os.environ.copy()
    env.update(GOTOOLCHAIN="local", CGO_ENABLED="0", GOOS="linux", GOARCH="amd64",
               GOCACHE=str(build_cache / "build-cache"), GOMODCACHE=str(build_cache / "module-cache"), GOPATH=str(build_cache / "workspace"))
    env["DITUNNEL_XRAY"] = str(output / "xray")
    env["XRAY_LOCATION_ASSET"] = str(output)
    source = root / "runtime/amneziawg"

    def run(*arguments, capture=False, cwd=None):
        return subprocess.run([str(go), *arguments], cwd=cwd or source, env=env, check=True,
                              text=True, stdout=subprocess.PIPE if capture else None).stdout

    run("mod", "download")
    kernel = json.loads(run("list", "-m", "-json", awg["module"], capture=True))
    if kernel["Version"] != awg["moduleVersion"] or kernel["Sum"] != awg["moduleSum"]:
        raise RuntimeError("AmneziaWG source version/checksum mismatch")
    run("mod", "verify")
    # The release binary misses wildcard UDP and IPv6 process ownership on Linux.
    # Build the same pinned source with our reviewed, hash-guarded local fix.
    xray = manifest["xray"]
    module = json.loads(run("mod", "download", "-json", "github.com/xtls/xray-core@" + xray["moduleVersion"], capture=True, cwd=build_cache))
    if module["Version"] != xray["moduleVersion"] or module["Sum"] != xray["moduleSum"]:
        raise RuntimeError("Xray source version/checksum mismatch")
    sys.path.insert(0, str(root / "runtime/xray-linux"))
    import process_lookup
    with tempfile.TemporaryDirectory(prefix="xray-source-", dir=build_cache) as temporary:
        xray_source = Path(temporary) / "source"
        shutil.copytree(module["Dir"], xray_source)
        process_lookup.apply(xray_source, xray)
        run("mod", "download", cwd=xray_source)
        run("mod", "verify", cwd=xray_source)
        if args.test:
            run("test", "./common/net", "./app/router", "-count=1", "-timeout=90s", cwd=xray_source)
        run("build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-ldflags=-s -w -buildid= -X github.com/xtls/xray-core/core.build=Di-Tunnel-linux-process-v2", "-o", str(output / "xray"), "./main", cwd=xray_source)
        (output / "XRAY-DEPENDENCIES.txt").write_text(run("list", "-m", "-f", "{{.Path}} {{.Version}} {{.Sum}}", "all", capture=True, cwd=xray_source))
        xray_modules = run("list", "-m", "-f", "{{.Dir}}", "all", capture=True, cwd=xray_source).splitlines()
        modified = output / "xray-process-source"
        modified.mkdir(exist_ok=True)
        shutil.copyfile(xray_source / "common/net/find_process_linux.go", modified / "find_process_linux.go")
        shutil.copyfile(xray_source / "app/router/condition.go", modified / "condition.go")
        shutil.copyfile(root / "runtime/xray-linux/process_lookup.py", modified / "process_lookup.py")
        shutil.copyfile(root / "runtime/xray-linux/process_lookup_test.go", modified / "process_lookup_test.go")
        shutil.copyfile(root / "runtime/xray-linux/process_routing_test.go", modified / "process_routing_test.go")
    (output / "XRAY-SOURCE.json").write_text(json.dumps({
        "linuxProcessLookup": 2, "binarySha256": digest(output / "xray"),
        "moduleVersion": xray["moduleVersion"], "moduleSum": xray["moduleSum"],
        "patchSha256": digest(root / "runtime/xray-linux/process_lookup.py"),
        "upstream": "https://github.com/XTLS/Xray-core/tree/v" + xray["version"],
        "license": "MPL-2.0; see LICENSE-XRAY; modified file in xray-process-source",
    }, indent=2) + "\n")
    if args.test:
        run("test", "./...", "-count=1", "-timeout=90s")
        run("vet", "./...")
    run("build", "-mod=readonly", "-trimpath", "-ldflags=-s -w -buildid=", "-o", str(output / "ditunnel-awg"), ".")
    (output / "DEPENDENCIES.txt").write_text(run("list", "-m", "-f", "{{.Path}} {{.Version}} {{.Sum}}", "all", capture=True))
    shutil.copyfile(source / "go.sum", output / "go.sum")
    shutil.copyfile(root / "LICENSE", output / "LICENSE-DITUNNEL")
    notices = ["Di-Tunnel AmneziaWG bridge: MIT; see LICENSE-DITUNNEL", (go_root / "go/LICENSE").read_text()]
    directories = run("list", "-m", "-f", "{{.Dir}}", "all", capture=True).splitlines() + xray_modules
    for directory in dict.fromkeys(directories):
        if not directory or not Path(directory).is_dir() or Path(directory).resolve() == source.resolve():
            continue
        for license_file in Path(directory).iterdir():
            if license_file.is_file() and license_file.name.upper().split(".")[0] in ("LICENSE", "COPYING", "NOTICE"):
                notices.append(f"\n--- {license_file.parent.name}/{license_file.name} ---\n" + license_file.read_text(errors="replace"))
    (output / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(notices))
    for name in ("xray", "ditunnel-awg", "geoip.dat", "geosite.dat"):
        (output / (name + ".sha256")).write_text(digest(output / name) + "\n")
    print(f"Linux runtime: {output}")


if __name__ == "__main__":
    main()
