////////////////////////////////////////////////////////////////////////////////
// Module: Program.cs
//
// Notes:
// Two modes:
//   nettraceParser <file.nettrace> --json <output.json>
//     Writes the GC event data as JSON (the shape the VS Code extension's
//     DotnetInsightsGcSnapshotEditor rendering already expects) and exits
//     quietly - this is what the extension invokes.
//   nettraceParser <file.nettrace> [--dump-fields <EventName>]
//     Manual verification harness: dumps the trace header, per-provider/
//     event-name counts, a GC summary, and optionally raw decoded fields
//     for one event name. Real automated coverage lives in the sibling
//     nettraceParser.Tests project (`dotnet test`), not here.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Progress;
using DotnetInsights.NetTrace.Rundown;

if (args.Length < 1)
{
    Console.WriteLine("Usage: nettraceParser <file.nettrace> [--json <output.json>] [--dump-fields <EventName>]");
    return;
}

string filePath = args[0];

Stopwatch totalStopwatch = Stopwatch.StartNew();
Stopwatch phaseStopwatch = Stopwatch.StartNew();

// --json parsing is hoisted here (was previously read only after the read
// phase completed) specifically so progress reporting - see
// Progress/ProgressReporter.cs - can be gated on "are we in --json mode"
// from the very first byte read. Nothing below this point behaves any
// differently for the plain CLI/--dump-fields path than it did before -
// isJsonMode being false means ProgressReporter.Enable() is simply never
// called, and every ProgressReporter method is a no-op until it is (see
// that class's own header comment) - so this whole feature is provably
// invisible to that path, not just "happens not to matter" by omission.
int jsonArgIndex = Array.IndexOf(args, "--json");
bool isJsonMode = jsonArgIndex >= 0 && jsonArgIndex + 1 < args.Length;
string jsonOutputPath = isJsonMode ? args[jsonArgIndex + 1] : null;
// Sits next to jsonOutputPath by a fixed naming convention rather than
// being embedded as a path in the JSON itself - the caller (the VS Code
// extension, see DotnetInsightsNettraceEditor.ts) already knows
// jsonOutputPath and can derive this the same way, so nothing needs to
// round-trip a filesystem path through the JSON payload.
string ticksBinaryPath = isJsonMode ? Path.ChangeExtension(jsonOutputPath, ".ticks.bin") : null;

if (isJsonMode)
{
    ProgressReporter.Enable();
}

ProgressRange readRange = ProgressPlan.PlanRead();
ProgressReporter.BeginPhase("Reading trace file", readRange.Start, readRange.End);
// Pre-touches Console.Error's own lazy init before the no-GC region just
// below - see Warmup's own comment for why this specific ordering matters.
ProgressReporter.Warmup();

