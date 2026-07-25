#!/usr/bin/env python3
################################################################################
# Packages nettraceParser as self-contained, per-OS release assets matching
# roslynHelper's exact archive layout and naming convention (verified against
# the real roslynHelper-osx-x64.tar.gz release asset):
#
#   nettraceParser-{osName}-x64.tar.gz
#     nettraceParser/                    <- single top-level folder
#       nettraceParser                   <- self-contained apphost executable
#       nettraceParser.dll
#       nettraceParser.deps.json
#       nettraceParser.runtimeconfig.json
#       <dependency DLLs...>
#
# This is a standard (not single-file) self-contained publish - roslynHelper's
# own release assets aren't single-file either, so this matches on purpose.
#
# DependencySetup.ts extracts a downloaded archive directly into a folder it
# already named after the tool (e.g. ".../nettraceParser/"), so the archive's
# own top-level "nettraceParser/" folder becomes the doubled path
# ".../nettraceParser/nettraceParser/nettraceParser" the extension expects -
# do not flatten or rename that top-level folder.
#
# Usage: ./pack.py [output-dir] [rid...]
#   output-dir defaults to ./artifacts
#   rid... defaults to all three below; pass specific RIDs to build a subset,
#   e.g. ./pack.py ./artifacts osx-x64
################################################################################

import argparse
import shutil
import subprocess
import sys
import tarfile
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT = SCRIPT_DIR / "nettraceParser.csproj"

# rid -> osName, matching roslynHelper-{osName}-x64.tar.gz / gcEventListener-{osName}.tar.gz.
ALL_TARGETS = {
    "osx-x64": "osx",
    "linux-x64": "linux",
    "win-x64": "win",
}


def publish(rid: str, publish_dir: Path) -> None:
    print(f"== Publishing {rid} (self-contained) ==")
    subprocess.run(
        [
            "dotnet", "publish", str(PROJECT),
            "-c", "Release",
            "-r", rid,
            "--self-contained", "true",
            "-p:PublishSingleFile=false",
            "-o", str(publish_dir),
        ],
        check=True,
    )


def package(publish_dir: Path, archive_path: Path) -> None:
    print(f"== Packaging {archive_path.name} ==")
    with tarfile.open(archive_path, "w:gz") as tar:
        tar.add(publish_dir, arcname="nettraceParser")

    size_mb = archive_path.stat().st_size / (1024 * 1024)
    print(f"Created {archive_path} ({size_mb:.1f} MB)\n")


def main() -> int:
    parser = argparse.ArgumentParser(description="Package nettraceParser release archives.")
    parser.add_argument("output_dir", nargs="?", default=str(SCRIPT_DIR / "artifacts"))
    parser.add_argument("rids", nargs="*", help="Specific RIDs to build (default: osx-x64, linux-x64, win-x64)")
    args = parser.parse_args()

    if args.rids:
        unknown = [rid for rid in args.rids if rid not in ALL_TARGETS]
        if unknown:
            print(f"Unknown RID(s): {', '.join(unknown)}. Supported: {', '.join(ALL_TARGETS)}", file=sys.stderr)
            return 1
        targets = {rid: ALL_TARGETS[rid] for rid in args.rids}
    else:
        targets = ALL_TARGETS

    output_dir = Path(args.output_dir)
    if output_dir.exists():
        shutil.rmtree(output_dir)
    output_dir.mkdir(parents=True)

    publish_root = output_dir / "publish"

    for rid, os_name in targets.items():
        publish_dir = publish_root / rid / "nettraceParser"
        archive_path = output_dir / f"nettraceParser-{os_name}-x64.tar.gz"

        publish(rid, publish_dir)
        package(publish_dir, archive_path)

    shutil.rmtree(publish_root)

    print("Done. Ready to upload, e.g.:")
    print(f"  gh release upload <tag> {output_dir}/*.tar.gz")
    print()
    for entry in sorted(output_dir.iterdir()):
        print(f"  {entry.name}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
