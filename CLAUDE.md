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

### Thread classification (`nettraceParser/Threading/ThreadActivityProfiler.cs`)

The Threading view's tables all answer "what stack was this thread in at time
T", and on a real service that question is mostly answered by threads that are
parked ON PURPOSE - native poll loops, message consumers, watchers, the
runtime's own gate and timer threads. They look blocked in every window ever
taken and explain none of them, so every table was topped by them. Each thread
is now classified over the WHOLE capture and the tables read that
classification.

Hard-won facts, all measured against three real service captures
(ads-retrieval 836MB/66 threads, asset-delivery 1.39GB/132, assets-registry
3.23GB/518):

- **`ThreadSampleType` is the load-bearing signal, and it was being thrown
  away.** The `Microsoft-DotNETCore-SampleProfiler` event's 4-byte payload is
  the CLR's own `SampleProfilerSampleType` (`Error`/`External`/`Managed`);
  `SampleProfileEventProjector`'s header used to say the event carried no
  payload worth decoding, which was true only for the CPU view's needs.
  `External` means the thread was NOT executing managed code - in a P/Invoke,
  the runtime, or blocked in a syscall. This is the only thing in the capture
  that identifies a thread parked in a native call, because its MANAGED leaf
  frame reads like ordinary running code: six
  `Grpc.Core.Internal.GrpcThreadPool.RunHandlerLoop` threads produced 222,240
  samples each, 100% External, zero managed, and `CpuIdleWaitClassifier`
  scored all six as "100% running". No list of library method names can ever
  keep up with this (Kafka, gRPC, `FileSystemWatcher`, ...) and the runtime
  already knows the answer. **`External` alone is NOT "blocked"** - 92.6% of
  all samples on the ads-retrieval capture are External, because any thread
  doing socket or file work is mostly in native code.
- **Benign-parked needs all four gates**, and they are set where a false
  POSITIVE costs most (hiding the one thread that mattered): contention
  accounting for `< 1%` of the thread's life, `managedFraction <= 0.05`,
  `topStacksShare >= 0.95`, and enough evidence to say so (>= 50 samples,
  >= 1000ms observed). The managed fraction threshold sits in a measured empty
  band - working threads clustered at 0.058-1.0, parked ones at 0.0-0.021,
  nothing in between. An earlier 0.02 split the parked cluster itself.
- **A parked worker is a small LOOP, not one frozen stack** - the
  concentration gate is over the top THREE stacks, not the top one. A real
  `Roblox.Coordination.BackgroundWorker.Run` drain worker parks on an empty
  `BlockingCollection` 90.9% of the time, sleeps out its poll interval a
  further 6.4%, and spends 1.0% in the blocking call that ships the batch:
  98.2% across three stacks, 90.9% in the top one. Judging it on the top stack
  alone called it `Blocked`, along with ~45 sibling queue-drain threads on
  another capture. Across all three captures every non-pool thread that should
  be benign sits at 0.982-1.000 on the three-stack figure.
- **Contention evidence must be MATERIAL, not merely present.** "Any
  contention event at all" was validated only against captures whose parked
  threads happened to have exactly zero - luck, not a property. That same
  drain worker carries 157 contention events worth 8.3ms across a 300-second
  life: 0.0028%, ambient lock traffic explaining none of its idleness. 1% of
  the thread's own observed life sits in a measured gap - non-pool threads
  that should be benign account for 0.0000-0.33%, everything genuinely stuck
  starts at 1.09%.
- **A pool worker can only ever be benign by being parked in the pool's OWN
  park**, and this is the one place where looking *more* parked makes a thread
  *more* interesting rather than less. Getting it backwards hid the single
  best finding in a reference capture: four threads running a synchronous
  `Confluent.Kafka.Consumer.Consume` inside an `ExecuteAsync`, rooted in
  `PortableThreadPool+WorkerThread.WorkerThreadStart` ->
  `ThreadPoolWorkQueue.Dispatch`, each pinned for the entire 300-second
  capture - textbook pool starvation, previously labelled benign `Parked`
  because they sat so still. Behaviourally they are indistinguishable from the
  benign dedicated drain worker above; the difference is entirely **whose
  thread they are standing on**, which is why `isPoolWorker` has to be right
  (hence the majority-of-samples test) and why it is checked before anything
  behavioural.
- **`ParkShareOfWait` answers the pool-worker question on BOTH paths.** A
  parked-looking worker and a busy-looking one are the same population at
  different duty cycles, so measuring them differently puts a seam through the
  middle of it: gating the parked path on `PoolParkFraction >= 0.95` instead
  split one capture's idle workers at 0.937/0.939 versus 0.956 - three
  readings of the same thing.
