# vscode-dotnet-insights

VS Code extension (`dotnetInsights/`, TypeScript) for inspecting .NET runtime
behavior (GC, JIT, IL) plus several bundled native helper tools it shells out
to: `gcEventListener/`, `roslynHelper/`, `nettraceParser/` (all C#/.NET), and
a `pmi` tool. Each helper is distributed as a self-contained per-OS binary
uploaded as a GitHub release asset and downloaded on first use — see
"Tool distribution & the stale-cache trap" below before touching any of them.

## `nettraceParser`

Hand-built `.nettrace` (EventPipe) binary parser — deliberately *not* built
on `Microsoft.Diagnostics.Tracing.TraceEvent`. `FastSerialization.cs` /
`StreamReaderWriter.cs` are vendored source (from microsoft/perfview, MIT)
because `Microsoft.Diagnostics.FastSerialization` isn't published as a
standalone NuGet package — it only ships bundled inside TraceEvent's nupkg.

Hard-won facts, verified against real captures — re-derive from the manifest
only if these stop matching observed behavior:

- **Stream positioning**: `IOStreamStreamReader.Fill()` seeks using its own
  internal `positionInStream` counter starting at 0, not the underlying
  stream's real `.Position`. Feed it a `MemoryStream` slice starting at file
  offset 8 (past the `"Nettrace"` magic), not a pre-advanced `FileStream`.
- **Version compatibility**: `SerializationType.FromStream`'s compat check
  requires every deserialized type implement `IFastSerializableVersion`
  (`Version`/`MinimumVersionCanRead`/`MinimumReaderVersion`) — missing this
  throws "App is version 0".
- **Stream terminator**: the eager `allowLazyDeserialization=false` loop
  expects a bare `EndObject` tag (a legacy V1 quirk) but real files terminate
  with `NullReference`. Use `GetEntryObject()` + a manual
  `while (deserializer.ReadObject() != null) {}` loop instead.
- **Compressed event header delta decoding**: `CompressedEventBlobDecoderState`
  (`Blocks/CompressedEventBlobHeader.cs`) must be a **fresh, zero-initialized**
  instance per `EventBlock`/`MetadataBlock` — per `NetTraceFormat_v5.md`,
  "when starting a new event block assume the previous event contained every
  field with a zeroed value." A block's own `MinTimestamp`/`MaxTimestamp`
  header fields are purely descriptive (let a reader locate blocks of
  interest without decoding every event inside them) and must **not** seed
  the decoder state — seeding `TimeStamp` with `MinTimestamp` double-counts
  it into every event's own timestamp for the rest of that block (a block's
  first event blob already re-encodes a delta that amounts to its own true
  absolute QPC value on its own). This was a real, long-standing bug here:
  every event's decoded QPC came out ~2x too large, invisible to every test
  in this repo (none compared timestamps) until a ground-truth diff against
  `Microsoft.Diagnostics.Tracing.TraceEvent` caught it (see "Ground-truth
  diff testing" below). It also fully explains the next bullet below, which
  was itself a misdiagnosis of this same bug's symptom.
- **QPC timestamp anchor**: use `NettraceHeader.SyncTimeQPC`/`SyncTimeUtc`
  directly as the wall-clock reference (`referenceQpc =
  file.Header.SyncTimeQPC`) — this used to look unreliable ("~3 days off in
  one verified case," previously worked around by anchoring to the trace's
  own first event's QPC instead), but that was a symptom of the timestamp
  decode bug above, not a real problem with `SyncTimeQPC`. With that bug
  fixed, `SyncTimeQPC` agrees with the first event's own QPC to within ~1ms
  on every real capture checked and matches PerfView/TraceEvent's own anchor
  exactly (verified via `Microsoft.Diagnostics.Tracing.TraceEvent`'s
  `sessionStartTimeQPC`, which is literally `_syncTimeQPC` read the same way).
- **GC event correlation**: `GCHeapStats` / `GCGlobalHeapHistory` /
  `GCPerHeapHistory` can arrive on the wire *after* `GCEnd` for the GC they
  describe — `GcEventProjector.Project` correlates via several distinct
  "most recent X" trackers, not by tracking "currently open" GCs, because a
  single shared notion of "current" isn't enough:
  - `GCGlobalHeapHistory`/`GCPerHeapHistory` route via `currentBatchGcId`,
    resolved through a **per-generation** pending queue (keyed by `GCStart`'s
    own `Depth`, dequeued using `GCGlobalHeapHistory`'s own
    `CondemnedGeneration`) — a single shared queue/pointer can't tell a slow
    background gen2 GC's bookkeeping apart from overlapping foreground
    gen0/1 GCs.
  - `GCHeapStats` routes via a **separate** `mostRecentlyEndedGcId` (updated
    on `GCEnd`), not `currentBatchGcId` — verified against a real Server GC
    capture that a background GC's own `GCHeapStats` arrives right after its
    own `GCEnd` but *before* its own `GCGlobalHeapHistory` (the reverse order
    from a foreground GC). Falls back from `currentBatchGcId` only once that
    candidate already has its stats, so both orders resolve correctly.
  - `PauseDurationMSec`'s true window is `GCSuspendEEBegin` (thread
    suspension requested) through `GCRestartEEEnd` (threads running again) —
    not `GCStart`-to-`GCEnd`, which omits the time spent actually stopping
    every thread and, for a background GC, covers its entire concurrent mark
    phase rather than a real pause. A background GC's own pause is seeded at
    `GCStart` with just its initiating `GCSuspendEEBegin`-to-`GCSuspendEEEnd`
    gap (microseconds, not a lasting pause), then further internal
    `SuspendForGCPrep` cycles *accumulate* onto it while a `SuspendForGC`
    cycle *replaces* it — both verified field-for-field against TraceEvent's
    own `TraceManagedProcess.cs` (`AddConcurrentPauseTime`,
    `GetCurrentGC`). `GCEnd`'s own `GCStart`-to-`GCEnd` fallback computation
    must be skipped once a real suspend/restart-based value already exists
    for that GC (tracked via `gcIdsWithSuspendBasedPause`) — a background
    GC's completing `GCEnd` arrives *after* its last internal
    `GCRestartEEEnd`, so without this guard it silently clobbers the correct
    value with the full mark-phase span.
- **`GCPerHeapHistory`**: only `Version >= 3` payloads are decoded (what this
  environment's .NET actually emits); older layouts are unimplemented on
  purpose.
- **StackId recycling**: per `NetTraceFormat_v5.md`'s StackBlock section,
  "Events are only allowed to refer to a stack id if there is no sequence
  point in between the event and the stack" — a numeric `StackId` is only
  valid until the next sequence point, after which a later `StackBlock` is
  free to reuse the same id for a completely different stack. A single
  whole-file `Dictionary<int, long[]>` (the original `NettraceFile.StacksById`
  design) gets silently overwritten by that reuse, so resolving stacks lazily
  (after the whole file is parsed) makes every event's resolved stack a
  coin flip between its real stack and whatever later claimed that id —
  individually plausible-looking, but wrong. Confirmed against real
  production data diffed with `Microsoft.Diagnostics.Tracing.TraceEvent`: 0
  of 30 sampled `GCAllocationTick` leaf frames agreed before the fix. Fixed
  by resolving each event's stack **eagerly**, in `EventBlock.cs`, at the
  exact moment it's parsed (against whatever `StacksById` holds then, since
  blocks are processed in real file order) — `EventRecord`/`AllocationEvent`
  now carry the resolved `long[]` directly instead of a recyclable `int`, and
  downstream aggregation (`AllocationJsonExporter`) keys by stack array
  reference (`ReferenceEqualityComparer`) rather than the id. Permanent
  regression coverage:
  `GroundTruthDiffTests.AllocationEventProjector_Project_StackLeafFramesMatchTraceEventGroundTruth`
  (via a new `TraceEventAllocationReader` in `nettraceParser.GroundTruth`) —
  verified clean (0 mismatches) against a 1.3M-tick real capture.
- Field names/shape in `Gc/GcJsonExporter.cs`'s `--json` output are copied
  1:1 from `dotnetInsights/src/DotnetInsightsGcSnapshotEditor.ts`'s
  `gcDataFromXml` so the shared webview renderer needs no source-specific
  branching.
- `DateTime` in the JSON output is the machine's **local** time
  (`gcEvent.Timestamp.ToLocalTime().ToString("o")`), not UTC — the extension
  renders it directly, so getting this wrong shows the wrong wall-clock time
  to the user, not just a wrong offset string.
- **GC is suppressed for the read phase only** (`ReadPhaseGcSuppression.cs`,
  via `GC.TryStartNoGCRegion`). `NettraceFile.Read` allocates ~2.6x the
  input file's size and retains essentially all of it (`StackBlock`'s
  decoded `long[]` stacks live on `EventRecord.Stack` for the process's
  life), so the generational GC's "most objects die young" assumption is
  simply false there: measured collections reclaimed **0.6–2.0 KB each**
  while still paying full mark/promote cost, and ~100% gen0 survival filled
  gen1 at gen0's own rate, escalating into repeated full gen2 collections.
  On a real 737MB/4.29M-event capture this took the run from 2745–3656ms to
  a stable 2543–2558ms, GC pause 167–419ms → 0.0ms, collections [4,3,3] →
  [1,1,1], **and peak RSS 2.31GB → 1.82GB** (no promotion copying), with
  byte-identical JSON output. It also removed a large run-to-run bimodality
  where the baseline randomly alternated ~2750ms/~3650ms depending on
  whether the GC escalated to 3 full collections or 1.
  **Undersizing the budget is worse than not doing this at all** — a
  region exhausted mid-read forces an induced collection and exits, which
  measured 3743ms/[5,4,4] against a 3561ms/[4,3,3] baseline. Hence
  `ComputeBudgetBytes` returns 0 (declining entirely) rather than ever
  requesting a budget it isn't confident covers the whole read.
  `DOTNET_GCgen0size` was also measured as an alternative (best case
  −520ms at 32MB) but is strictly worse: it's a deployment-time env var the
  extension would have to set when spawning the process, and larger values
  *slowed* the export phase by ~400ms even while speeding up `read`.
