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
- **GC event correlation**: `GCHeapStats` / `GCGlobalHeapHistory` /
  `GCPerHeapHistory` can arrive on the wire *after* `GCEnd` for the GC they
  describe. Correlate via a `mostRecentlyStartedGcId` counter, not by
  tracking "currently open" GCs.
- **QPC timestamp domain**: `NettraceHeader.SyncTimeQPC`'s numeric
  relationship to the per-event QPC stream does not reliably hold on this
  platform (produced timestamps ~3 days off in one verified case). Anchor
  wall-clock conversion to the trace's own **first event's QPC value**
  instead of the header's `SyncTimeQPC`.
- **`GCPerHeapHistory`**: only `Version >= 3` payloads are decoded (what this
  environment's .NET actually emits); older layouts are unimplemented on
  purpose.
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

## Known open items

- `DotnetInsightsNettraceEditor.ts` still registers its webview with
  `languageId: "ildasm"` (copy-pasted from the PMI/disassembly editor this
  was modeled on) — flagged as misleading but the rename was never actioned.
- Tool version constants in `extension.ts` are not bumped on every binary
  re-upload (see "stale-cache trap" above) — several `nettraceParser` fixes
  shipped this way during initial development.