- **Classify the runtime's own parked threads by IDENTITY, not behaviour.**
  `TimerQueue.TimerThread`, `PortableThreadPool+GateThread.GateThreadStart`
  and `PortableThreadPool+WaitThread.WaitThreadStart` all park by design. The
  timer thread carries `TimerQueue` on its stack (so it reads as a pool
  worker), parks in `WaitHandle.WaitOneNoCheck` rather than the pool's
  semaphore (so it is not an idle worker), and wakes on every tick to run real
  managed code - 11.8% of its samples - so the parked test misses it too.
  Behaviour alone therefore lands it on `BlockedPoolWorker`, the loudest label
  the view has, on a thread doing exactly its job. Confirmed on a real
  capture. The pool's actual WORKER entry point is deliberately not on that
  list: a starved worker is the finding, not the noise.
- **The exclusion is per THREAD, never per frame**, and there is a direct
  proof in the data: `Confluent.Kafka.Consumer.Consume` appears in both the
  actionable stall table (108 samples, from the one consumer that does real
  work) and the excluded one (431 samples, from four that are parked in it).
  Any method-name filter has to be wrong about one of them.
- **`IsPoolWorker` is a fraction, not a flag**, and it is load-bearing for the
  rule above. An "any sample ever" test marked a gRPC handler thread a pool
  worker off 8 samples in 222,240 because it queued a work item once.
- **`ParkShareOfWaitForHealthyWorker = 0.75`** separates a pool worker parked
  between work items from one stuck elsewhere. Measured on captures that each
  contain only one population, which is what makes the gap trustworthy:
  healthy 0.872-0.951 (ads-retrieval, 34 workers), blocked 0.039-0.729
  (assets-registry, 6 workers, each independently corroborated by 30ms-4,909ms
  of real contention wait).
- Excluded samples are **shown under their own heading**, not dropped. A
  filter that cannot be audited is one nobody believes the first time it hides
  something they expected - and on the ads-retrieval capture this filter sets
  aside half of every sample in the file.
- With no `ThreadSampleType` in the capture, **nothing** is classified as
  parked (`HasSampleTypeData`) and the view says so, rather than falling back
  to the leaf-frame heuristic this whole file exists because of.
- Cost: one extra pass over every CPU sample, reported as `threading=` on the
  `Timing:` line and 341-591ms across the three captures. A run-length front
  end on the per-thread stack histogram (parked threads emit the same stack in
  long runs) took that from 750-791ms to 586-611ms on the 3.23GB capture with
  byte-identical output. **Measured and declined**: folding the leaf's
  idle/wait answer into the per-stack memo saves less (to 661-678ms) and would
  pin each leaf's resolution to first use, where every other consumer resolves
  per sample - the whole-stack scans are memoized that way only because they
  ask about a stack's shape, which does not move.

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
The export phase's own 6 sub-writer weights are
the one place a per-item estimate compares genuinely comparable
things — 6 writers in the same phase of the same run — so `GcJsonExporter.
WriteToFile` computes that split dynamically from THIS run's own real
counts rather than a fixed constant.

Only `Cpu/CpuProfileJsonExporter.cs`, `Gc/AllocationJsonExporter.cs`
(`AllocationSummaryBuilder.Write`) and `Threading/ThreadActivityProfiler.cs`
get internal fine-grained progress tracking within the export phase — the
other three sub-writers (exceptions, contention, the `gcData` array) are small
enough on every capture measured so far (all under ~1% of total time) that a
start/complete snap is visually indistinguishable from tracking them
internally. The threading writer crossed that line when the thread
classification landed: it went from a rounding error to 341–591ms (~10% of the
run), which is long enough for a frozen bar to be noticed. Every per-event loop's
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
down as `export=Xms(alloc=..,exc=..,cpu=..,cont=..,threading=..,gc=..)`.

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
keeps it alive) rather than an event stream. The webview presents it as three
ranked tables - Type Census, Retained Size, References - each row expanding
into the retention/reference tree for that type; see "The ranked table is ONE
component" below for the shared component all of them are built from. It lives inside nettraceParser
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

### Core dumps (`nettraceParser/CoreDump/`) — the only source that survives load

`nettraceParser --gcdump-from-dump <core.dmp> [-o out.gcdump] [--json out.json]`
builds the same `HeapGraph` from a process core dump via **ClrMD**
(`Microsoft.Diagnostics.Runtime`), so the dominator pass, census, root-path
trie, `.gcdump` writer and the whole webview are shared with the other two heap
sources and know nothing about where the graph came from. The extension opens
`.dmp`/`.core` through the same custom editor as `.gcdump`
(`isProcessDumpPath` in `DotnetInsightsGcDumpEditor.ts` picks the flag).

