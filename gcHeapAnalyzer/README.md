# gcHeapAnalyzer

A ClrMD-based tool that attaches to a running .NET process, walks its GC heap,
and produces a heap **fragmentation** report as JSON: how much of each
generation's committed memory is live objects vs. free holes, where the large
free holes are, which types are pinned (and blocking compaction), and which
types dominate the Large Object Heap.

It does not use EventPipe/ETW at all - it works over the *current, static*
state of the heap at attach time, not a stream of GC events. See "Why ClrMD
instead of an event trace?" below.

**This tool currently attaches by fully suspending the target process for the
entire duration of the heap walk** (`DataTarget.AttachToProcess(pid, suspend:
true)`) - the target is completely frozen, not just slowed down, from the
moment of attach until the walk finishes and ClrMD detaches. See "Suspended
attach vs. snapshot attach" below for a real, meaningfully different
alternative ClrMD supports that this tool does not currently use, and why it
matters far more once you're running `--watch` over many cycles than it does
for a single one-off capture.

## Running it

### Single-shot (one snapshot, right now)

```
gcHeapAnalyzer --pid <pid> [--output <path>]
```

- `--pid <pid>` - the target .NET process. Required (unless `--sample` is used).
- `--output <path>` - write the JSON report here. If omitted, the report is
  printed to stdout instead (status/progress messages always go to stderr, so
  stdout stays clean JSON either way).

The target process is **suspended for the duration of the walk** (typically a
few hundred milliseconds for a heap in the hundreds-of-MB to low-GB range) and
resumed automatically once ClrMD detaches.

### Watch mode (repeated snapshots over a period of time)

```
gcHeapAnalyzer --pid <pid> --watch --output <path> [--interval-seconds <n>] [--stop-file <path>] [--duration-seconds <n>]
```

- `--watch` - loop instead of capturing once. Requires both `--pid` and `--output`.
- `--interval-seconds <n>` (default `5`) - minimum time between the *start* of
  one cycle and the next. If a walk itself takes longer than this (large
  heap), the next cycle starts immediately afterward instead of waiting.
- `--stop-file <path>` - optional. As soon as a file exists at this path, the
  watcher finishes its current cycle, deletes the file, and exits cleanly.
  Checked every cycle and every 500ms while idle between cycles.
- `--duration-seconds <n>` - optional. Stop automatically after this many
  seconds total. Omit to run until Ctrl+C, SIGTERM, or `--stop-file`.

Each cycle **overwrites** `--output` wholesale with the newest snapshot - there
is no accumulated history file and no append mode. See "Why run it over time?"
below for what this does and doesn't give you.

**Every cycle is a full stop-the-world pause of the target, lasting exactly as
long as that cycle's heap walk takes.** `--watch` is not "attach once and poll
a live process" - it re-runs the entire attach→suspend→walk→resume sequence
from scratch every cycle (see `LiveWatcher.cs`), and `--interval-seconds` only
bounds the *minimum* gap between cycle **starts**, not a guaranteed window of
normal execution. If a single walk takes longer than the interval (a large
heap can take seconds), the next cycle's suspend begins immediately after the
previous one's resume, with essentially no gap at all. Watched over many
cycles, a large-heap target can end up frozen for the *majority* of the whole
observation window - worse the bigger the heap, which is backwards from what
you generally want when watching a busy service over time. See "Suspended
attach vs. snapshot attach" below for the alternative that avoids this.

### Sample mode (no live process needed)

```
gcHeapAnalyzer --sample [--output <path>]
```

Emits a synthetic, hand-crafted report in the exact same JSON shape as a real
capture, without attaching to anything. Useful for seeing the output shape
before setting up a real target, or for developing/testing on macOS where SIP
blocks live attachment without root.

### Elevated privileges

Attaching to another process's memory generally needs elevated privileges:

- **macOS**: SIP may block attaching to processes you don't own, or even ones
  you do, depending on how they were launched. Run as root, or check `--sample`.
- **Linux**: `ptrace_scope` may need to be `0` (`sudo sysctl kernel.yama.ptrace_scope=0`),
  or run as root.
- **Windows**: a standard user can attach to processes running under the same
  identity; otherwise you need debug privileges for the target.