- The `--json` timing line reports `gcPause=`/`gcCounts=[gen0,gen1,gen2]`
  (from `GC.GetTotalPauseDuration()`/`GC.CollectionCount`) precisely so
  "why was this run slower" is answerable without a `dotnet-trace` attach —
  which has its own confound here, since attach latency can silently miss
  whichever GCs fire earliest, making cross-run comparisons from two
  independent traces unreliable in a way this in-process counter isn't.

### Projector concurrency and the hot-path lookup primitives (2026-08-15)

A phase-attributed self-profile (the harness lives outside the repo, in
`~/projects/Investigations/nettraceParser-phase-profiler/` — it buckets
`nettraceParser`'s own CPU samples into its own pipeline phases using the
`PROGRESS` lines' wall-clock timestamps) took a real 3.23GB/35.08M-event
capture from **12.4s to ~7.9s**, with byte-identical JSON output (verified
section-by-section against a pre-change build; the only difference anywhere
was *which* methods tie at the exact top-200 `hotMethods` cutoff, now made
deterministic by a frame-id tie-break in `WriteHotMethods`). What changed,
and the rules worth keeping:

- **The eight projector passes run concurrently** (`Task`-based, explicit —
  not `Parallel.ForEach`). They're independent read-only passes over the same
  `List<EventRecord>`; sequential they cost ~1230ms end-to-end on an 8-core
  machine with ~0% idle samples, concurrently ~505ms. The one dependency is
  `SampleProfileEventProjector`, chained off the event overview because the
  overview's exact per-event-type counts presize its 16.24M-element result
  list (`EventOverview.CountForEvent`); growing that list from empty was 33%
  of that projector's own cost. Per-projector `Timing:` numbers are now
  *concurrent* durations and deliberately no longer sum to the phase's wall
  time — the line reports `projectors=<wall>ms wall, concurrent[...]`.
- **Provider/event names are interned at metadata-parse time**
  (`MetadataBlock.ReadMetadataPayload`). Every projector filters with
  `record.ProviderName != ClrProviderName` against a literal, and
  `String.Equals` short-circuits on *reference* equality — so a decoded
  instance that merely equals the literal loses that fast path and content-
  compares once per event per pass. That was 4.9% of the whole run
  (`SpanHelpers.SequenceEqual`) and interning ~40 metadata strings removes
  essentially all of it.
- **Frame-id-keyed lookups on per-sample paths are array-indexed, not
  hashed** — `Cpu/FrameIdTable.cs` (was `Dictionary<int, HotMethodStats>`,
  13.4% of the CPU export phase), `Cpu/FrameIdSet.cs` (stamp-based per-stack
  dedup; replaced an `Array.Sort`+compact that was 16.5% of that phase, which
  had itself replaced a `HashSet<int>.Clear()` that was worse), and
  `Cpu/IdleWaitFrameCache.cs` (memoizes `CpuIdleWaitClassifier`'s 18 string
  comparisons per distinct method instead of per sample — `TimeBreakdownBuilder`
  ran it over all 16.24M samples, ~1.0s). All three index **two** dense
  ranges, not one: see `MethodSymbolTable.UnresolvedIdBase`, now public for
  exactly that reason.
- **`EventOverviewBuilder` hashes nothing per event** — its
  `Dictionary<(string, int), _>` (72% of that phase, `Marvin.ComputeHash32`
  alone 24.4%) is now provider slots matched by reference with an event-id-
  indexed array behind each. Event ids at/above 65536 fall back to a
  dictionary so a corrupt id can't size an array off a number read from the
  file.
- **Names decoded from payloads go through `Utf16StringPool`** — the wire
  format is already UTF-16, so `PayloadReader.GetUnicodeCharsAt` hands out a
  `ReadOnlySpan<char>` view and the pool returns one canonical string per
  distinct content, probing `HashSet<string>` through its
  `GetAlternateLookup<ReadOnlySpan<char>>` so a hit allocates nothing. (That
  API is net9.0+; the pool first shipped as hand-rolled open addressing while
  this project was on net8.0, and was rewritten onto the BCL's own when
  `nettraceParser`/`.Tests`/`.GroundTruth` moved to **net10.0** — matching
  `roslynHelper`, which was already there. The other C# projects in this repo
  are untouched.) A capture with 1.44M exceptions holds
  a few dozen distinct type names; decoding each event's own was over half the
  exception projection phase (721ms → ~317ms). `FindUnicodeStringEnd` also
  scans as `char`s (vectorized `IndexOf`) rather than byte pairs.
- **The CPU export's timeline no longer costs a second per-sample pass.**
  `WriteTimeline` used to re-walk all 16.24M samples purely to recover each
  sample's leaf frame, re-probing the stack→frames dictionary to do it; the
  main loop already has that leaf, so it now accumulates per-leaf bucket
  histograms directly (`FrameIdTable<int[]>`, ~1MB) and `BuildTimeline` just
  reorders them into rank order. The sample time range needed for bucketing
  comes from a cheap min/max pre-scan that touches no dictionary. Verified
  identical output, including every `methodSelfByBucket` row.
- **Stacks are deduplicated by content at decode time** (`StackTable.GetOrAdd`),
  and this is a memory fix first: the runtime re-emits stacks after every
  sequence point (the same property that makes StackIds recyclable), so on a
  real 3.23GB capture **2,346,969 of 2,430,313 decoded stacks - 96.6%, 1,481MB
  of 1,539MB - were byte-identical repeats**. Deduplicating leaves 83,344 real
  stacks and 57MB. `StackBlock` decodes into a reusable `long[]` scratch and
  passes a span, so the 96.6% case allocates nothing at all - which matters
  specifically because the read phase runs inside a no-GC region, where
  allocating a duplicate and dropping it still holds its pages until the
  region ends. Peak RSS **7.0GB → 5.52GB**; total run **6.7s → 5.0s**, since
  the exporters' per-distinct-stack work collapsed by the same 29x (export
  4.46s → 2.47s). Hash is length + 4 sampled frames with a chain walk on
  collision, so a collision costs a `SequenceEqual`, never a merge.
  **Output effect, verified against a dedup-disabled build of the same tree**:
  every measurement is identical (samples, bytes, counts, `gcData`,
  `timeBreakdown`, ticks sidecar) and only `distinctStackCount` changes - it
  now counts distinct call PATHS rather than distinct stack objects, e.g.
  201,122 → 2,921 for the top exception type, which is what that "N call
  paths" hint always meant to say. (7 `totalWaitMSec` values also differ in
  their 15th significant digit, from summing the same doubles in a different
  order.)