**Why this exists.** Both event paths depend on the runtime walking its own heap
while the process runs on, and that is only trustworthy on a QUIET heap.
Measured, on a real production ASP.NET service captured with
`dotnet-trace ... 0x1980001:5`: the dump named **1,798,821 addresses it never
described** (35.4% of its 10.16M references), **26,006 of 41,919 roots** dangled,
and **1.5% of the heap was reachable** — so every retained size and every
retention path in it was worthless, while the type census was fine. Ruled out
by measurement, not assumption: no dropped events (block sequence numbers ran
contiguously), not truncation (the dump finished 45s before the capture ended),
not a decode bug (zero unresolved addresses were interior to a described
object). Bucketed by 4MB, the heap split cleanly — **147 regions fully
described, 246 never described at all** — so the runtime enumerated part of the
heap. Reproduced on a churning test heap, where `dotnet-trace` at level 5,
level 4 with a 1GB buffer, and `dotnet-gcdump collect` all produced equally
unusable root sets. There is no capture flag that fixes it; freezing the
process is the fix.

Hard-won facts:

- **`AddressToIndexMap` never resizes, by design.** Seeding it with a guess does
  not degrade, it HANGS — a full table under linear probing spins forever on the
  next insert (cost me a 10-minute 100%-CPU mystery). Hence walk 0, which counts
  objects and touches no references, purely to size it.
- **Three walks, exact allocations**: count objects → assign indices and count
  each object's references into a `ChunkedIntList` (blocks, never doubling) →
  fill nodes and write each edge straight into its final CSR slot. More than one
  walk because CSR needs every child count before any child can be written;
  buffering 8-byte target addresses instead is the 286MB-at-35M-edges cost the
  trace path already removed.
- **`ClrObject.IsFree` must be excluded, in all three walks identically.** ClrMD
  enumerates the GC's free-list placeholders (that is how it walks a segment
  linearly) and `dotnet-gcdump` does not: on the verification dump they were
  86,626 objects and 2.8MB of a phantom `Free` type, and if the walks disagree
  about what an object is, the count that sized the address map no longer
  matches what gets inserted into it.
- **Stack roots crash the DAC on a macOS Mach-O core dump.** Verified on a real
  .NET 10 dump with ClrMD 3.1 AND 4.1: handles, the finalizer queue, objects and
  references all enumerate fine; `EnumerateStackRoots` SIGSEGVs on the first
  thread. A native crash is not catchable from managed code, so this has to be
  decided BEFORE the walk — `ShouldDefaultToSkippingStackRoots` sniffs the
  Mach-O magic (0xFEEDFACF) off the DUMP, not the host, because a Linux dump
  read on a Mac still has readable stacks. Losing them costs less than it
  sounds: handles cover statics and every GC handle, and on the verification
  dump handle + finalizer roots alone reached 100,003 of 100,003 objects in the
  retained graph under test. `GcDumpMetadata.StackRootsOmitted` carries the
  caveat into the JSON and the webview says so, because "some objects are
  unrooted" and "one whole category of root was unreadable" otherwise look
  identical.
- **Root CATEGORIES are rebuilt here** (`[strong handle]`, `[pinned handle]`,
  `[other handle]`, `[finalizer queue]`, `[thread stack]`), each a size-0 node
  between `[.NET Roots]` and its roots — the shape real `dotnet-gcdump` output
  has and the thing the trace path loses entirely. A retention path that ends
  "held by [.NET Roots]" says nothing. Empty categories are still emitted:
  "looked, found none" is not the same as "never looked". Module names come from
  `ClrType.Module` and are likewise unavailable on the trace path.
- **Weak handles are not roots** — `ClrHandle.IsStrong` filters them, or the UI
  would report objects as retained by the one thing that explicitly does not
  keep them alive.
- **ClrMD is an allowed dependency here, unlike TraceEvent for `.nettrace`.**
  That refusal is about a documented file format worth hand-rolling; a core dump
  is read by asking a private, versioned runtime contract questions through the
  DAC, and there is no hand-rolled version of that worth writing. Only
  `CoreDump/` touches the package.
- **The DAC must match the dumped runtime**, so a Linux dump is most easily
  converted on Linux (`--dac` overrides). Missing-DAC is reported as an error
  message on the stack, not an exception — the CLI prints one line saying what
  to do.
- Verified end to end on a real 402MB macOS core dump (301,160 objects):
  **0 unresolved references, 0.2% unreachable** (versus 98.5% for the same class
  of heap through the trace path), retained sizes correct through the dominator
  tree. Test coverage is `CoreDumpHeapGraphBuilderTests` behind
  `CORE_DUMP_FIXTURE` (same opt-in shape as the other fixture-gated tests), and
  it asserts STRUCTURE — every edge lands on a real node, CSR offsets are
  monotone, nothing is unresolved, no object carries the placeholder type —
  rather than pinning counts that change with every dump. To produce a dump on a
  machine where `dotnet-dump collect` cannot attach (macOS refuses
  `task_for_pid` without entitlements), let the runtime write one itself:
  `DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=2 DOTNET_DbgMiniDumpName=…`
  then crash the app.

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


### NetTrace v6 (`nettraceParser/V6/`) — what `dotnet-trace collect-linux` writes

