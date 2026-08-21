# Dotnet Insights

**This extension uses Unsigned tools. This extension is not meant to be used in a production environment**

An extension for drilling into .NET MSIL and Jitted ASM for managed executables (PE Files). This is a cross platform extension that works on Linux (Ubuntu) OSX and Windows x64. The extension has a few different quality of life improvements. It is intended as an extension to improve .NET development in general. Please see the full feature list below.

Future work to include Linux arm64. Currently 32-bit support is not expected to be worked on; however, feel free to contribute.

## dotnetInsights

See [dotnetInsights](dotnetInsights/README.md) for more information.

## Linux traces from `dotnet-trace collect-linux`

`dotnet-trace collect-linux` collects through Linux `perf_events` and the
kernel's `user_events` mechanism, so one capture carries kernel, native and
managed activity together. It writes **nettrace v6**, a breaking change from
the format `dotnet-trace collect` produces — v6 drops the FastSerialization
framing entirely — so an older build of this extension rejects such a file
with *"Not a understood file format"*. Extension 1.9.4 and later read both.

Open the `.nettrace` in VS Code as usual. What you get:

- **CPU** — samples come from `perf_events` rather than the CLR sampler, and
  frames are symbolicated so kernel, native and JIT'd managed code all appear
  in one stack:

  ```
   16615  finish_task_switch.isra.0
   14597  RhpNewFast
   11595  ObjectNative::Monitor_TryEnter_FastPath
   10746  SVR::gc_heap::find_first_object
   10130  System.Uri.CheckCanonical [OptimizedTier1]
  ```

  A `collect-linux` capture names its modules but ships symbols for only a few
  of them, so the rest are fetched from Microsoft's public symbol server by
  ELF build ID and cached locally. Only the first trace from a given runtime
  build downloads anything (libcoreclr's symbols are ~138MB); every trace after
  that resolves from the cache. Set
  `dotnet-insights.downloadNativeSymbols` to `false` to stay offline — cached
  symbols are still used — and add distribution servers such as
  `debuginfod:https://debuginfod.ubuntu.com` via
  `dotnet-insights.symbolServers` to also name `libc` and `openssl` frames,
  which Microsoft's server does not carry.
- **GC**, **Exceptions**, **Events** — as usual; the CLR events are present
  and decoded normally.
- **Threading** — works, but its parked/blocked classification is *derived*.
  A perf-sampled capture carries no managed/native flag per sample, so one is
  inferred from whether each sample's innermost frame is managed code. The
  view says so; treat the roles as indicative rather than exact.

Whether the Allocation and Contention views have anything to show depends on
the keywords the capture was taken with, not on the format.

## Capturing a GC heap dump (`.gcdump`) without `dotnet-gcdump`

Open a `.gcdump` in this extension and you get three views over the heap: a
type census, retained sizes, and a type-level reference graph. Expanding a type
row in the first two shows what holds its instances alive, one reference per
level, all the way to a GC root.

The usual way to produce one is `dotnet-gcdump collect`. **That tool silently
truncates large heaps**, so `nettraceParser` can build the `.gcdump` itself,
from a trace captured with `dotnet-trace`. The result is an ordinary `.gcdump`
that PerfView, Visual Studio and `dotnet-gcdump report` all open normally.

### Why not just use `dotnet-gcdump collect`

Two limits, both in that tool's own post-processing rather than in the data the
runtime emits:

| Limit | Where | What you see |
|---|---|---|
| `MaxNodeCount = 10_000_000` | `DotNetHeapDumpGraphReader.cs` | The dump stops at ten million objects and **the truncated file is written anyway**. The explanatory `[WARNING]` goes to a log that is suppressed unless you pass `-v`, so by default nothing tells you. |
| 30s default `--timeout` | `EventPipeDotNetHeapDumper.cs` | The dump is abandoned. This one at least fails loudly &mdash; no file is written. |

A process holding ~12M objects produces a dump containing exactly 10,000,000
nodes, with the sampling multipliers still reporting 1 (i.e. claiming the
numbers are exact). Since the runtime emits the complete event stream either
way, capturing the events and decoding them yourself avoids both limits.

### The two steps

**1. Capture the heap-dump events with `dotnet-trace`:**

```bash
dotnet-trace collect -p <pid> \
  --providers Microsoft-Windows-DotNETRuntime:0x1980001:5 \
  --duration 00:00:00:30 \
  -o heap.nettrace
```

`0x1980001` is the `GCHeapSnapshot` keyword (`GC | GCHeapCollect | GCHeapDump |
GCHeapAndTypeNames | Type`) and `5` is `Verbose`. Enabling that keyword is the
whole trigger: the runtime induces a blocking gen2 GC and emits the bulk
node/edge/type/root events. A normal trace does not contain them.

Pick `--duration` to comfortably cover the heap walk &mdash; it is a
stop-the-world pause proportional to heap size. A trace cut short mid-stream is
detected (see "Dropped events" below); a heap that moved *underneath* the walk
is not &mdash; see "When the process is under load".

**2. Post-process it into a `.gcdump`:**

```bash
nettraceParser --gcdump-from-trace heap.nettrace -o heap.gcdump
```

Then open `heap.gcdump` in VS Code, or point any other heap tool at it.

### Dropped events

`dotnet-trace` uses a circular buffer that **drops** events under pressure,
whereas `dotnet-gcdump` opens its session in blocking mode. On a large heap the
bulk events can be lost.

This is detected rather than silently mis-decoded: the node and edge streams
carry block sequence numbers, and a gap makes the conversion fail with

```
The heap-dump event stream has gaps, so nodes and edges cannot be paired
reliably (GCBulkNode block N arrived where block M was expected).
Re-capture with a larger dotnet-trace --buffersize.
```

Raise `--buffersize` (default 256MB) and re-capture. Silence here means the
stream was complete.

### Differences from a `dotnet-gcdump` dump

Verified against the same process captured both ways: **total heap bytes,
reference counts and unreachable-object counts match exactly.** Three
presentational differences remain, all of them things `dotnet-gcdump` adds:

- It synthesizes named root-category pseudo-nodes (`[static vars]` and
  friends), which slightly inflates its object and reference counts.
- It splits a type into size-bucketed rows (`System.Object[] (Bytes > 1K)`);
  this keeps one row per type.
- It rewrites the angle brackets in compiler-generated names (`<>c` becomes
  `[]c`); this keeps the runtime's own spelling. Generic type names *are*
  normalized to C# syntax (`List<int>`, not ``List`1[System.Int32]``) so
  censuses from the two paths line up.

Conditional-weak-table (dependent handle) edges are not decoded, so an object
reachable only through one is reported as unrooted.

### When the process is under load

Both event-based paths - this one and `dotnet-gcdump collect` - are only
trustworthy against a heap that is holding still. Measured against a real
production service, and reproduced on a churning test heap:

| | objects described | references naming objects never described | reachable from a root |
|---|---|---|---|
| idle heap, `--gcdump-from-trace` | 2,998,612 | 0 | 100% |
| production service under load, `--gcdump-from-trace` | 4,254,952 | **1,798,821 (35%)** | **1.5%** |
| churning test heap, `dotnet-gcdump collect` | 27,088 | &mdash; | **13%** |

The type census stays usable in that state - object counts and bytes come from
the node stream alone. Everything depending on the root set does not: retained
sizes collapse toward zero and retention paths come back empty. Verbosity level,
buffer size and `--duration` change none of it, and neither does using
`dotnet-gcdump` instead.

If you cannot quiesce the process first, read a core dump instead.

## Reading a process core dump

A core dump has no such failure mode by construction: `createdump` suspends the
process and writes the actual memory image, so the object graph, the types and
the roots are all one instant.

**1. Collect it:**

```bash
dotnet-dump collect -p <pid> --type Heap -o heap.dmp
```

`--type Heap` keeps the file to roughly the heap's own size. The process is
paused while it is written - on the order of a second per gigabyte, the same
range as the stop-the-world GC the event path already costs.

**2. Convert it:**

```bash
nettraceParser --gcdump-from-dump heap.dmp -o heap.gcdump
```

Or go straight to the analysis the extension reads, which is a few hundred KB
whatever the heap size - useful when the dump itself is too big to move:

```bash
nettraceParser --gcdump-from-dump heap.dmp --json heap.json
```

You can also open `heap.dmp` (or `heap.core`) in VS Code directly; the extension
runs the same conversion itself.

### What this needs, and where to run it

ClrMD reads the dump through the DAC matching the runtime the dump came from. On
a host with that runtime installed it is found automatically; otherwise pass
`--dac <path to libmscordaccore>`. A Linux dump is therefore easiest to convert
on Linux - and since the `--json` output is small, that is what travels back to
the machine running VS Code.

Two further notes:

- **Stack roots on macOS.** The DAC's stack unwind crashes on a macOS Mach-O
  core dump, so those are converted without thread stack roots (detected
  automatically from the dump's magic; `--skip-stack-roots` forces it). Handles,
  statics and the finalizer queue are all still read, which is what a leak
  investigation runs on - but objects held *only* by a running frame are
  reported unrooted, and both the CLI and the webview say so when it applies.
- **Root kinds are preserved.** Unlike the trace path, retention paths from a
  core dump end at a named category - `[strong handle]`, `[pinned handle]`,
  `[finalizer queue]`, `[thread stack]` - rather than at an undifferentiated
  `[.NET Roots]`.

### Reading an existing `.gcdump`

Already have a dump from `dotnet-gcdump`? It opens directly &mdash; no
conversion needed. On the command line:

```bash
nettraceParser --gcdump heap.gcdump              # human-readable census
nettraceParser --gcdump heap.gcdump --json out.json
```