- **Stacks are referred to by a dense index, not by the decoded `long[]`**
  (`StackTable.cs`, 2026-08-15). `StackBlock` appends each decoded stack to one
  table and `EventRecord`/`SampleEvent`/`AllocationEvent`/`ExceptionEvent`/
  `ContentionEvent` carry a `StackIndex` into it; index 0 is permanently the
  empty stack, so "no stack" needs no sentinel. Every exporter that used to key
  a `Dictionary<long[], _>` by array identity now indexes an array (CPU) or a
  plain int-keyed dictionary (allocation/exception/contention), so
  `RuntimeHelpers.GetHashCode` is gone from the profile entirely. Measured on
  the 3.23GB capture: export 5.3s → **4.5s**, of which cpu 1.95s → 1.67s, exc
  1.1s → 0.85s, alloc 1.5s → 1.34s — *and* the projector phase dropped 505ms →
  366ms as a side effect, since `EventRecord` lost a reference field (35M fewer
  pointers for the GC to trace, and a smaller struct to stream). Whole run
  ~7.5s → **~6.7s**; output byte-identical including the ticks sidecar.
  Two `file = null`/`captureFile = null` sites in `Program.cs` (the `--json`
  and `--diff` paths, both deliberate GC-root drops) now capture the table
  into a local first — reading `file.Stacks` after the null is an immediate
  `NullReferenceException`, which is exactly how this was caught.