On failure the tool prints these exact hints to stderr along with the
underlying error.

## What it captures, and why

Once attached (`DataTarget.AttachToProcess(pid, suspend: true)`), the walk does
three separate things over the suspended heap:

1. **Per-segment object walk.** Every GC segment is mapped to a generation
   (dedicated Gen0/Gen1/Gen2 segments under Server GC map directly; a single
   shared "Ephemeral" segment under Workstation GC is split per-object by
   address range against the segment's own `Generation0`/`Generation1`
   bounds; `Large` → LOH, `Pinned` → POH; `Frozen` segments, which are
   runtime-internal, are skipped). For every object on every segment:
   - **Free objects** (`obj.IsFree`) count toward that generation's free
     bytes/chunk count, get bucketed into a size histogram (`< 1 KB`,
     `1-8 KB`, `8-85 KB`, `85 KB-1 MB`, `> 1 MB`), and - if the single hole is
     `>= 85,000 bytes` (the LOH allocation threshold) - are recorded
     individually as a "large free chunk" with its address, since a hole that
     size is both rare and directly actionable (it's large enough to satisfy
     a future large-object allocation without growing the heap, if the
     allocator can find it).
   - **Live LOH objects** are aggregated by type name into a top-50-by-bytes
     ranking (`topLohTypes`) - LOH fragmentation is almost always driven by a
     small number of types allocating variably-sized buffers.
2. **Pinned handle enumeration**, done as a separate pass over
   `runtime.EnumerateHandles()` (`Pinned`/`AsyncPinned` kinds only) rather than
   folded into the object walk, because a GC handle can pin an object in *any*
   generation, not just the one you'd expect. Each pinned object's segment is
   resolved back to a generation and grouped by `(TypeName, Generation)` -
   this is the report's most direct answer to "what's actually preventing
   compaction," since pinned objects can't be moved.
3. **Derived roll-ups**: per-generation `FragmentationPct = FreeBytes /
   CommittedBytes * 100`, plus a `Summary` object with the same numbers
   totaled across the whole heap.

### Report shape

```jsonc
{
  "processId": 12345, "processName": "MyService", "captureTimeUtc": "...",
  "summary": {
    "totalCommittedBytes": ..., "totalObjectBytes": ..., "totalFreeBytes": ...,
    "fragmentationPct": ..., "pinnedObjectCount": ..., "segmentCount": ...
  },
  "generations": [
    // index 0-4: Gen0, Gen1, Gen2, LOH, POH
    { "generation": 0, "label": "Gen0", "committedBytes": ..., "objectBytes": ...,
      "freeBytes": ..., "fragmentationPct": ..., "segmentCount": ..., "freeChunkCount": ... }
  ],
  "freeChunks": {
    "totalCount": ..., "totalFreeBytes": ...,
    "histogram": [{ "label": "8-85 KB", "minBytes": ..., "maxBytes": ..., "count": ..., "totalBytes": ... }],
    "largeChunks": [{ "address": "0x7f...", "sizeBytes": ..., "generation": 3 }]
  },
  "pinnedObjects": [{ "typeName": "System.Byte[]", "generation": 2, "count": ..., "totalBytes": ... }],
  "topLohTypes":   [{ "typeName": "System.Byte[]", "count": ..., "totalBytes": ... }]
}
```

All byte fields are raw, unscaled bytes. `largeChunks`/`pinnedObjects` are
sorted descending by size/count so the biggest offenders are first;
`topLohTypes` is capped at 50 entries.

### Why ClrMD instead of an event trace?

A GC event trace (EventPipe/ETW, what `nettraceParser` decodes) tells you
*when* GCs happened and *how much* memory moved, but never *where* the free
holes physically are, *which specific objects* are pinned, or *what's
currently sitting* on the Large Object Heap - none of that is emitted as
trace events at all. Getting that requires actually walking the live heap's
object graph, which is what ClrMD is for. The two approaches are
complementary, not alternatives - see the next section.

## Suspended attach vs. snapshot attach

ClrMD's `DataTarget` actually offers two different ways to get at a live
process's memory, and they have very different impact on the target:

