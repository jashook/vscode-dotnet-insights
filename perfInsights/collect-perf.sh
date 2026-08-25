#!/bin/sh
################################################################################
# collect-perf.sh - capture a profile for Perf Insights.
#
# Run this ON THE HOST (not inside the container), as root.
#
#   ./collect-perf.sh <pid> [seconds]
#
# For a containerised service, get the host pid first:
#   docker ps                       # container id
#   docker container top <id>       # host pid of the process
#
# It produces THREE files, and all three matter. The design is deliberate:
# structure and symbols are captured separately and merged on the analysing
# machine, because neither artifact alone is enough.
#
#   <name>.perf.data   the capture: samples, mappings, timestamps, tracepoint
#                      payloads. Complete and re-analysable, but its symbols
#                      depend on having the binaries - which the machine doing
#                      the analysis does not.
#   <name>.stacks      `perf script` text. Its symbolization CANNOT be beaten:
#                      perf resolved it here, with the real binaries and the
#                      real /proc/<pid>/maps. perfParser harvests it as an
#                      address->name oracle for the capture above.
#   <name>.kallsyms    the kernel's symbol table. A perf.data contains no
#                      kernel symbol NAMES at all, and this is the only thing
#                      that names the frames a blocked thread is sitting in.
#                      Valid only against captures from this boot (KASLR).
#
# Why `-e cpu-clock` rather than perf's default: the default event is `cycles`,
# a hardware counter. In a VM or a container without vPMU passthrough - most
# cloud hosts - it cannot be opened at all. cpu-clock is the software timer
# equivalent and always works.
################################################################################

set -e

PID="$1"
DURATION="${2:-30}"

if [ -z "$PID" ]; then
    echo "usage: $0 <pid> [seconds]" >&2
    echo "  get the host pid with: docker container top <container id>" >&2
    exit 1
fi

if [ ! -d "/proc/$PID" ]; then
    echo "$0: no process $PID on this host. Did you use the CONTAINER pid instead of the host one?" >&2
    exit 1
fi

NAME="perf-$(date +%Y%m%d-%H%M%S)-pid$PID"

echo "$0: capturing pid $PID for ${DURATION}s -> $NAME.*"

# Kernel addresses read as zero unless this is lowered, which silently makes
# every kernel frame unresolvable.
if [ -w /proc/sys/kernel/kptr_restrict ]; then
    echo 0 > /proc/sys/kernel/kptr_restrict
fi

################################################################################
# One capture, four event groups.
#
#   cpu-clock           where CPU time goes.
#   sys_enter/exit_futex  LOCK CONTENTION. A userspace mutex only enters the
#                       kernel when it is CONTENDED, so every enter/exit pair
#                       is contention by construction. NOTE: `lock:contention_*`
#                       is NOT this - those are KERNEL lock tracepoints and say
#                       nothing about a std::mutex. Measured on a workload with
#                       four threads fighting over one mutex: lock:contention_*
#                       produced 39 events, futex produced 375,224.
#   sched_switch        threading - which threads blocked, why, and in what
#                       stack.
#
# -g is on all of them on purpose: a contention event without the stack that
# took the lock, or a block without the stack that blocked, names a duration
# and nothing you can act on.
################################################################################

perf record \
    -p "$PID" \
    -g --call-graph fp \
    -F 99 \
    -e cpu-clock \
    -e syscalls:sys_enter_futex \
    -e syscalls:sys_exit_futex \
    -e sched:sched_switch \
    -o "$NAME.perf.data" \
    -- sleep "$DURATION"

echo "$0: resolving symbols here, while the binaries are still reachable"
perf script --header -i "$NAME.perf.data" > "$NAME.stacks"

# --inline expands inlined frames into the text. Not supported by every perf
# build, so it is tried and the result kept only if it produced something -
# an unsupported flag must not cost the whole file.
if perf script --header --inline -i "$NAME.perf.data" > "$NAME.stacks.inline" 2>/dev/null; then
    if [ -s "$NAME.stacks.inline" ]; then
        mv "$NAME.stacks.inline" "$NAME.stacks"
        echo "$0: used --inline (inlined frames included)"
    else
        rm -f "$NAME.stacks.inline"
    fi
else
    rm -f "$NAME.stacks.inline"
fi

cp /proc/kallsyms "$NAME.kallsyms" 2>/dev/null || \
    echo "$0: could not copy /proc/kallsyms - kernel frames will show as addresses" >&2

tar czf "$NAME.tar.gz" "$NAME.perf.data" "$NAME.stacks" "$NAME.kallsyms" 2>/dev/null || \
    tar czf "$NAME.tar.gz" "$NAME.perf.data" "$NAME.stacks"

echo
echo "$0: done."
ls -la "$NAME".* | awk '{printf "  %8.1f MB  %s\n", $5/1048576, $9}'
echo
echo "  Move $NAME.tar.gz to the machine you analyse on, extract it,"
echo "  and open the .perf.data in VS Code. The .stacks and .kallsyms"
echo "  beside it are picked up automatically."