- **Two measured negative results on that same dictionary, both worth not
  repeating.** (1) A per-thread sticky cache (64 slots keyed by `ThreadId`)
  looked obviously better than one global slot and measured *worse* — 51% hits
  vs 72% — because identical stacks are ONE shared `long[]` here, so the single
  hottest stack is shared across threads and per-thread slots each re-learn it
  while evicting each other. An 8-entry global cache scanned linearly gets 79%.
  (2) `RuntimeHelpers.GetHashCode` (via `ReferenceEqualityComparer`) shows as
  71–79% of the whole CPU export phase's CPU samples, but that is NOT a
  wall-time lever: cutting probes 4.56M→3.36M didn't move the phase, and
  replacing the identity hash with a content-derived one (reference equality
  kept) made it **6× slower** through collision pileups. The cost is the probe
  into a 633K-entry dictionary, not the hash call — so the fix is to stop
  keying by array identity at all — which is what the `StackTable` bullet
  above then did, confirming the diagnosis.
- **A sampled leaf is not proof of cost.** In the same profile,
  `Stopwatch.GetRawElapsedTicks` under `ProgressReporter.ReportFraction`
  looked like 13.3% of the read phase; direct counters showed 45,434 progress
  calls / 21,340 stopwatch queries there, and an A/B run with progress
  disabled showed *no* read-phase difference. Stack-walk misattribution.
  Confirm a surprising leaf with counters or an A/B before optimizing it.

### `.nettrace` parsing progress bar (`nettraceParser/Progress/`)

