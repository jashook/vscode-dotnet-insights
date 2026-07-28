#!/usr/bin/env python3
################################################################################
# Packages gcHeapAnalyzer as self-contained, per-OS/arch release assets -
# mirrors nettraceParser/pack.py exactly (see that file's header for the
# archive-layout rationale, copied here verbatim):
#
#   gcHeapAnalyzer-{osName}-{arch}.tar.gz
#     gcHeapAnalyzer/                    <- single top-level folder
#       gcHeapAnalyzer                   <- self-contained apphost executable
#       gcHeapAnalyzer.dll
#       gcHeapAnalyzer.deps.json
#       gcHeapAnalyzer.runtimeconfig.json
#       <dependency DLLs...>
#
# This is a standard (not single-file) self-contained publish - matches
# nettraceParser/roslynHelper/gcEventListener's own release assets, which
# aren't single-file either.
#
# Unlike those three tools, gcHeapAnalyzer is not currently downloaded by
# DependencySetup.ts - it's a standalone CLI (see gcHeapAnalyzer/README.md),
# not something the VS Code extension shells out to today. Assets are still
# named/laid out identically to the other tools' release assets on purpose,
# so wiring up an auto-download later needs no format changes here.
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
PROJECT = SCRIPT_DIR / "gcHeapAnalyzer.csproj"

# rid -> (osName, arch), matching nettraceParser-{osName}-{arch}.tar.gz /
# roslynHelper-{osName}-{arch}.tar.gz / gcEventListener-{osName}-{arch}.tar.gz.
# arch is "x64" or "arm64" - DependencySetup.ts (for the other tools) picks
# between them via process.arch at download time.
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
        tar.add(publish_dir, arcname="gcHeapAnalyzer")

    size_mb = archive_path.stat().st_size / (1024 * 1024)
    print(f"Created {archive_path} ({size_mb:.1f} MB)\n")


def main() -> int:
    parser = argparse.ArgumentParser(description="Package gcHeapAnalyzer release archives.")
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
        publish_dir = publish_root / rid / "gcHeapAnalyzer"
        archive_path = output_dir / f"gcHeapAnalyzer-{os_name}-{arch}.tar.gz"

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