// Suppress GC for the read phase only - see ReadPhaseGcSuppression.cs for
// the full measured rationale (that phase allocates ~2.6x the file size and
// retains essentially all of it, so its collections reclaim almost nothing
// while still paying full mark/promote cost). Declines on its own for small
// inputs or when the machine can't back a full-read budget, so this is safe
// to call unconditionally.
long noGcBudgetBytes = ReadPhaseGcSuppression.ComputeBudgetBytes(new FileInfo(filePath).Length, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
bool suppressedGcForRead = ReadPhaseGcSuppression.TryStart(noGcBudgetBytes);

NettraceFile file = NettraceFile.Read(filePath, ProgressReporter.ReportFraction);

if (suppressedGcForRead)
{
    ReadPhaseGcSuppression.End();
}

ProgressReporter.CompletePhase();

long readMs = phaseStopwatch.ElapsedMilliseconds;
phaseStopwatch.Restart();

// Anchoring wall-clock conversion to Header.SyncTimeQPC now agrees with
// file.Events[0]'s own QPC to within ~1ms on every real capture checked
// (previously this looked ~3 days off - see CompressedEventBlobHeader.cs's
// doc comment: that was a symptom of a per-event timestamp decode bug that
// inflated every event's QPC by ~2x, not an unreliable SyncTimeQPC field).
long referenceQpc = file.Header.SyncTimeQPC;

if (isJsonMode)
{
    // Computed now (not before Read) since it needs file.Events.Count,
    // known only once the read phase actually finishes - see
    // Progress/ProgressPlan.cs's own "stage 1" comment.
    ProgressRange[] projectorRanges = ProgressPlan.PlanProjectorPhases();

    ProgressReporter.BeginPhase("Projecting GC events", projectorRanges[0].Start, projectorRanges[0].End);
    List<GcEvent> gcEventsForJson = GcEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc, ProgressReporter.ReportFraction);
    ProgressReporter.CompletePhase();
    long gcProjectMs = phaseStopwatch.ElapsedMilliseconds;
    phaseStopwatch.Restart();

    ProgressReporter.BeginPhase("Projecting allocation events", projectorRanges[1].Start, projectorRanges[1].End);
    List<AllocationEvent> allocationEventsForJson = AllocationEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc, ProgressReporter.ReportFraction);
    ProgressReporter.CompletePhase();
    long allocationProjectMs = phaseStopwatch.ElapsedMilliseconds;
    phaseStopwatch.Restart();

    ProgressReporter.BeginPhase("Projecting exception events", projectorRanges[2].Start, projectorRanges[2].End);
    List<ExceptionEvent> exceptionEventsForJson = ExceptionEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc, ProgressReporter.ReportFraction);
    ProgressReporter.CompletePhase();
    long exceptionProjectMs = phaseStopwatch.ElapsedMilliseconds;
    phaseStopwatch.Restart();

    ProgressReporter.BeginPhase("Building event overview", projectorRanges[3].Start, projectorRanges[3].End);
    EventOverview eventOverviewForJson = EventOverviewBuilder.Build(file.Events, ProgressReporter.ReportFraction);
    ProgressReporter.CompletePhase();
    long eventOverviewMs = phaseStopwatch.ElapsedMilliseconds;
    phaseStopwatch.Restart();

    ProgressReporter.BeginPhase("Building method symbol table", projectorRanges[4].Start, projectorRanges[4].End);
    MethodSymbolTable symbolTable = MethodSymbolTable.Build(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, referenceQpc, ProgressReporter.ReportFraction);
    ProgressReporter.CompletePhase();
    long symbolTableMs = phaseStopwatch.ElapsedMilliseconds;
    phaseStopwatch.Restart();

    ProgressReporter.BeginPhase("Projecting CPU samples", projectorRanges[5].Start, projectorRanges[5].End);
    List<SampleEvent> sampleEventsForJson = SampleProfileEventProjector.Project(file.Events, file.Header.QPCFrequency, referenceQpc, ProgressReporter.ReportFraction);
    ProgressReporter.CompletePhase();
    long sampleProjectMs = phaseStopwatch.ElapsedMilliseconds;
    phaseStopwatch.Restart();

    ProgressReporter.BeginPhase("Projecting contention events", projectorRanges[6].Start, projectorRanges[6].End);
    List<ContentionEvent> contentionEventsForJson = ContentionEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc, ProgressReporter.ReportFraction);
    ProgressReporter.CompletePhase();
    long contentionProjectMs = phaseStopwatch.ElapsedMilliseconds;
    phaseStopwatch.Restart();

    int totalEventCount = file.Events.Count;

    // Whole-capture wall-clock span, on the same referenceQpc/RelativeMSec
    // axis every projector above already uses - the one thing only the raw
    // event list (not any projector's own narrower min/max) can answer,
    // since a capture can easily have long GC/contention/CPU-sample-free
    // stretches at the start or end that would otherwise understate it.
    // Computed here, not inside a new projector of its own, since this is
    // the last point file.Events is available (see the `file = null;` below)
    // and a single min/max scan is cheap next to the six passes already
    // done above.
    double captureDurationMSec = 0;

    if (totalEventCount > 0)
    {
        long minTimeStampQpc = long.MaxValue;
        long maxTimeStampQpc = long.MinValue;

        Span<EventRecord> eventsSpanForDuration = CollectionsMarshal.AsSpan(file.Events);
        for (int eventIndex = 0; eventIndex < eventsSpanForDuration.Length; ++eventIndex)
        {
            long timeStampQpc = eventsSpanForDuration[eventIndex].TimeStampRelativeQPC;

            if (timeStampQpc < minTimeStampQpc)
            {
                minTimeStampQpc = timeStampQpc;
            }

            if (timeStampQpc > maxTimeStampQpc)
            {
                maxTimeStampQpc = timeStampQpc;
            }
        }

        captureDurationMSec = (maxTimeStampQpc - minTimeStampQpc) * 1000.0 / file.Header.QPCFrequency;
    }

    // Nothing past this point ever reads file/file.Events again -
    // GcEventProjector.Project, AllocationEventProjector.Project,
    // ExceptionEventProjector.Project, EventOverviewBuilder.Build,
    // MethodSymbolTable.Build, SampleProfileEventProjector.Project, and
    // ContentionEventProjector.Project above all just iterate it and hand
    // back brand-new derived structures; none of them stash a reference to
    // the list itself. But `file` is still a GC root for the rest of this
    // method's stack frame regardless, so without dropping it here, every
    // gen2 GC during the jsonExport call below still has to trace
    // file.Events's full backing array - a real 5-minute capture holds
    // 4.29M+ EventRecord structs, each carrying 5 reference-typed fields
    // (ProviderName/EventName/Stack/Fields/PayloadBuffer) - tens of millions
    // of pointer slots per full mark pass. Confirmed via dotnet-trace
    // gc-verbose as the actual dominant per-gen2-pause cost - the raw
    // byte[] file buffer it's decoded from has zero embedded object
    // references and costs the mark phase nothing to trace no matter how
    // large it is, despite looking like the obvious culprit by raw size.
    file = null;

    string processName = Path.GetFileNameWithoutExtension(filePath);

    // jsonExport's own progress reporting (5 sub-writer phases) is driven
    // entirely from inside GcJsonExporter.WriteToFile itself - see that
    // method's own comment for why it calls ProgressReporter directly
    // rather than taking an onProgress parameter like every phase above.
    JsonExportTiming jsonExportTiming = GcJsonExporter.WriteToFile(jsonOutputPath, gcEventsForJson, allocationEventsForJson, exceptionEventsForJson, eventOverviewForJson, sampleEventsForJson, contentionEventsForJson, symbolTable, processName, ticksBinaryPath, captureDurationMSec);
    long jsonExportMs = phaseStopwatch.ElapsedMilliseconds;

    long totalMs = totalStopwatch.ElapsedMilliseconds;

    // GC.GetTotalPauseDuration() is this PROCESS's own real, cumulative
    // stop-the-world pause time (a real .NET API, not sampled/estimated) -
    // reported alongside the phase breakdown specifically so "why did this
    // run take longer" can be answered directly (was more of it spent
    // paused in GC?) without needing a separate dotnet-trace attach, which
    // has its own confound: attach latency (the trace only starts once the
    // diagnostic pipe handshake completes, arbitrarily late relative to
    // process start) can silently miss whichever GCs happen earliest in a
    // given run, making cross-run GC-time comparisons via two independent
    // traces unreliable in a way this in-process counter isn't.
    //
    // jsonExport's own (alloc=..,exc=..,cpu=..,cont=..,gc=..) breakdown is
    // permanent, not throwaway, instrumentation - added specifically so
    // Progress/ProgressPlan.cs's own jsonExport sub-writer weight constants
    // can be recalibrated against a real capture with a single CLI run
    // (read this line) rather than needing scaffolding re-added each time -
    // see that file's own header comment.
    Console.Error.WriteLine(
        $"Timing: read={readMs}ms ({totalEventCount} events) " +
        $"gcProject={gcProjectMs}ms ({gcEventsForJson.Count} GCs) " +
        $"allocationProject={allocationProjectMs}ms ({allocationEventsForJson.Count} ticks) " +
        $"exceptionProject={exceptionProjectMs}ms ({exceptionEventsForJson.Count} exceptions) " +
        $"eventOverview={eventOverviewMs}ms ({eventOverviewForJson.EventTypes.Count} distinct event types) " +
        $"symbolTable={symbolTableMs}ms " +
        $"sampleProject={sampleProjectMs}ms ({sampleEventsForJson.Count} samples) " +
        $"contentionProject={contentionProjectMs}ms ({contentionEventsForJson.Count} contentions) " +
        $"jsonExport={jsonExportMs}ms(alloc={jsonExportTiming.AllocationMs}ms,exc={jsonExportTiming.ExceptionMs}ms,cpu={jsonExportTiming.CpuMs}ms,cont={jsonExportTiming.ContentionMs}ms,gc={jsonExportTiming.GcMs}ms) " +
        $"total={totalMs}ms " +
        $"gcPause={GC.GetTotalPauseDuration().TotalMilliseconds:F1}ms gcCounts=[{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}]");

    return;
}