- **`AttachToProcess(pid, suspend: true)` - what this tool uses today.** Pauses
  every thread in the target for as long as `DataTarget` stays alive - in this
  tool's case, the entire heap walk. For a large heap that can be multiple
  seconds of the target being completely unresponsive, not just slow, once per
  capture (and once per `--watch` cycle - see above).
- **`AttachToProcess(pid, suspend: false)` - not a safe "live" alternative.**
  ClrMD's own documentation is explicit here: *"the user of ClrMD is still
  responsible for suspending the process itself. ClrMD does NOT support
  inspecting a running process and will produce undefined behavior when
  attempting to do so."* This parameter is for when the process is already
  stopped by some other means (e.g. it's a live debuggee already paused under
  a debugger) - it is not a way to inspect a genuinely running process safely.
- **`CreateSnapshotAndAttach(pid)` - a real low-impact alternative, Windows-only.**
  Uses the OS's own process-snapshotting facility to take a near-instantaneous
  point-in-time copy of the target - the target is only paused for as long as
  that snapshot takes to create (typically milliseconds), then resumes and
  keeps running completely normally while ClrMD analyzes the frozen *copy* for
  as long as the walk needs. This inverts the tradeoff of the suspend-based
  approach: instead of "frozen for the whole walk, every cycle," the target is
  "responsive the whole time, paused only briefly once per cycle to snapshot."
  It throws `PlatformNotSupportedException` on any platform other than
  Windows, so it can't simply replace the current approach outright - macOS
  and Linux would still need today's suspend-based path.

This tool does not currently call `CreateSnapshotAndAttach` anywhere - both
single-shot and `--watch` mode always use the full-suspend path. This is the
most important thing to know before pointing `--watch` at a production
service with a large heap for an extended period: today's implementation
means real, cumulative unresponsive time proportional to (walk time) ×
(cycle count), not a background/low-impact observation.

## Why run it over time? Is a one-time connect enough?

**A single connect is enough if you only need to know "how fragmented is this
process's heap right now"** - a spot-check on a service you suspect has
bloated, to decide whether it's worth investigating further. That's a
complete, self-consistent snapshot (the process is suspended for the whole
walk), and nothing about a single capture is wrong or incomplete on its own.

**It is not enough if the question is about a trend, a cause, or a specific
window in time**, for a few concrete reasons:

1. **Fragmentation is a moving target, and one sample can't tell you which
   direction it's moving.** A single snapshot can't distinguish "this is a
   stable baseline this process always runs at" from "this happens to be
   captured mid-spike" from "this is the tail end of a slow leak that never
   gets fully reclaimed." You need at least two points to see a trend, and
   several to tell a trend apart from normal GC-to-GC noise.

2. **There's no cheap "has a GC happened since I last looked" signal to wait
   on.** Even with the lower-impact `CreateSnapshotAndAttach` approach (see
   "Suspended attach vs. snapshot attach" above), ClrMD has no way to be
   notified when the heap changes - there's nothing to subscribe to that says
   "now would be an interesting moment to look." The only option is to
   repeatedly snapshot/attach-walk at some interval and treat whichever
   result lands nearest the moment you care about as good enough. That's
   exactly what `--watch` does - see `LiveWatcher.cs`'s header comment for the
   full reasoning. (This tool's *current* implementation compounds that with
   the full-suspend-per-cycle cost described above, but even a
   snapshot-based version would still need repeated cycles rather than one
   long-lived connection, for this reason.)

3. **The common real use case is correlating fragmentation with GC
   activity**, which needs a `.nettrace` GC-verbose capture running
   *alongside* the watch loop over the same time window (`--duration-seconds`
   exists specifically to make a paired "N-minute GC trace + N-minute ClrMD
   watch" capture easy to align, matching `dotnet-trace`'s own `--duration`).
   A single ClrMD snapshot has no time axis to line up against that trace at
   all.

**Important caveat about what `--watch` actually gives you**: it is *not* a
time series by itself. Every cycle overwrites the same `--output` file with
the latest snapshot - there is no accumulated history. If you want to see how
the report changed *between* cycles (not just look at whatever the newest one
happens to be when you check), you need to copy/timestamp `--output` yourself
between cycles (e.g. a small wrapper script polling and archiving it), since
the tool intentionally doesn't do that itself - see `LiveWatcher.cs`'s
rationale for always "patching" rather than versioning output.
