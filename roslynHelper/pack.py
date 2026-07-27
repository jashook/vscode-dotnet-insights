#!/usr/bin/env python3
################################################################################
# Packages roslynHelper as self-contained, per-OS/arch release assets -
# mirrors nettraceParser/pack.py exactly (see that file's header for the
# archive-layout rationale, copied here verbatim):
#
#   roslynHelper-{osName}-{arch}.tar.gz
#     roslynHelper/                      <- single top-level folder
#       roslynHelper                     <- self-contained apphost executable
#       roslynHelper.dll
#       roslynHelper.deps.json
#       roslynHelper.runtimeconfig.json
#       <dependency DLLs...>
#
# DependencySetup.ts extracts a downloaded archive directly into a folder it
# already named after the tool (e.g. ".../roslynHelper/"), so the archive's
# own top-level "roslynHelper/" folder becomes the doubled path
# ".../roslynHelper/roslynHelper/roslynHelper" the extension expects - do
# not flatten or rename that top-level folder.
#
# Usage: ./pack.py [output-dir] [rid...]
#   output-dir defaults to ./artifacts
#   rid... defaults to all targets below; pass specific RIDs to build a
#   subset, e.g. ./pack.py ./artifacts osx-arm64
################################################################################

import argparse
import shutil
import subprocess
import sys
import tarfile
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT = SCRIPT_DIR / "roslynHelper.csproj"

# rid -> (osName, arch), matching nettraceParser-{osName}-{arch}.tar.gz /
# gcEventListener-{osName}-{arch}.tar.gz. arch is "x64" or "arm64" -
# DependencySetup.ts picks between them via process.arch at download time.
ALL_TARGETS = {
    "osx-x64": ("osx", "x64"),
    "osx-arm64": ("osx", "arm64"),
    "linux-x64": ("linux", "x64"),
    "linux-arm64": ("linux", "arm64"),
    "win-x64": ("win", "x64"),
    "win-arm64": ("win", "arm64"),
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
        tar.add(publish_dir, arcname="roslynHelper")

    size_mb = archive_path.stat().st_size / (1024 * 1024)
    print(f"Created {archive_path} ({size_mb:.1f} MB)\n")


def main() -> int:
    parser = argparse.ArgumentParser(description="Package roslynHelper release archives.")
    parser.add_argument("output_dir", nargs="?", default=str(SCRIPT_DIR / "artifacts"))
    parser.add_argument("rids", nargs="*", help="Specific RIDs to build (default: all of " + ", ".join(ALL_TARGETS) + ")")
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

    for rid, (os_name, arch) in targets.items():
        publish_dir = publish_root / rid / "roslynHelper"
        archive_path = output_dir / f"roslynHelper-{os_name}-{arch}.tar.gz"

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
