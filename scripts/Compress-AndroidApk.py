#!/usr/bin/env python3
"""Compress the executable ELF libraries of a Release APK before it is signed."""
import argparse
from copy import copy
from pathlib import Path
import tempfile
from zipfile import ZIP_DEFLATED, ZIP_STORED, ZipFile


def executable_libraries(archive):
    libraries = []
    stores = 0
    for entry in archive.infolist():
        path = entry.filename.split("/")
        if len(path) != 3 or path[0] != "lib" or not path[2].endswith(".so"):
            continue
        name = path[2]
        # Data-only ELF wrappers used by the runtime keep their original layout.
        assembly_store = name == "libassembly-store.so" or (
            name.startswith("libassemblies.") and name.endswith(".blob.so")
        )
        if assembly_store or name in ("libarc.bin.so", "libxamarin-app.so"):
            if entry.compress_type != ZIP_STORED:
                raise ValueError(f"Runtime data must be stored uncompressed: {entry.filename}")
            stores += assembly_store
            continue
        with archive.open(entry) as library:
            if library.read(4) != b"\x7fELF":
                raise ValueError(f"Invalid native library: {entry.filename}")
        libraries.append(entry)
    if not stores or not libraries:
        raise ValueError("APK is missing its assembly store or native libraries")
    return libraries


def compress_apk(apk, check):
    scratch = None
    try:
        with ZipFile(apk) as original:
            entries = original.infolist()
            if len({entry.filename for entry in entries}) != len(entries):
                raise ValueError("APK contains duplicate entries")
            libraries = executable_libraries(original)
            pending = {entry.filename for entry in libraries if entry.compress_type != ZIP_DEFLATED}
            if check and pending:
                raise ValueError("APK has uncompressed executable libraries: " + ", ".join(sorted(pending)))
            if not pending:
                return

            with tempfile.NamedTemporaryFile(dir=apk.parent, suffix=".apk", delete=False) as file:
                scratch = Path(file.name)
            with ZipFile(scratch, "w") as output:
                output.comment = original.comment
                for entry in entries:
                    destination = copy(entry)
                    if entry.filename in pending:
                        destination.compress_type = ZIP_DEFLATED
                    output.writestr(destination, original.read(entry), compresslevel=9)
        scratch.replace(apk)
        print(f"Compressed {len(pending)} native libraries in {apk.name}")
    finally:
        if scratch is not None:
            scratch.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk", type=Path)
    parser.add_argument("--check", action="store_true", help="verify the compression profile without writing")
    args = parser.parse_args()
    compress_apk(args.apk, args.check)


if __name__ == "__main__":
    main()