`--json` mode (only — the plain CLI/`--dump-fields` path is byte-for-byte
untouched, verified via a diff against an isolated `git worktree` at the
pre-change commit) writes `PROGRESS <percent> <phase label>` lines to
stderr — the same channel the final `Timing: ...` line already uses — so
`DotnetInsightsNettraceEditor.ts` can drive a live progress bar while
parsing is still in flight. `Progress/ProgressReporter.cs` owns emission
(integer-percent-changed gate, then a ~100ms throttle, then a monotonic
clamp, in that order — the percent gate alone bounds a huge capture's read
phase to ≤101 short writes against the `GC.TryStartNoGCRegion` budget
`ReadPhaseGcSuppression` sizes above, which is why `Warmup()` pre-touches
`Console.Error`'s own lazy encoder init *before* `TryStart`, not inside the
no-GC region). `Progress/ProgressPlan.cs` computes each phase's `[start,
end)` slice of the overall bar in three stages, each computed only once its
own inputs are known (file size before `Read`; `file.Events.Count` right
after; real `gcCount`/`allocationCount`/`exceptionCount`/`sampleCount`/
`contentionCount` right before `GcJsonExporter.WriteToFile`) — this makes
monotonicity structural (each stage only ever subdivides a range the
previous stage hasn't consumed yet) rather than enforced after the fact.

**Why not one global weight formula**: verified wrong on a real
1.57GB/29.6M-event capture — the read phase (bytes-driven) was only ~27-33%
of wall-clock time despite being ~98% of the raw byte count against the
~30M "items" (events/GCs/ticks/exceptions/samples) the rest of the pipeline
touches. Byte count and item count are not interchangeable costs, so
`ProgressPlan.cs`'s weight constants are measured PROPORTIONS of real
wall-clock time (not raw unit sums), calibrated against **two**
differently-shaped real captures specifically because one alone
overfits — confirmed by the two captures' own numbers disagreeing wildly on
some splits (e.g. the 7 projector phases' own relative weights: `gcProject`
was 46% of that combined total on a CPU-sample-heavy capture but only 10%
on a GC/allocation-heavy one; `eventOverview` the reverse, 8% vs 46%).
`ReadShareOfTotal` and `ProjectorsShareOfRemainder` agree far better between
the two captures (within a few points) since they're each dominated by
whichever few phases are large in a given capture rather than depending on
*which specific* phase that is. **The per-projector weight array is gone as
of 2026-08-15** — the eight projectors now run *concurrently* (see "Projector
concurrency" below), so they no longer occupy disjoint stretches of the bar
that could be weighted against each other; they report as one combined
"Projecting events" phase that advances on the mean of their eight completion
fractions, published into a `double[]` slot each and read by the main thread
(`ReportProjectorProgress` in `Program.cs`) because `ProgressReporter` is
static single-threaded state that writes to `Console.Error`.
The export phase's own 5 sub-writer weights are
the one place a per-item estimate compares genuinely comparable
things — 5 writers in the same phase of the same run — so `GcJsonExporter.
WriteToFile` computes that split dynamically from THIS run's own real
counts rather than a fixed constant.

Only `Cpu/CpuProfileJsonExporter.cs` and `Gc/AllocationJsonExporter.cs`
(`AllocationSummaryBuilder.Write`) get internal fine-grained progress
tracking within the export phase — the other three sub-writers (exceptions,
contention, the `gcData` array) are small enough on every capture measured
so far (all under ~1% of total time) that a start/complete snap is visually
indistinguishable from tracking them internally. Every per-event loop's
progress check is gated by `(index & ProgressReporter.IndexProgressMask) ==
0` (a power-of-two mask, not `% N`) specifically because a delegate call on
every iteration of a 26M-sample loop is exactly the kind of per-iteration
cost this codebase's own perf work (see `CachedStackFrames` above) has
repeatedly found and removed.

The extension host owns the true 100% deliberately — the C# process never
reports it. Real work remains after it exits (reading the JSON + ticks
binary back, `renderGcSnapshotWebview`'s own HTML build, the
`webview.html` assignment itself), so `NettraceProgress.ts` maps the child
process's own 0–100 down to `[0, 80)` and reserves `[80, 90)`/`[90,
97)`/`[97, 100]` for those three host-side stages — each preceded by a
`postMessage` and an `await new Promise(resolve => setImmediate(resolve))`
yield, since those steps are synchronous and block the extension host's own
event loop, so a message posted right before one wouldn't actually reach
the webview until it finished. `DotnetInsightsNettraceEditor.ts`'s
`resolveCustomEditor` now assigns `webviewPanel.webview.html` **twice**: a
lightweight loading placeholder (`NettraceLoadingRenderer.ts`,
`media/nettraceLoadingView.js`) synchronously, immediately — before
`nettraceParser` is even spawned, so there's a live document able to
receive `postMessage` at all — then the real rendered content once parsing
finishes, fully replacing the first document (not an in-place patch:
`media/snapshotGcStats.js` calls `acquireVsCodeApi()` itself, which throws
if called twice in the same document). The loading view starts
**indeterminate** and only switches to a real percentage on the first
`nettraceProgress` message, specifically so a stale, already-downloaded
`nettraceParser` binary that predates this feature (see the stale-cache
trap below — this shipped with `latestNettraceParserVersionNumber` bumped
to `"1.6.8"` for exactly this reason) shows motion instead of a bar frozen
at 0% forever. A `nettraceLoadingReady` handshake message (webview → host)
covers the fact that a `postMessage` sent before the loading document has
finished loading and run its own script is silently dropped, with no
VS Code-side buffering — the host replays its own last known progress once
it receives that signal.

Recalibrating `ProgressPlan.cs`'s weight constants against a third
differently-shaped capture is a single CLI run, not scaffolding that needs
re-adding: the final `Timing: ...` line's `export=` field permanently breaks
down as `export=Xms(alloc=..,exc=..,cpu=..,cont=..,gc=..)`.

That field is called `export=`, not `jsonExport=` (renamed 2026-08-15, along
with `JsonExportTiming`/`ProgressPlan.PlanJsonExport*`), because the phase has
not written only JSON for some time: `alloc=` includes the allocation-tick
**binary** sidecar (`AllocationSummaryBuilder.WriteTicks` — that array never
went into the JSON at all), and the `--binary` capture container
(`Binary/BinaryCaptureWriter.cs`) is written right after it. That container
write used to sit outside every timer in the line; it now reports as
`binaryExport=`, deliberately separate from `export=` so that as sections
migrate off JSON the number that grows and the number that shrinks stay
visible against each other. `GcJsonExporter`/`--json` keep their names — they
really do still write JSON.

Packaging: `nettraceParser/pack.py` (Python — a bash version was explicitly
rejected in favor of this). Publishes self-contained, non-single-file builds
for `osx-x64`/`linux-x64`/`win-x64` and archives each as
`nettraceParser-{osName}-x64.tar.gz` with a single top-level `nettraceParser/`
folder, matching `roslynHelper`'s real release-asset layout exactly (verified
by inspecting a real `roslynHelper-osx-x64.tar.gz`). Upload with
`gh release upload <tag> nettraceParser/artifacts/*.tar.gz --clobber`.

### `.gcdump` heap snapshots (`nettraceParser/GcDump/`)

`nettraceParser --gcdump <file.gcdump> [--json <out>]` reads a
`dotnet-gcdump collect` heap SNAPSHOT (what is on the heap right now and what
keeps it alive) rather than an event stream. It lives inside nettraceParser
only because a `.gcdump` is a `!FastSerialization.1` stream - the same
serializer `FastSerialization.cs` already vendors - so it reuses the
deserializer, `Progress/`, `pack.py`, `DependencySetup.ts` and the version
constant with no new release asset. The extension side is
`DotnetInsightsGcDumpEditor.ts` / `GcDumpRenderer.ts` / `media/gcDumpView.js`,
registered on `*.gcdump`.

Hard-won facts, all verified against real captures and against
dotnet/diagnostics' own `Graph.cs`/`GCHeapDump.cs` (the code that WRITES the
format):

- **No magic prefix.** Unlike `.nettrace` the FastSerialization signature is at
  offset 0, so the `Deserializer` reads the file directly - no `MemoryStream`
  slicing (contrast the "Stream positioning" note above).
- **Stream label width is `FourBytes`, always.** `SerializationSettings.Default`
  is `EightBytes` (what `.nettrace` wants), so `.gcdump` needs an explicit
  override; getting it wrong misreads every reference rather than failing
  loudly. `Graph.cs` carries an `m_isVeryLargeGraph` flag that widens counts to
  int64 and labels to 8 bytes, but it is **never stored in the file** (it is a
  constructor argument), dotnet-gcdump's own reader hardcodes `FourBytes` and
  `new MemoryGraph(1)`, and nothing in dotnet-gcdump ever constructs one with
  the flag set. It is dead for any file that tool produces, and a reader could
  not detect it anyway.
- **Object count is not the limit; blob length is.** `nodeCount` is an int32,
  but `blobLength` is too, capping the node blob at 2GB - roughly 100M+ objects
  at typical per-node record sizes. Separately, **`dotnet-gcdump` itself caps a
  capture at 10,000,000 nodes** (a 12M-object process produced a dump with
  exactly 10,000,000, with `AverageCountMultiplier` still 1). That cap is the
  practical ceiling on real input, not anything in this reader.
- **`type.Size == 0` means the size is encoded per node** (arrays, strings);
  otherwise it comes from the type table. `dotnet-gcdump report`'s own
  "Object Bytes" column is `type.Size` - the TYPE's size, not the type's
  total - so it does not sum to the heap and only equals `size x count` for
  fixed-size types. Its **Count** column is the real per-type oracle.
- **The root is synthetic.** `RootIndex` names a `[.NET Roots]` node of size 0
  whose children are root categories (`[static var ...]` and friends, also size
  0). `dotnet-gcdump report`'s "GC Heap objects" is the RAW node count and
  therefore includes it; `TypeCensusBuilder` excludes it, so the expected
  relationship is `theirs == ours + 1`, asserted explicitly in
  `GcDumpReaderTests.cs` rather than papered over.
- **Real captures contain unrooted objects** - 7-9% of nodes on every file
  checked, all with no incoming references at all. Their retained sizes are
  necessarily 0, so the count is reported in the UI rather than swallowed.
- **Lengauer-Tarjan, not Cooper-Harvey-Kennedy, for dominators.** CHK is the
  obvious choice (flat-array sweeps, trivially simple) and was tried first; it
  took **108 seconds** of a 110 second run on a real 10M-node/29.8M-edge dump.
  Its `Intersect` walks two nodes up the dominator tree, and heap graphs are
  deep (any linked list) with high-fan-in shared nodes (a pooled buffer), so
  that product blows up on exactly the shape real heaps take - unlike the
  shallow control-flow graphs CHK's own paper measures. LT has no such step:
  same dump, **~4 seconds**, identical retained sizes. Both the DFS and LT's
  `COMPRESS` must be **iterative** - the graphs that made CHK slow are deep
  enough to overflow the stack.
- **Retained bytes per type sums only "outermost" instances** (those whose
  immediate dominator is not itself of that type). Summing over ALL instances
  is the obvious implementation and is badly wrong for self-nesting types: on a
  480MB heap of 9.9M linked nodes it reported **633GB** for the node type,
  because a list's tail is counted once per element ahead of it.
- Everything shipped to the webview is aggregated to the **type** level, so a
  237MB / 10M-object dump leaves as **53KB of JSON** (a type-rich 445KB dump
  produces 829KB). That is why there is no DNIBIN binary sidecar here even
  though the `.nettrace` path has one - there is nothing for it to save.
  Type names are interned into a dense pool for the same reason the `.nettrace`
  path interns method names.

#### Memory, and what did and did not help

`--gcdump-from-trace` is memory-bound, not CPU-bound. On a real
12M-object/35.8M-edge heap (814MB capture) peak RSS started at **5.15GB** and
is now a **~2.2GB median of 5 runs** (range 1.8-2.3GB), with byte-identical
output. What worked, and what backfired:

- **Exactly-sized allocations beat growable ones, by more than their own
  size.** A `List<T>` reaching hundreds of MB by doubling holds the old array
  alive while allocating one twice as large. Counting first (one extra pass
  over payloads already in memory) removed those spikes from the edge buffer,
  the node arrays and the node blob. `GcDumpWriter.MeasureBlobLength` exists
  purely for this and must stay in lockstep with `NodeBlob.Build`.
- **Resolving edge targets to node indices during the pass, not after,**
  deleted a 35.8M-entry `ulong[]` (286MB) outright rather than shrinking it.
  It works because node indices are handed out in definition order, which is
  the order the flat edge stream follows - so edges land directly in their
  final CSR slots.
- **`AddressToIndexMap` instead of `Dictionary<ulong, int>`** - 470MB to
  201MB at 12M entries, no resize copies (see that file's header).
- **Streaming the capture instead of materializing it made things WORSE, and
  was reverted.** Consuming events per block (never retaining ~800MB of block
  buffers) sounds strictly better and is not: the three decode passes then
  need three reads, tripling the read phase's allocation churn. Under this
  project's Server GC that took collections from 2 to 32, GC pause from ~4ms
  to ~2s, wall clock ~7s to ~12s, and peak RSS UP. Holding the buffers is
  cheaper than re-allocating them. The `NettraceFile.Read` / `EventBlock`
  callback hook added for this was reverted too.
- **Forcing `GC.Collect` before the write phase did not help** (measured
  ~3.2-3.7GB across 3 runs vs ~2.2GB without). The peak happens during
  read+decode; a collect afterward is too late and the compaction itself
  commits pages.

Peak RSS is genuinely noisy here - Server GC commits lazily, so single runs
vary by ~500MB and a 1-vs-1 comparison proves nothing. Measure a median of
several, under `/usr/bin/time -l` (`Process.PeakWorkingSet64` returns 0 on
macOS, which is why the `Timing:` line reports working set at exit instead).


#### Speed

Same 12M-object capture, Release build: **7372ms -> ~3500ms median of 5**, with
identical output. Two changes account for almost all of it:

- **`GC.AllocateUninitializedArray` for `EventBlock`'s per-block buffer.** The
  next statement overwrites every byte from the stream, so the zeroing `new
  byte[]` does first is pure waste - and at ~800MB of blocks it was the single
  largest item in a CPU profile (~33% of the run, attributed to allocation and
  first-touch). This one is in the SHARED read path, so every nettraceParser
  mode benefits, not just `--gcdump-from-trace`. It also collapsed GC pause
  from ~1900ms to ~3ms and collections from [32,5,2] to [2,1,1].
- **A one-entry cache in front of `TypeTableBuilder.IndexOfTypeId`.** Objects
  of a type arrive in runs, so most of the 12M per-capture lookups become a
  comparison; the dictionary was ~3.6% of the whole run.

- **Writing the node blob eight bytes at a time instead of one**, in
  `Graphs.MemoryGraph.ToStream`. `Serializer` has no bulk byte write (the
  reader side has `Read(byte[], int, int)`; the writer side has no
  counterpart), and adding one would mean editing vendored FastSerialization.
  Widening to `long` needs no such change and cuts ~139 million interface
  calls to ~17 million. Controlled A/B on the same binary state: write phase
  737ms -> ~550ms, output **byte-identical** (`cmp` clean). This is what
  dotnet-gcdump's own `Graph.FromStream` already does on the read side, and it
  uses `Unsafe.ReadUnaligned<long>` so the native-endian load pairs with
  `MemoryStreamWriter.Write(long)`'s native-endian store.

Sub-phase timings (Stopwatch, not sampling): pass 2 (define nodes)
~650ms, allocation of the CSR edge array ~690ms, pass 3 (resolve edges)
~1050ms, blob measure+build ~370ms, serializer/file write ~550ms.

Two things that did NOT work, both measured:

- **Hoisting the big allocations to the top of `BuildGraph` made it slower**
  (4192ms vs 3613ms). Acquiring ~190MB of fresh pages from the OS costs the
  same wherever it happens - a single 143MB `AllocateUninitializedArray`
  measured ~690ms on its own - but doing it while the 800MB trace is live and
  the node arrays are still empty gave the runtime a worse moment to grow the
  heap. Allocation placement is not free to move around at this scale.
- **`AllocateUninitializedArray` on the decode's own node arrays changed
  nothing measurable.** They are large enough to come straight from fresh OS
  pages, which arrive zeroed anyway; the win in `EventBlock` came from the
  sheer volume (800MB across ~1200 blocks), not from the API itself.

Profiling notes: self-profile with `dotnet-trace collect -- <nettraceParser
...>` and read the result with nettraceParser's own CPU view. Trust the SELF
percentages - the caller-tree totals came out inconsistent (two sequential
methods each reporting ~80% inclusive), so attribution by stack is unreliable
on these captures. Sub-phase `Stopwatch` instrumentation was what actually
localised the cost.

Measure the RELEASE build. Debug and Release differ enough to mislead: the
same input peaked at ~2.2GB median under Debug and ~2.9GB median under Release,
and Release is what `pack.py` ships.


Ground truth is `dotnet-gcdump report` itself, driven from
`nettraceParser.Tests/GcDumpReaderTests.cs` behind `GCDUMP_GROUNDTRUTH_FIXTURE`
(same opt-in shape as `NETTRACE_GROUNDTRUTH_FIXTURE`):
```
GCDUMP_GROUNDTRUTH_FIXTURE=~/path/to/some.gcdump \
  dotnet test --filter GcDumpReaderTests
```
`testApps/GcDumpObjectGraphGenerator` builds a retained graph of a chosen
object count (deep chains + shared payloads, so retained size and own size
genuinely differ) to capture large fixtures against.


## Extension rendering (`dotnetInsights/src/`)

- `GcSnapshotRenderer.ts` holds the chart/summary-tile rendering shared by
  both static-GC-snapshot sources: `DotnetInsightsGcSnapshotEditor.ts`
  (`.gcinfo`/Perfview XML) and `DotnetInsightsNettraceEditor.ts`
  (`.nettrace`, shells out to `nettraceParser --json`). Don't duplicate
  rendering logic into a new editor — extend this shared function instead.
- The two sources produce different `DateTime` shapes and downstream code
  must handle both: `.gcinfo` has no absolute time anchor, so it synthesizes
  an elapsed-since-capture string starting with `+` (e.g. `+00:01:23.456`);
  `.nettrace` produces an absolute local-time ISO 8601 string. Anything that
  formats `DateTime` (see `media/snapshotGcStats.js`'s `formatGcAxisTime`)
  must branch on a leading `+` rather than assuming one shape.
- Charts use **Chart.js 2.x**, not 3+ — `scales.yAxes` (array), value not
  `scales.y` object; `tooltips.callbacks.title`, not `plugins.tooltip`. A
  label entry can be an array of strings, which Chart.js 2.x renders as
  stacked lines under one tick — used to show the GC number and its time
  together on the x-axis without dropping the GC number.
- **Ranked-table column widths** (`.cpuHotMethodsTable` — CPU Hot Methods,
  Contention Sites, Exception Types, each with a `.callerTreeInner` tree
  nested inside its expanded rows). One rule governs all of it: under
  `table-layout: auto` a **percentage** width is honored exactly, while a
  width in `em`/`px`/`rem` is a *floor* that soaks up the table's leftover
  space. So: numeric columns and the nested tree's numeric columns share
  `--rankedNumericColumnWidth` (a percentage) so both grids land on the same
  pixels; widths are declared on **header cells only** (enough to size a
  column in auto layout, no per-row markup) and key off the
  `data-sort="number"` attribute `renderSortableTableHeader` already emits;
  and the name column **stays `width: auto`**, which is the mechanism that
  makes it absorb the rest. Three measured wrong turns, kept because each is
  easy to repeat: (1) giving the name column a percentage was *worse than the
  original bug* — `.rowHideColumn`'s `1.6em` became the only non-percentage
  column and swallowed the remainder, 22px → 226px, so it needs its own `2%`;
  (2) sizing numerics in `rem` made them grow to 192px from a declared 123px;
  (3) an `em` inside `.callerTreeInner` resolves against **~26.5px**, not a
  header cell's 14px, which is why `.bytesColumn`/`.percentColumn` (`9em`/
  `6em`) measured 239px/159px and never lined up — scope any override to
  `.cpuHotMethodsTable .callerTreeInner`, since those classes are also worn by
  the Heap Contents and Allocation drill-down tables. The percentage must
  clear the widest numeric header's min-content (~115px, "Total Wait (ms)")
  and headers must stay free to wrap, so a narrow window wraps a header rather
  than forcing its column wider and breaking alignment.

## Tool distribution & the stale-cache trap

`DependencySetup.ts` downloads each helper tool's release asset into VS
Code's `globalStorageUri` folder once, then **never re-downloads it** unless
a version-marker file — named `<versionConstant>-<tool>.txt`, content is
just that same version string — is missing or contains a different value.
The version constants (`latestNettraceParserVersionNumber`,
`latestListenerVersionNumber`, `lastestVersionNumber`, ...) live in
`extension.ts`.

**The trap**: re-uploading a fixed binary to the *same* release tag via
`gh release upload --clobber` does not change that version string, so every
machine that already downloaded the old binary keeps silently using it
forever — this cost a full debugging round-trip in this repo already (a
DateTime rendering fix looked broken because the locally cached
`nettraceParser` binary predated the fix, even though the JSON it produced
and the webview code were both already correct).

When shipping a fix to an already-released tool version, either:
1. Bump that tool's version constant in `extension.ts` (forces every user to
   redownload — the correct long-term fix, not yet done for the churn during
   `nettraceParser`'s initial bring-up), or
2. For local testing only, delete the cached copy so it redownloads:
   `rm -rf "$HOME/Library/Application Support/Code/User/globalStorage/jashoo.dotnetinsights/<tool>" "$HOME/Library/Application Support/Code/User/globalStorage/jashoo.dotnetinsights/<version>-<tool>.txt"`
   (or set `dotnet-insights.<tool>Path` to a local build directly).

## Testing

Real `@vscode/test-electron` + Mocha integration tests, not mocked —
**must be `@vscode/test-electron@3.1.0`**, `3.0.0` resolves the wrong
executable path (`Contents/MacOS/Electron` instead of `Contents/MacOS/Code`)
and every launch fails. `src/test/runTest.ts` must
`delete process.env.ELECTRON_RUN_AS_NODE` before spawning — if that env var
is set globally in the shell (as it was in this environment), the spawned
VS Code process runs as plain Node and rejects every CLI flag.

`src/test/suite/gcStatsCalculations.test.ts` cross-checks pure calculation
logic against both synthetic data and a real `nettraceParser --json` fixture
(`src/test/suite/fixtures/nettrace-gcdata.json`, 140 real GCs) — prefer
extending this real-fixture pattern over adding purely synthetic tests when
touching GC data shapes, since several real bugs here (QPC timestamp domain,
GC event correlation order) were only visible against real capture data.

Commands (run from `dotnetInsights/`): `npm run compile`, `npm test`.

### Ground-truth diff testing (`nettraceParser.GroundTruth`)

`nettraceParser.GroundTruth` is a small standalone project (`dotnet run --
<file.nettrace> [--json out.json]`) that reads a `.nettrace` file via
`Microsoft.Diagnostics.Tracing.TraceEvent` (`Analysis.GC.TraceGC`/
`TraceGarbageCollector`) instead of `nettraceParser`'s own hand-rolled
decoder — the one project in this repo deliberately allowed to depend on
TraceEvent, since the whole point is to be an independent second
implementation built on the same library PerfView's GC Stats view uses.
`nettraceParser.Tests/GroundTruthDiffTests.cs` diffs `GcEventProjector`'s
output against it field-by-field (generation, reason, heap sizes, promoted
bytes, pause timing, ...) and separately diffs `AllocationEventProjector`'s
resolved stack leaf frames against a second reader
(`TraceEventAllocationReader`, which needs its own `.etlx` conversion via
`TraceLog.CreateFromEventPipeDataFile` since stack-walking only works on
TraceLog's played-back event stream, not raw `EventPipeEventSource` —
expect this half to take noticeably longer on a large capture). Both are
gated behind the `NETTRACE_GROUNDTRUTH_FIXTURE` env var (a local file path)
so they're a silent no-op by default and never need a fixture checked in:
```
NETTRACE_GROUNDTRUTH_FIXTURE=~/path/to/some.nettrace \
  dotnet test --filter GroundTruthDiffTests
```
This is how the timestamp-decode bug, the `GCHeapStats` misattribution bug,
the `PauseDurationMSec` semantic gap, and the StackId-recycling bug above
were all found and confirmed fixed — none of them were visible from
`nettraceParser`'s own pinned-value tests (`RealCaptureTests.cs`), because
those pins were derived from `nettraceParser`'s own (buggy) output in the
first place. Prefer extending this diff test over adding another
synthetic/pinned-value test when the
question is "does this match what PerfView would show," not just "does this
match what this code already computes."

## Known open items

- `DotnetInsightsNettraceEditor.ts` still registers its webview with
  `languageId: "ildasm"` (copy-pasted from the PMI/disassembly editor this
  was modeled on) — flagged as misleading but the rename was never actioned.
- Tool version constants in `extension.ts` are not bumped on every binary
  re-upload (see "stale-cache trap" above) — several `nettraceParser` fixes
  shipped this way during initial development.