Console.WriteLine("== Header ==");
Console.WriteLine($"SyncTime: {file.Header.Year}-{file.Header.Month:D2}-{file.Header.Day:D2} {file.Header.Hour:D2}:{file.Header.Minute:D2}:{file.Header.Second:D2}.{file.Header.Millisecond:D3}");
Console.WriteLine($"QPCFrequency: {file.Header.QPCFrequency}");
if (Environment.GetEnvironmentVariable("NETTRACE_DEBUG") != null)
{
    Console.WriteLine($"SyncTimeQPC (referenceQpc, used for GC timestamps): {file.Header.SyncTimeQPC}");
}
Console.WriteLine($"PointerSize: {file.Header.PointerSize}");
Console.WriteLine($"ProcessId: {file.Header.ProcessId}");
Console.WriteLine($"NumberOfProcessors: {file.Header.NumberOfProcessors}");
Console.WriteLine($"MetadataBlockCount: {file.MetadataBlockCount}");
Console.WriteLine($"EventBlockCount: {file.EventBlockCount}");
Console.WriteLine($"SkippedBlockCount: {file.SkippedBlockCount}");

Console.WriteLine();
Console.WriteLine("== Metadata ==");
Console.WriteLine($"Distinct event schemas: {file.MetadataById.Count}");

