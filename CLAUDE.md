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

Packaging: `nettraceParser/pack.py` (Python — a bash version was explicitly
rejected in favor of this). Publishes self-contained, non-single-file builds
for `osx-x64`/`linux-x64`/`win-x64` and archives each as
`nettraceParser-{osName}-x64.tar.gz` with a single top-level `nettraceParser/`
folder, matching `roslynHelper`'s real release-asset layout exactly (verified
by inspecting a real `roslynHelper-osx-x64.tar.gz`). Upload with
`gh release upload <tag> nettraceParser/artifacts/*.tar.gz --clobber`.

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
