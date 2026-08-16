# Dotnet Insights

**This extension uses Unsigned tools. This extension is not meant to be used in a production environment**

An extension for drilling into .NET MSIL and Jitted ASM for managed executables (PE Files). This is a cross platform extension that works on Linux (Ubuntu) OSX and Windows x64. The extension has a few different quality of life improvements. It is intended as an extension to improve .NET development in general. Please see the full feature list below.

Future work to include Linux arm64. Currently 32-bit support is not expected to be worked on; however, feel free to contribute.

## dotnetInsights

See [dotnetInsights](dotnetInsights/README.md) for more information.

## Capturing a GC heap dump (`.gcdump`) without `dotnet-gcdump`

Open a `.gcdump` in this extension and you get four views over the heap: a type
census, retained sizes, paths to root, and a type-level reference graph.

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
stop-the-world pause proportional to heap size. If the trace is cut short the
next step will tell you rather than producing a partial dump.

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

### Reading an existing `.gcdump`

Already have a dump from `dotnet-gcdump`? It opens directly &mdash; no
conversion needed. On the command line:

```bash
nettraceParser --gcdump heap.gcdump              # human-readable census
nettraceParser --gcdump heap.gcdump --json out.json
```