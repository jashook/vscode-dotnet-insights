#!/bin/sh
################################################################################
# Captures the perf.data fixtures perfParser is developed against, plus a
# build-id-indexed symbol bundle so those captures can be resolved on a machine
# that is not this container (the whole point: a Linux capture is read on a
# Mac).
#
# Notes:
# The Ubuntu `perf` wrapper at /usr/bin/perf dispatches on `uname -r` and looks
# for /usr/lib/linux-tools/<running kernel>/perf. Under Docker Desktop the
# running kernel is the VM's (6.12.x-linuxkit) while the package ships tools
# built for an Ubuntu kernel, so that lookup always fails. The versioned binary
# speaks the same perf_event ABI, so it is invoked directly.
#
# There is no hardware PMU in the VM, so `cycles` (perf's default event) cannot
# be opened. cpu-clock is the software equivalent; a fixture recorded on real
# hardware would use cycles and produce the same record layout.
################################################################################

set -e

PERF=$(ls /usr/lib/linux-tools/*/perf 2>/dev/null | head -n 1)
if [ -z "$PERF" ]; then
    echo "capture.sh: no perf binary found under /usr/lib/linux-tools" >&2
    exit 1
fi

echo "capture.sh: using $PERF"

echo -1 > /proc/sys/kernel/perf_event_paranoid 2>/dev/null || \
    echo "capture.sh: could not lower perf_event_paranoid (need --privileged)" >&2
echo 0 > /proc/sys/kernel/kptr_restrict 2>/dev/null || true

DURATION=${DURATION:-10}
OUT=${OUT:-/out}
mkdir -p "$OUT"

# 1. Rust, CPU sampling with callchains. The primary fixture.
echo "capture.sh: recording rust cpu samples for ${DURATION}s"
"$PERF" record -F 499 -e cpu-clock -g --call-graph fp \
    --buildid-all \
    -o "$OUT/rust-cpu.perf.data" \
    -- /work/rust_workload "$DURATION"

# 2. Rust, machine-wide with the scheduling and kernel-lock tracepoints. The
#    sched:* events are the source for off-CPU and blocked-thread analysis;
#    lock:contention_* are KERNEL lock tracepoints and say nothing about a
#    userspace std::sync::Mutex, which is why futex is recorded alongside them.
echo "capture.sh: recording rust off-cpu + lock for ${DURATION}s"
"$PERF" record -a -g --call-graph fp \
    -e sched:sched_switch -e sched:sched_wakeup \
    -e syscalls:sys_enter_futex -e syscalls:sys_exit_futex \
    -e lock:contention_begin -e lock:contention_end \
    --buildid-all \
    -o "$OUT/rust-offcpu.perf.data" \
    -- /work/rust_workload "$DURATION" || \
    echo "capture.sh: off-cpu capture failed (tracepoints unavailable?)" >&2

# 3. Rust, PER PROCESS rather than machine-wide. The sched and futex
#    tracepoints are inherited by the traced command's children, so they do not
#    need -a - and not passing it matters enormously: the machine-wide form of
#    this same capture is 497MB for ten seconds, almost all of it other
#    processes' context switches, while this one is a few MB of the process
#    actually under study.
echo "capture.sh: recording rust per-process off-cpu for ${DURATION}s"
"$PERF" record -g --call-graph fp \
    -e sched:sched_switch -e sched:sched_stat_sleep -e sched:sched_stat_blocked \
    -e syscalls:sys_enter_futex -e syscalls:sys_exit_futex \
    --buildid-all \
    -o "$OUT/rust-offcpu-proc.perf.data" \
    -- /work/rust_workload "$DURATION" || \
    echo "capture.sh: per-process off-cpu capture failed" >&2

# 4. The C workload, kept as the cross-language control: the same shapes in a
#    language with no mangling and no monomorphization, so a Rust-specific
#    decode bug shows up as a difference between two captures rather than as a
#    plausible-looking profile.
echo "capture.sh: recording c cpu samples for ${DURATION}s"
"$PERF" record -F 499 -e cpu-clock -g --call-graph fp \
    --buildid-all \
    -o "$OUT/c-cpu.perf.data" \
    -- /work/workload "$DURATION" || true

################################################################################
# Symbol bundle.
#
# Laid out as <first 2 hex>/<remaining 38>.debug under symbols/.build-id -
# the conventional build-id layout that /usr/lib/debug/.build-id uses and that
# `apt install <pkg>-dbgsym` populates. Keying by build id rather than by
# filename is what makes a cached symbol file impossible to mismatch against a
# different build of the same name.
################################################################################

BUNDLE="$OUT/symbols/.build-id"
mkdir -p "$BUNDLE"

# The kernel's own symbol table. A perf.data contains no kernel symbol names at
# all - perf resolves them at report time from the reporting machine's
# /proc/kallsyms, which is useless for a capture read anywhere else. It is
# saved beside the symbol bundle rather than inside it because it is keyed by
# nothing: KASLR means it is only valid against captures from THIS BOOT.
#
# kptr_restrict was lowered at the top of this script; without that every
# address here reads as zero.
if [ -r /proc/kallsyms ]; then
    cp /proc/kallsyms "$OUT/symbols/kallsyms" 2>/dev/null || true
fi

for capture in "$OUT"/*.perf.data; do
    [ -f "$capture" ] || continue

    "$PERF" buildid-list -i "$capture" 2>/dev/null | while read -r buildid path; do
        case "$path" in
            /*) ;;
            *) continue ;;
        esac

        [ -f "$path" ] || continue
        [ -n "$buildid" ] || continue

        prefix=$(printf '%s' "$buildid" | cut -c1-2)
        rest=$(printf '%s' "$buildid" | cut -c3-)
        mkdir -p "$BUNDLE/$prefix"
        [ -f "$BUNDLE/$prefix/$rest.debug" ] || cp "$path" "$BUNDLE/$prefix/$rest.debug"
    done
done

# Ground truth for the reader: perf's own decode. `perf script` on a
# machine-wide tracepoint capture is enormous (a 10-second one measured 2.9GB),
# so only the CPU captures get one.
for name in rust-cpu c-cpu; do
    if [ -f "$OUT/$name.perf.data" ]; then
        echo "capture.sh: dumping ground truth for $name"
        "$PERF" report -i "$OUT/$name.perf.data" --stdio --header --no-children \
            > "$OUT/$name.report.txt" 2>/dev/null || true
        "$PERF" script -i "$OUT/$name.perf.data" --header \
            > "$OUT/$name.script.txt" 2>/dev/null || true
    fi

    if [ -f "$OUT/$name.perf.data" ]; then
        "$PERF" buildid-list -i "$OUT/$name.perf.data" > "$OUT/$name.buildids.txt" 2>/dev/null || true
    fi
done

# Ground truth for the demangler: every symbol in the Rust binary, mangled and
# as rustc's own demangler renders it. Two columns, tab separated.
if command -v rustfilt >/dev/null 2>&1; then
    echo "capture.sh: writing demangler ground truth"
    nm --defined-only /work/rust_workload 2>/dev/null | awk '{print $3}' | grep -E '^_[RZ]' | sort -u \
        > "$OUT/rust-symbols.mangled.txt" || true

    if [ -s "$OUT/rust-symbols.mangled.txt" ]; then
        rustfilt < "$OUT/rust-symbols.mangled.txt" > "$OUT/rust-symbols.demangled.txt" || true
        paste "$OUT/rust-symbols.mangled.txt" "$OUT/rust-symbols.demangled.txt" \
            > "$OUT/rust-symbols.tsv" || true
    fi
fi

if [ -f "$OUT/rust-offcpu.perf.data" ]; then
    "$PERF" evlist -i "$OUT/rust-offcpu.perf.data" -v > "$OUT/rust-offcpu.evlist.txt" 2>/dev/null || true
fi

chmod -R a+rw "$OUT" 2>/dev/null || true
du -sh "$OUT"/*.perf.data "$BUNDLE" 2>/dev/null || true