`dotnet-trace collect-linux` collects through Linux `perf_events` + the
kernel's `user_events` mechanism, machine-wide by default, and writes
**nettrace v6** — a BREAKING format change, not a version bump within one
layout. v6 deletes the FastSerialization layer outright: no per-object type
names or versions, no object header/footer, no stream-offset alignment
padding. What is left is a flat sequence of blocks, each introduced by a
4-byte header (`uint24 BlockSize`, `uint8 BlockKind`), and strings move from
UTF-16 to UTF-8.

Spec: perfview's `src/TraceEvent/EventPipe/NetTraceFormat.md` (v6) and
`UniversalProviders.md` (the `Universal.*` event definitions). Fetch them from
the repo rather than re-deriving — both are short and authoritative.

- **A v6 file fails inside vendored FastSerialization, not in this project's
  code.** The `Nettrace` magic matches and the very next thing v5 expects —
  the `"!FastSerialization.1"` signature — is absent, so `Deserializer.
  Initialize` throws *"Not a understood file format"*. That reads like a
  corrupt capture rather than an out-of-date reader, which is why the version
  constant bump matters more here than usual (see the stale-cache trap).
- **Detection is the format's own rule, not a heuristic**: v6 follows the
  magic with a `uint32 Reserved` that is always 0, and in a v5 file those same
  4 bytes are the length prefix of `"!FastSerialization.1"` — the constant 20.
  `V6Format.ReadMajorVersion` reads exactly that.
- **`EventBlock.HeaderSize` INCLUDES its own `uint16`; `MetadataBlock`'s
  EXCLUDES it.** Both are as documented. Getting it backwards does not fail —
  the compressed event header is self-describing, so the reader happily
  decodes garbage into complete-looking events. The first version of this
  reader produced 11,077,123 events with a 46-hour timestamp span instead of
  the correct 11,274,185 over 299.6s. Pinned by
  `V6ReaderTests.V6Reader_EventBlockHeaderSizeIncludesItsOwnField`.
- **`varint` is ZIGZAG encoded** (`(value >> 1) ^ -(value & 1)`), not
  sign-extended. `varuint` is plain ULEB128.
- **Three id spaces recycle across sequence points, not one.** v5 had
  StackId; v6 adds `ThreadIndex` (into a table built by `ThreadBlock` and torn
  down by `RemoveThreadBlock`) and `LabelListIndex`. All three are resolved
  EAGERLY at event-parse time, for exactly the reason the v5 StackId bullet
  above exists — a deferred lookup resolves against whatever last claimed the
  number.
- **v6 events carry no thread id, only a `ThreadIndex`.** `EventRecord` keeps
  its `ThreadId`; the reader resolves it. A pid is NOT added to `EventRecord`
  (a struct that exists 35M+ times over — 4 more bytes is hundreds of MB);
  `V6ThreadTable` keeps a `threadId -> processId` map instead, which works
  because Linux allocates tids from the same namespace-wide space as pids.

#### The metadata is nearly empty, and that silently empties whole views

On a real 764MB reference capture (a containerized ASP.NET service,
299.6s, 11,274,185 events) **every one of the 33
`Microsoft-Windows-DotNETRuntime` metadata rows carries a ProviderGuid and
nothing else** — no Version, no Level, no Keywords, and an empty
`FieldDescriptions`. That is legal and follows from how the events get there:
the runtime emits them through `user_events` as opaque payload blobs, so the
writer knows the numeric event id but has no manifest to describe it with.

This does not matter for payload decoding — the payload bytes are the
standard CLR ETW layouts and this project already decodes those from raw
bytes by manifest offset (`GCStart` 26 bytes, `GCEnd` 10, `GCHeapStats` 110,
all verified). It matters enormously for **Version**, because the decoders are
version-gated: `ExceptionEventProjector` drops anything below 1,
`AllocationEventProjector` below 2, `GcEventProjector` decodes
`GCPerHeapHistory` only at 3+, and `ClrGcEnd`/`ClrGcSuspendEEBegin` read a
field as int32 at 1 and int16 below it. Taking metadata at face value would
have silently discarded all 72,674 exceptions in that capture and mis-decoded
its GC events — no error, just empty views.

`V6ClrEventVersions` resolves it, in order: an explicit `Version` LABEL on the
event (v6 lets a LabelList override metadata, and the reference capture's
`GCPerHeapHistory` events really do use this, carrying Version=3), then the
metadata row's own Version, then a table. **The table was generated, not
guessed**: a v5 capture's metadata carries BOTH the event id and the version
the runtime emitted it at, so v5 captures are the authoritative source. Run
this tool's own plain mode over one — the `== Event schemas ==` section exists
for this — and take the union across several. Four real captures agreed on
every id they shared. A payload-length ladder then only ever *lowers* that
version, for the four events where the version selects a field WIDTH rather
than appending fields, which is the one case the decoders' own
`version >= N && Length >= M` guards cannot catch.