Dictionary<string, int> providerCounts = new Dictionary<string, int>();
foreach (KeyValuePair<int, EventMetadata> entry in file.MetadataById)
{
    providerCounts.TryGetValue(entry.Value.ProviderName, out int currentCount);
    providerCounts[entry.Value.ProviderName] = currentCount + 1;
}

foreach (KeyValuePair<string, int> providerCount in providerCounts)
{
    Console.WriteLine($"  {providerCount.Key}: {providerCount.Value} event schema(s)");
}

Console.WriteLine();
Console.WriteLine("== Events ==");
Console.WriteLine($"Total decoded events: {file.Events.Count}");

Dictionary<string, int> eventNameCounts = new Dictionary<string, int>();
foreach (EventRecord record in file.Events)
{
    string key = $"{record.ProviderName}/{record.EventName}";
    eventNameCounts.TryGetValue(key, out int currentCount);
    eventNameCounts[key] = currentCount + 1;
}

foreach (KeyValuePair<string, int> eventNameCount in eventNameCounts)
{
    Console.WriteLine($"  {eventNameCount.Key}: {eventNameCount.Value}");
}

if (args.Length > 2 && args[1] == "--dump-fields")
{
    string eventNameFilter = args[2];

    Console.WriteLine();
    Console.WriteLine($"== Field dump for '{eventNameFilter}' (first match) ==");

    foreach (EventRecord record in file.Events)
    {
        if (record.EventName != eventNameFilter)
        {
            continue;
        }

        DumpFields(record.Fields, 1);
        break;
    }
}