- **CLR event names are absent in v5 too**, so this is not a regression: a v5
  capture's CLR metadata rows have an EMPTY `EventName` (the provider is
  manifest-based). v6 supplies `"Unknown(57)"`, which is strictly more
  informative. No id→name table was needed — `Overview/ClrEventNames.cs`
  already handles display.

#### 40% of the reference capture is one repeated error label

Its LabelList blocks are **305MB of 764MB** — 10,172,583 label entries against
11,274,185 events — and 10,171,358 of them are one repeated string pair,
`Error` = `"Expected actual values"`, attached to every event whose field
layout the writer could not describe. It is the writer saying "here is a raw
blob instead of fields", consistent with the empty `FieldDescriptions` above,
and harmless to decoding.

`V6LabelListTable` therefore **walks label strings and skips their bytes
rather than decoding them**, and stores an entry only when a label list
actually carries an override — 640 entries instead of 10.2 million on that
capture. The `Error` labels are still COUNTED (comparing raw UTF-8, no
allocation) because "40% of your file is a writer-side annotation" is worth
being able to say out loud.

#### `Universal.System` is the symbol table, and it covers managed code too

A v6 capture's stacks are raw addresses spanning kernel, native and JIT'd
managed code, and **none of them resolve through `Rundown/MethodSymbolTable`**
— that table is built from `MethodLoadVerbose`/`MethodDCStartVerbose` (ids
143/144), which a collect-linux capture does not contain. Before
`Universal/UniversalSymbolTable.cs`, all 6,019 distinct frames in the CPU view
rendered as `<unresolved 0x…>`.

- **`ProcessSymbol` addresses are ABSOLUTE virtual addresses**, despite the
  spec wording ("within the mapping"). Verified by resolving the capture's own
  hottest frames — `0xFFFFFFFF8C29DFAB` → `finish_task_switch.isra.0`.
- **`ProcessSymbol` covers MANAGED methods**, published by the runtime in the
  CLR's perf-map form (`instance void [Asm] Ns.Type::Method(object)
  [OptimizedTier1]`). That is why this one table is enough.
- **Only a minority of mappings carry symbols** — 42 of 561. The rest are
  stripped shared objects and managed assemblies, so the fallback is
  `module+0xoffset` (`libcoreclr.so+0x433627`), which is a real groupable
  identity rather than a bare address. Result on the reference capture: **0 of
  3,432 distinct frames unresolved.**
- **Binary search must compare UNSIGNED.** Kernel addresses have the top bit
  set and read as negative `int64`, which sorts them below every user-space
  address and makes a signed search miss every kernel symbol.
- **`"contains ::"` does NOT identify a managed name** — C++ symbols are full
  of it. An earlier `FormatSymbolName` keyed only on `::` and rewrote
  `icu_78::CollationKeys::writeSortKeyUpToQuaternary` into the mangled hybrid
  `icu_78.CollationKeys::writeSortKeyUpToQuaternary`, which is neither the
  real symbol nor a valid managed name. The `[Assembly] ` bracket is what
  identifies the CLR form; native symbols have none, so requiring it leaves
  all of them untouched. The signature's `(` must also be searched for AFTER
  the `::`, since a return type can carry parentheses (`modreq(...)`).
- **Frame ids must be keyed by NAME, not by address.** A symbol covers an
  address RANGE, so samples land on many addresses inside one function;
  `MethodSymbolTable`'s unresolved path minted an id per address, splitting
  one hot function across rows — `__pthread_mutex_lock` appeared twice at
  2,944 and 2,433 self samples instead of once at 5,377, and
  `System.Uri.CheckCanonical` was fragmented out of the top 20 entirely
  (it is #3 at 10,130 once merged). The resolved path already content-interned
  names for exactly this reason; the unresolved path now does too.

#### Native symbols: the capture carries a recipe, not the symbols

The frames a collect-linux capture cannot name are not a parsing gap - the
symbols are not in the file. Only 42 of the reference capture's 561 modules
ship any, and `libcoreclr.so` - which owns **5.4% of all CPU samples, more
than any single managed method** - ships none. What every module DOES carry is
a `ProcessMappingMetadata` record holding its ELF `build_id` and `debug_link`,
which is exactly the key a symbol server is looked up by. `nettraceParser/
Symbols/` turns that recipe into names.

- **THE MODULE OFFSET MUST BE THE ELF VIRTUAL ADDRESS, NOT THE MAPPING
  OFFSET.** `ip - mapping.StartAddress` is wrong and was shipped wrong for a
  day. A mapping starts at some offset INTO the module file (libcoreclr.so's
  text maps at file offset 0x1C8000) and the file has its own
  `p_vaddr - p_offset` bias, so the address anything else can use is:

      elfVirtualAddress = (ip - mapping.Start) + mapping.FileOffset
                          - p_offset + p_vaddr

  **Verified against ground truth, not derived and hoped for.**
  `libSystem.IO.Compression.Native.so` is the one module in the reference
  capture carrying BOTH in-capture `ProcessSymbol` entries and the metadata,
  so its six known symbol addresses can be run through each candidate formula
  and checked against the real binary (fetched by build id). The naive form
  scored **0 of 6**; this one scored **6 of 6**. The naive form does not fail
  visibly - it lands inside a *different* function, so it answers confidently
  and wrongly. Pinned by `NativeSymbolResolutionTests`.

- **Microsoft's symbol server has .NET's native modules, keyed by ELF build
  id**: `https://msdl.microsoft.com/download/symbols/_.debug/elf-buildid-sym-<id>/_.debug`.
  Verified end to end - the returned `libcoreclr.so.dbg` is 138MB, reports the
  exact build id the capture recorded, and names 31 of the 32 libcoreclr
  frames in that capture's top 200. It does NOT have distro libraries; libc,
  openssl and zlib 404 there and need a **debuginfod** server
  (`https://debuginfod.ubuntu.com/buildid/<id>/debuginfo`), which is a
  different URL shape and so is declared with a `debuginfod:` prefix.

- **The cache is keyed by build id, never by filename** - a cached file can
  then never be the wrong version of the right name, which is the failure mode
  that makes symbol caches lie. It also needs no invalidation rule: a
  different build is simply a different key. **Misses are cached too**
  (a `.miss` marker), and that marker is checked BEFORE the cached file, not
  after - a 404 page or truncated download saved to disk would otherwise be
  re-read and re-parsed on every open forever. That ordering bug was caught by
  a test, not by inspection.

- **Downloads are demand-driven and the SELECTION HAS NO SHARE FLOOR.**
  Modules are ranked by how many stack frames land in them and capped at 12.
  There was a 0.1% minimum-share floor and it was removed after it measurably
  lost symbols: the counts come from a strided sample (below), so a module near
  the floor lands on either side of it depending on which stacks the stride
  hit - dropping `libSystem.Native.so`, and 274 real symbols, between two runs
  over the same file. **A sampled count and a threshold do not belong
  together**; the cap alone bounds the work and is immune to sampling noise.

- **Ranking the modules cost 4,828ms before it was fixed** - longer than the
  entire rest of the parse - and the fix was NOT the obvious one. Two changes
  took it to ~390ms with identical output: walk a strided ~100,000-stack sample
  instead of all 936,389 (the counts only ever choose modules, and are never
  displayed), and search the MAPPING ranges first, consulting the 10,187-entry
  in-capture symbol table only for modules that actually have in-capture
  symbols - which the dominant module, libcoreclr.so, does not. Loading and
  parsing the 138MB ELF itself was never the cost: it measures ~14ms.

- **Symbols are demangled, but only a documented subset.** Itanium C++ mangling
  covers 228 of 3,198 resolved names on the reference capture and includes most
  of the runtime's GC and locking internals - the rows somebody is reading this
  view to find. `NativeSymbolDemangler` handles the ordinary shape (linkage/CV
  prefix, length-prefixed components, dropped parameter list), which decodes
  226 of those 228, and returns everything else UNCHANGED. A raw mangled name
  is honest and can be pasted into a real demangler; a half-rewritten one is
  neither. Same rule `FormatSymbolName` follows for CLR names, and for the same
  reason.

- **The name is the bare function, with no `+offset`**, matching how an
  in-capture `ProcessSymbol` resolves - otherwise one hot function splits into
  a row per sampled instruction, which is the same bug the name-keyed frame ids
  fixed above.

Cost and controls: `nativeSymbols=` on the `Timing:` line breaks out
`select=` (the ranking pass) from the fetch, because the two have completely
different characters - the ranking always costs the same, a fetch is a
download once and free forever after. Steady state on the reference capture is
~400ms. `--no-symbol-download` works entirely offline (still using whatever is
cached), `--symbol-cache <dir>` moves the cache, `--symbol-server <url>` adds
one. The extension exposes the first two as
`dotnet-insights.downloadNativeSymbols` and
`dotnet-insights.symbolServers`, and points the cache at its own
globalStorage. **The whole feature is a no-op on a v5 capture** - verified
byte-identical output, with `nativeSymbols=0ms`.

#### CPU samples and the derived `ThreadSampleType`

CPU samples arrive as `Universal.Events/cpu`, not
`Microsoft-DotNETCore-SampleProfiler`. **Match Universal events by NAME, never
by id** — `UniversalProviders.md` guarantees stable names and explicitly does
not guarantee ids ("There are no stable event IDs, but there will be a set of
stable names"). The reference capture assigns `cpu` id 2; another need not.
`EventOverview` is keyed by (provider, event id), so the id this capture chose
is discovered while reading metadata and surfaced as
`NettraceFile.V6UniversalCpuEventId` purely so the sample list can be presized.

The bigger consequence: **a perf-sampled capture carries no `ThreadSampleType`
at all**, and the entire Threading view's parked/blocked classification is
built on it. It is derived instead (`UniversalSampleTypeClassifier`) from
whether each sample's LEAF frame resolves to managed code — which is the same
question `ThreadSampleType` answers ("was this thread executing managed
code"), asked of a real symbol table. This is NOT the leaf-method-name
heuristic that `ThreadActivityProfiler`'s header warns about; that one guesses
what a method *does*, this one measures whether the sampled instruction
pointer was in managed code at all.

It is still labelled as derived all the way out to the webview
(`threadActivity.sampleTypeSource` = `"derived"` vs `"runtime"`, rendered as a
note in `ThreadingRenderer.ts`), because the parked/blocked THRESHOLDS were
calibrated against the runtime's own signal and have **not** been validated
against a v5 capture of the same process. Two known gaps on the reference
capture, both in the conservative direction (nothing is hidden that should be
read):

- `managedFraction` runs ~0.53-0.54 on its busiest threads, nowhere near the
  0.0-0.021 band CLAUDE.md's v5 captures showed for parked threads.
- `isPoolWorker` is false for every thread, because perf's unwind does not
  reliably reach the managed pool entry point (`PortableThreadPool+
  WorkerThread.WorkerThreadStart`) — the stacks bottom out in
  `__clone` → `libcoreclr.so+0x…`. Every thread therefore classifies as
  `Active`, which is a refusal to claim rather than a false "benign".

#### What is and is not in a collect-linux capture

Keyword selection at collect time, not a parsing gap: the reference capture
has **no `GCAllocationTick` (id 10) and no contention events**, so the
Allocation and Contention views are empty on it. GC, exceptions, CPU,
threading and the event overview all populate.

#### Verification

`--json` on the reference capture: 11,274,185 events, 32 GCs (ids
16363-16394, alternating gen0/gen1 every ~10s, 20 Server GC heaps, ~17.5GB
heap, 66-113ms pauses), 72,674 exceptions, 1,090,977 CPU samples, 0
unresolved frames. Event counts and the total match an independent Python
decode of the same file byte for byte.

**The v5 path is unaffected**, verified by diffing against a build of the
pre-change tree on a 15.2M-event v5 capture (12,386 GCs, 1.8M allocation
ticks, 10.6M samples, 1,376 contentions): the JSON's only difference is the
one deliberately added field (`sampleTypeSource: "runtime"`), and the 21.9MB
ticks sidecar is byte-identical.

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

### The ranked table is ONE component — use it, don't re-derive it

CPU Hot Methods, Contention Sites, Exception Types, the GC detail table and
all three `.gcdump` heap views are the same thing: a ranked table whose rows
expand into an inline stack/retention tree. The pieces:

- **Header**: `renderRankedTableHeader` (`GcDetailTableRenderer.ts`) —
  `renderSortableTableHeader` plus the row-hide button's own bare, unsortable
  leading `<th>`. Every one of those five tables goes through it; four of them
  used to `.replace()` the hide column in by hand, one didn't and broke.
- **Behaviour**: `media/rankedTable.js` — `setupDetailTableSortHandlers` /
  `sortDetailTableByColumn` / `wireSortableTableHeaders` /
  `createRowHideController` / `splitQualifiedName`, all plain globals, loaded
  before `snapshotGcStats.js` (which used to define the first four *inside its
  own IIFE*, which is why the `.gcdump` webview — a separate document — shipped
  with sortable-LOOKING headers that did nothing at all when clicked).
- **Markup**: a `.detailTable.cpuHotMethodsTable` **div** wrapping a bare
  `<table>`; rows `<tr class="typeRow …"><td class="rowHideColumn">✕</td><td>▸
  name</td>` then numeric cells; a paired hidden
  `<tr class="callPathsDetail"><td colspan=N class="callerTreeCell">` for the
  tree; each tree level its own `<table class="callerTreeInner">` with a
  `<colgroup>` whose first `<col>` is a 1.6em spacer and a matching empty
  leading `<td>` on every row.

**The column positions are the contract.** snapshot.css keys off them:
column 1 narrow/centered, column 2 the left-aligned, wrapping, `width: auto`
name column, columns 3+ numeric and right-aligned. The `.gcdump` census table
emitted its own order (type name as column 1, no hide column) and the failure
was not subtle — one 567-character generic type name (`System.Func<...>`,
ordinary on a real heap) blew the unwrapped first column out to thousands of
pixels and pushed every numeric column off-screen, while `Objects` sat in the
column sized to hold long names. Its two tree views were worse: bare `<table>`
elements outside any `.detailTable` wrapper inherited no table styling at all
and rendered centered, which erased the indentation that WAS the tree.

Two things that are NOT shared, deliberately:

- **Sorting a capped, JS-rendered table sorts the DATA, not the DOM.** The
  `.gcdump` tables render the first 500 of thousands of rows, so
  `sortDetailTableByColumn` (which reorders the rows present) would reorder
  that one page and present it as the top 500 by the newly clicked column.
  `wireSortableTableHeaders` exists to share the header/indicator wiring while
  letting the caller decide what "sort" means; `gcDumpView.js` re-sorts its
  array and re-renders.
- **Each view owns its own click delegation.** The tree machinery is small
  (build one level, cache it in the DOM, toggle `expanded` on the row and its
  detail row); duplicating it costs less than a shared registry keyed by four
  different `data-*` attribute pairs, which is the shape
  `cpuDrillDownStats.js` / `exceptionDrillDownStats.js` /
  `contentionDrillDownStats.js` already settled into.

A tree's numeric columns line up with the ranked table's RIGHTMOST columns
because both grids span the same box — an alignment, not a shared meaning. The
`.gcdump` trees therefore label their own columns once, at the top of an
expansion (`.treeColumnLabelRow`), or "Bytes" reads as whatever header happens
to sit above it.

Dimmed text (`.methodTypePrefix`, `.unresolvedFrame`, `.calledByLabel`,
`.pathCount`, `.rowHideBtn`) uses `color: inherit` + `opacity`, not a
hard-coded `rgba(0,0,0,…)`: these webviews follow the VS Code theme, and a
near-black glyph on a dark theme is invisible — the ✕ hide column read as an
empty gutter. On a light theme the inherited foreground IS near-black, so the
rendering is unchanged. (Plenty of other hard-coded colours remain in
snapshot.css; these five were converted because the `.gcdump` views now use
them.)

## Editor tabs: opening a view must not close the user's (issue #99, 1.9.2)

`dotnetInsights.edit` (the `*.dll` custom editor) renders nothing into its own
webview — it runs `ildasm` and shows the generated `.ildasm` text document
instead — so it has to get rid of its own placeholder tab. It used to do that
with `workbench.action.closeActiveEditor`, or, whenever more than one editor
was visible, **`closeEditorsAndGroup`**, which closed the user's entire editor
group. That second branch is the one real users hit, since it triggers whenever
anything other than a single editor is open.

- **Dispose the placeholder panel; never run a `workbench.action.close*`
  command.** `webviewPanel.dispose()` closes exactly one editor — its own.
  This is also why doing the swap in `resolveCustomEditor` rather than
  `openCustomDocument` matters: resolve is handed the panel, and it doesn't run
  at all for a tab restored into the background, so a reloaded window no longer
  closes editors during startup.
- **Never dispose that panel synchronously inside `resolveCustomEditor`.**
  VS Code goes on setting the editor up after that call returns, and disposing
  underneath it fails the whole open with a modal *"Unable to open '<name>.dll':
  OverlayWebview has been disposed"* — while the `.ildasm` document itself
  opens fine behind the dialog, so the symptom looks unrelated to the cause.
  `showIlDasmInPlaceOfPanel` defers by a `setTimeout(…, 0)` and disposes only
  once the replacement document is on screen, several async round trips later.
- **`WebviewPanel.viewColumn` is `undefined` until the panel has been laid
  out**, so read it *after* the deferral, not at call time. A test asserting a
  column on a freshly created panel has to poll for one first.
- **A `ViewColumn` is a position, never a count or an index.** Three separate
  call sites derived one from `visibleTextEditors`, all wrong in different
  ways, all fixed in 1.9.2: `visibleTextEditors.length + 1`
  (`onSaveIlDasm.ts`) walked a fresh editor column further right on every
  generation (measured 1 → 2 → 3 → 4), and a 0-based index into that array
  passed as a 1-based column (`showJitDump`/`showAsm`) worked only by accident
  — index 0 resolves to column One, so a listing in the *first* group looked
  correct while one in the second group produced index 1, column One again, and
  put the counterpart in the wrong group. `visibleTextEditors` is not ordered by
  column either. Take the column from the editor or panel that owns it
  (`editor.viewColumn`), and derive neighbours arithmetically from that.
- **`preview: false` on anything this extension generates.** A preview tab is
  reused, so `showTextDocument(doc, 1)` twice leaves exactly ONE tab — verified
  in the test host. The min opts / tier 1 / jit dump commands each write a
  uniquely named listing and comparing two of them is the whole point, so
  generating the second silently closed the first.

Testing these: `@vscode/test-electron` drives the real API, but a **custom
editor viewType can only be registered if it is declared in `package.json`'s
`contributes.customEditors`**, so there is no way to stand up a synthetic
provider for a test, and driving the real `dotnetInsights.edit` needs the
downloaded `ildasm` binary. Hence `ilDasmEditorSwap.test.ts` /
`listingColumnPlacement.test.ts` / `generatedListingTabs.test.ts` exercise the
extracted helpers plus the disposal *timing* invariant (the panel must still be
alive at the instant `resolveCustomEditor` returns) rather than an end-to-end
open. One trap when writing them: **`onDidDispose` fires immediately but
`vscode.window.tabGroups` catches up a turn later**, so an assertion that a tab
is gone has to poll rather than read the tab list right after the await.

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