if (Environment.GetEnvironmentVariable("NETTRACE_DEBUG") != null)
{
    Console.WriteLine();
    Console.WriteLine("== CLR provider EventId histogram (debug) ==");

    Dictionary<int, int> eventIdCounts = new Dictionary<int, int>();
    Dictionary<int, int> eventIdVersion = new Dictionary<int, int>();
    Dictionary<int, int> eventIdPayloadLen = new Dictionary<int, int>();
    foreach (EventRecord record in file.Events)
    {
        if (record.ProviderName != "Microsoft-Windows-DotNETRuntime")
        {
            continue;
        }

        eventIdCounts.TryGetValue(record.EventId, out int c);
        eventIdCounts[record.EventId] = c + 1;
        eventIdVersion[record.EventId] = record.Version;
        eventIdPayloadLen[record.EventId] = record.PayloadLength;
    }

    foreach (KeyValuePair<int, int> kv in eventIdCounts)
    {
        Console.WriteLine($"  EventId={kv.Key} count={kv.Value} version={eventIdVersion[kv.Key]} payloadLen={eventIdPayloadLen[kv.Key]}");
    }

    Console.WriteLine();
    Console.WriteLine("== Microsoft-DotNETCore-SampleProfiler EventId histogram (debug) ==");

    Dictionary<int, int> sampleEventIdCounts = new Dictionary<int, int>();
    Dictionary<int, int> sampleEventIdVersion = new Dictionary<int, int>();
    Dictionary<int, int> sampleEventIdPayloadLen = new Dictionary<int, int>();
    Dictionary<int, int> sampleEventIdStackLen = new Dictionary<int, int>();
    foreach (EventRecord record in file.Events)
    {
        if (record.ProviderName != "Microsoft-DotNETCore-SampleProfiler")
        {
            continue;
        }

        sampleEventIdCounts.TryGetValue(record.EventId, out int sampleCount);
        sampleEventIdCounts[record.EventId] = sampleCount + 1;
        sampleEventIdVersion[record.EventId] = record.Version;
        sampleEventIdPayloadLen[record.EventId] = record.PayloadLength;
        sampleEventIdStackLen[record.EventId] = record.Stack.Length;
    }

    foreach (KeyValuePair<int, int> kv in sampleEventIdCounts)
    {
        Console.WriteLine($"  EventId={kv.Key} count={kv.Value} version={sampleEventIdVersion[kv.Key]} payloadLen={sampleEventIdPayloadLen[kv.Key]} stackLen(lastSeen)={sampleEventIdStackLen[kv.Key]}");
    }
}

Console.WriteLine();
Console.WriteLine("== GC summary ==");

List<GcEvent> gcEvents = GcEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc);
Console.WriteLine($"Completed GCs: {gcEvents.Count}");

foreach (GcEvent gcEvent in gcEvents)
{
    Console.WriteLine($"  GC #{gcEvent.Id} [{gcEvent.Timestamp:O}] gen{gcEvent.Generation} {gcEvent.Reason} pause={gcEvent.PauseDurationMSec:F2}ms numHeaps={gcEvent.NumHeaps} totalHeapSize={gcEvent.TotalHeapSize} totalPromoted={gcEvent.TotalPromoted} heapsDecoded={gcEvent.Heaps.Count}");

    if (Environment.GetEnvironmentVariable("NETTRACE_DEBUG") != null && gcEvent.Heaps.Count > 0)
    {
        ClrGcHeap heap = gcEvent.Heaps[0];
        for (int genIndex = 0; genIndex < heap.Generations.Length; ++genIndex)
        {
            ref readonly ClrGcGeneration gen = ref heap.Generations[genIndex];
            Console.WriteLine($"    heap0 gen{genIndex}: SizeBefore={gen.SizeBefore} SizeAfter={gen.SizeAfter} NewAllocation={gen.NewAllocation} SurvRate={gen.SurvRate} In={gen.In} Out={gen.Out}");
        }
    }
}

void DumpFields(Dictionary<string, object> fields, int indent)
{
    string prefix = new string(' ', indent * 2);

    foreach (KeyValuePair<string, object> field in fields)
    {
        if (field.Value is Dictionary<string, object> nested)
        {
            Console.WriteLine($"{prefix}{field.Key} (Object):");
            DumpFields(nested, indent + 1);
        }
        else
        {
            Console.WriteLine($"{prefix}{field.Key} ({field.Value?.GetType().Name}) = {field.Value}");
        }
    }
}
