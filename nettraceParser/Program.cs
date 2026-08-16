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
using System.Threading;
using System.Threading.Tasks;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Binary;
using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Diff;
using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.GcDump;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Progress;
using DotnetInsights.NetTrace.Rundown;
using DotnetInsights.NetTrace.Threading;

if (args.Length < 1)
{
    Console.WriteLine("Usage: nettraceParser <file.nettrace> [--json <output.json>] [--dump-fields <EventName>]");
    Console.WriteLine("       nettraceParser --diff <baseline.nettrace> <comparison.nettrace> --json <output.json>");
    return;
}

// --diff runs the whole single-capture pipeline twice and emits one compact
// comparison payload (see Diff/CaptureDiffJsonExporter.cs for why the diff is
// computed here rather than in the webview). Handled before anything else so
// the single-capture path below is left exactly as it was.
int diffArgIndex = Array.IndexOf(args, "--diff");

if (diffArgIndex >= 0)
{
    if (diffArgIndex + 2 >= args.Length)
    {
        Console.Error.WriteLine("--diff requires two capture paths: --diff <baseline.nettrace> <comparison.nettrace> --json <output.json>");
        return;
    }

    string baselinePath = args[diffArgIndex + 1];
    string comparisonPath = args[diffArgIndex + 2];

    int diffJsonArgIndex = Array.IndexOf(args, "--json");

    if (diffJsonArgIndex < 0 || diffJsonArgIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--diff requires --json <output.json>");
        return;
    }

    string diffOutputPath = args[diffJsonArgIndex + 1];

    ProgressReporter.Enable();
    ProgressReporter.Warmup();

    Stopwatch diffStopwatch = Stopwatch.StartNew();

    // Each capture owns half the progress bar. The two are read strictly in
    // sequence, never concurrently: a single capture already peaks around
    // 1.8GB RSS, so holding both event graphs at once would roughly double
    // that for no benefit. BuildProfileForDiff drops its NettraceFile before
    // returning, so only one capture's events are ever live.
    CaptureProfile baselineProfile = BuildProfileForDiff(baselinePath, 0.0, 50.0);
    CaptureProfile comparisonProfile = BuildProfileForDiff(comparisonPath, 50.0, 100.0);

    CaptureDiff captureDiff = CaptureDiffBuilder.Build(baselineProfile, comparisonProfile);
    CaptureDiffJsonExporter.WriteToFile(diffOutputPath, captureDiff);

    Console.Error.WriteLine(
        $"Timing: diff={diffStopwatch.ElapsedMilliseconds}ms " +
        $"baseline={baselineProfile.TotalEventCount} events/{baselineProfile.CaptureDurationMSec:F0}ms " +
        $"comparison={comparisonProfile.TotalEventCount} events/{comparisonProfile.CaptureDurationMSec:F0}ms " +
        $"rows=[events={captureDiff.EventTypes.Count},alloc={captureDiff.AllocationTypes.Count},exc={captureDiff.ExceptionTypes.Count},cpu={captureDiff.CpuMethods.Count},cont={captureDiff.ContentionSites.Count},locks={captureDiff.Locks.Count}] " +
        $"gcPause={GC.GetTotalPauseDuration().TotalMilliseconds:F1}ms gcCounts=[{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}]");

    return;
}

// --gcdump-from-trace builds a .gcdump out of the heap-dump EVENTS in a
// .nettrace, so a heap snapshot can be captured with dotnet-trace alone and
// dotnet-gcdump never enters the pipeline (it silently truncates at 10M nodes -
// see GcDump/HeapDumpEventDecoder.cs). Checked before --gcdump because the
// two flags would otherwise both match a command line carrying each.
int gcDumpFromTraceArgIndex = Array.IndexOf(args, "--gcdump-from-trace");

if (gcDumpFromTraceArgIndex >= 0)
{
    if (gcDumpFromTraceArgIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--gcdump-from-trace requires a trace path: --gcdump-from-trace <trace.nettrace> [-o <out.gcdump>]");
        Environment.ExitCode = 1;
        return;
    }

    Environment.ExitCode = GcDumpConvertCommand.Run(args, args[gcDumpFromTraceArgIndex + 1]);
    return;
}

// --gcdump reads a heap SNAPSHOT (`dotnet-gcdump collect` output) rather than
// an event stream. It shares this tool only because a .gcdump is a
// FastSerialization stream, which this project already vendors a deserializer
// for - see GcDump/GcDumpFormat.cs. Dispatched here, ahead of the
// single-capture path, so that path is left exactly as it was.
int gcDumpArgIndex = Array.IndexOf(args, "--gcdump");

if (gcDumpArgIndex >= 0)
{
    if (gcDumpArgIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--gcdump requires a file path: --gcdump <file.gcdump> [--json <output.json>]");
        Environment.ExitCode = 1;
        return;
    }

    // Exit code rather than a returned value: every other branch of these
    // top-level statements uses a bare `return;`, and introducing a
    // value-returning one here would change the inferred entry point's
    // return type and break all of them.
    Environment.ExitCode = GcDumpCommand.Run(args, args[gcDumpArgIndex + 1]);
    return;
}

// Drives the combined "Projecting events" phase's progress bar while the eight
// projector tasks run concurrently. Progress reporting stays on THIS thread on
// purpose: ProgressReporter is static, single-threaded state that writes to
// Console.Error (see Progress/ProgressReporter.cs), so the projectors publish
// their own completion fraction into a slot each and this loop reads them.
static void ReportProjectorProgress(Task[] projectorTasks, double[] projectorFractions)
{
    // Long enough that polling costs nothing against a phase measured in
    // hundreds of milliseconds, short enough that the bar still moves
    // smoothly - ProgressReporter's own ~100ms emit throttle is the real
    // limit on how often anything reaches the extension anyway.
    const int PollIntervalMs = 25;

    while (true)
    {
        bool allCompleted = Task.WaitAll(projectorTasks, PollIntervalMs);

        double totalFraction = 0.0;
        for (int slotIndex = 0; slotIndex < projectorFractions.Length; ++slotIndex)
        {
            totalFraction += Volatile.Read(ref projectorFractions[slotIndex]);
        }

        ProgressReporter.ReportFraction(totalFraction / projectorFractions.Length);

        if (allCompleted)
        {
            return;
        }
    }
}

// Runs one capture through the same projectors the --json path uses and
// reduces it to the small named-metric profile the diff needs, reporting
// progress inside [progressStart, progressEnd) so two captures fill one bar.
static CaptureProfile BuildProfileForDiff(string captureFilePath, double progressStart, double progressEnd)
{
    double progressSpan = progressEnd - progressStart;

    // Sub-ranges within this capture's own half, weighted the same way the
    // single-capture ProgressPlan weights them: the read dominates, the
    // projectors take most of the rest, the reduction itself is negligible.
    double readEnd = progressStart + (progressSpan * 0.55);
    double projectEnd = progressStart + (progressSpan * 0.97);

    ProgressReporter.BeginPhase($"Reading {Path.GetFileName(captureFilePath)}", progressStart, readEnd);

    long noGcBudget = ReadPhaseGcSuppression.ComputeBudgetBytes(new FileInfo(captureFilePath).Length, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    bool suppressedGc = ReadPhaseGcSuppression.TryStart(noGcBudget);

    NettraceFile captureFile = NettraceFile.Read(captureFilePath, ProgressReporter.ReportFraction);

    if (suppressedGc)
    {
        ReadPhaseGcSuppression.End();
    }

    ProgressReporter.CompletePhase();

    long captureReferenceQpc = captureFile.Header.SyncTimeQPC;
    int capturePointerSize = captureFile.Header.PointerSize;
    long captureQpcFrequency = captureFile.Header.QPCFrequency;
    DateTime captureSyncTimeUtc = captureFile.Header.SyncTimeUtc;

    ProgressReporter.BeginPhase($"Analyzing {Path.GetFileName(captureFilePath)}", readEnd, projectEnd);

    List<GcEvent> diffGcEvents = GcEventProjector.Project(captureFile.Events, capturePointerSize, captureQpcFrequency, captureSyncTimeUtc, captureReferenceQpc);
    List<AllocationEvent> diffAllocationEvents = AllocationEventProjector.Project(captureFile.Events, capturePointerSize, captureQpcFrequency, captureSyncTimeUtc, captureReferenceQpc);
    List<ExceptionEvent> diffExceptionEvents = ExceptionEventProjector.Project(captureFile.Events, capturePointerSize, captureQpcFrequency, captureSyncTimeUtc, captureReferenceQpc);
    EventOverview diffEventOverview = EventOverviewBuilder.Build(captureFile.Events);
    MethodSymbolTable diffSymbolTable = MethodSymbolTable.Build(captureFile.Events, capturePointerSize, captureQpcFrequency, captureReferenceQpc);
    List<SampleEvent> diffSampleEvents = SampleProfileEventProjector.Project(captureFile.Events, captureQpcFrequency, captureReferenceQpc);
    List<ContentionEvent> diffContentionEvents = ContentionEventProjector.Project(captureFile.Events, capturePointerSize, captureQpcFrequency, captureSyncTimeUtc, captureReferenceQpc);

    int diffTotalEventCount = captureFile.Events.Count;
    double diffCaptureDurationMSec = ComputeCaptureDurationMSec(captureFile.Events, captureQpcFrequency);

    ProgressReporter.CompletePhase();

    // Dropped before the profile is built, and crucially before the NEXT
    // capture is opened - same reasoning as the --json path's own
    // `file = null`, but load-bearing here rather than an optimization,
    // since otherwise both captures' event graphs would be live at once.
    //
    // The stack table is captured first: CaptureProfile.Build resolves every
    // sample's and contention's stack through it (see StackTable.cs), so it
    // has to survive the file being dropped. It holds only the decoded
    // stacks, not the event list, so this doesn't weaken the point above.
    StackTable diffStackTable = captureFile.Stacks;
    captureFile = null;

    ProgressReporter.BeginPhase($"Summarizing {Path.GetFileName(captureFilePath)}", projectEnd, progressEnd);

    CaptureProfile profile = CaptureProfile.Build(
        captureFilePath,
        Path.GetFileNameWithoutExtension(captureFilePath),
        diffCaptureDurationMSec,
        diffTotalEventCount,
        diffEventOverview,
        diffGcEvents,
        diffAllocationEvents,
        diffExceptionEvents,
        diffSampleEvents,
        diffContentionEvents,
        diffStackTable,
        diffSymbolTable);

    ProgressReporter.CompletePhase();

    return profile;
}

// Whole-capture wall-clock span on the same axis the projectors use - the
// same min/max scan the --json path performs inline.
static double ComputeCaptureDurationMSec(List<EventRecord> events, long qpcFrequency)
{
    if (events.Count == 0 || qpcFrequency <= 0)
    {
        return 0;
    }

    long minTimeStampQpc = long.MaxValue;
    long maxTimeStampQpc = long.MinValue;

    Span<EventRecord> eventsSpan = CollectionsMarshal.AsSpan(events);
    for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
    {
        long timeStampQpc = eventsSpan[eventIndex].TimeStampRelativeQPC;

        if (timeStampQpc < minTimeStampQpc)
        {
            minTimeStampQpc = timeStampQpc;
        }

        if (timeStampQpc > maxTimeStampQpc)
        {
            maxTimeStampQpc = timeStampQpc;
        }
    }

    return (maxTimeStampQpc - minTimeStampQpc) * 1000.0 / qpcFrequency;
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

// --binary <output.bin> writes the container documented in
// Binary/BinaryCaptureFormat.cs. Independent of --json on purpose: the
// migration converts one section at a time, so a run needs to be able to
// emit BOTH and have them diffed against each other (see
// BinaryCaptureDiffTests). It requires --json today only because every
// section's data is produced by the JSON export pass.
int binaryArgIndex = Array.IndexOf(args, "--binary");
string binaryOutputPath = binaryArgIndex >= 0 && binaryArgIndex + 1 < args.Length ? args[binaryArgIndex + 1] : null;

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
    ProgressRange projectorRange = ProgressPlan.PlanProjectorsCombined();

    // One slot per projector, written by that projector's own thread and read
    // by this one - see ReportProjectorProgress. The per-phase weights
    // ProgressPlan.PlanProjectorPhases used to apportion are no longer
    // meaningful now that the phases overlap in time, so the bar advances on
    // the mean of the eight completion fractions instead.
    double[] projectorFractions = new double[8];

    // Every projector below is an INDEPENDENT read-only pass over the same
    // file.Events list, so they run concurrently rather than one after
    // another. Measured on a real 3.23GB/35.08M-event capture: the eight
    // passes cost ~1230ms end to end when run in sequence on a machine with 8
    // cores and ~0% idle samples anywhere in the run - i.e. seven cores sat
    // idle for that whole stretch.
    //
    // Explicit Tasks rather than Parallel.ForEach (see CLAUDE.md), and
    // deliberately no onProgress callbacks: ProgressReporter is static,
    // single-threaded state that writes to Console.Error, so the phase is
    // reported by THIS thread from the count of completed projectors instead
    // (see ReportProjectorProgress below). Each task times itself so the
    // Timing: line still breaks the work down per projector - those numbers
    // are now concurrent durations, which is why they no longer sum to the
    // phase's own wall time.
    ProgressReporter.BeginPhase("Projecting events", projectorRange.Start, projectorRange.End);

    List<EventRecord> eventsForProjection = file.Events;
    int projectionPointerSize = file.Header.PointerSize;
    long projectionQpcFrequency = file.Header.QPCFrequency;
    DateTime projectionSyncTimeUtc = file.Header.SyncTimeUtc;

    long gcProjectMs = 0;
    long allocationProjectMs = 0;
    long exceptionProjectMs = 0;
    long eventOverviewMs = 0;
    long symbolTableMs = 0;
    long sampleProjectMs = 0;
    long contentionProjectMs = 0;
    long threadingProjectMs = 0;

    Task<List<GcEvent>> gcProjectTask = Task.Run(() =>
    {
        Stopwatch taskStopwatch = Stopwatch.StartNew();
        List<GcEvent> projected = GcEventProjector.Project(eventsForProjection, projectionPointerSize, projectionQpcFrequency, projectionSyncTimeUtc, referenceQpc, fraction => Volatile.Write(ref projectorFractions[0], fraction));
        gcProjectMs = taskStopwatch.ElapsedMilliseconds;
        return projected;
    });

    Task<List<AllocationEvent>> allocationProjectTask = Task.Run(() =>
    {
        Stopwatch taskStopwatch = Stopwatch.StartNew();
        List<AllocationEvent> projected = AllocationEventProjector.Project(eventsForProjection, projectionPointerSize, projectionQpcFrequency, projectionSyncTimeUtc, referenceQpc, fraction => Volatile.Write(ref projectorFractions[1], fraction));
        allocationProjectMs = taskStopwatch.ElapsedMilliseconds;
        return projected;
    });

    Task<List<ExceptionEvent>> exceptionProjectTask = Task.Run(() =>
    {
        Stopwatch taskStopwatch = Stopwatch.StartNew();
        List<ExceptionEvent> projected = ExceptionEventProjector.Project(eventsForProjection, projectionPointerSize, projectionQpcFrequency, projectionSyncTimeUtc, referenceQpc, fraction => Volatile.Write(ref projectorFractions[2], fraction));
        exceptionProjectMs = taskStopwatch.ElapsedMilliseconds;
        return projected;
    });

    Task<EventOverview> eventOverviewTask = Task.Run(() =>
    {
        Stopwatch taskStopwatch = Stopwatch.StartNew();
        EventOverview built = EventOverviewBuilder.Build(eventsForProjection, fraction => Volatile.Write(ref projectorFractions[3], fraction));
        eventOverviewMs = taskStopwatch.ElapsedMilliseconds;
        return built;
    });

    Task<MethodSymbolTable> symbolTableTask = Task.Run(() =>
    {
        Stopwatch taskStopwatch = Stopwatch.StartNew();
        MethodSymbolTable built = MethodSymbolTable.Build(eventsForProjection, projectionPointerSize, projectionQpcFrequency, referenceQpc, fraction => Volatile.Write(ref projectorFractions[4], fraction));
        symbolTableMs = taskStopwatch.ElapsedMilliseconds;
        return built;
    });

    // The one dependency in this set: the overview's exact per-event-type
    // counts let this presize its result list (16.24M samples on the capture
    // above, where growing from empty was a third of this projector's cost -
    // see EventOverview.CountForEvent). Chained rather than run standalone so
    // the two still overlap with the other six.
    Task<List<SampleEvent>> sampleProjectTask = eventOverviewTask.ContinueWith(completedOverview =>
    {
        Stopwatch taskStopwatch = Stopwatch.StartNew();
        int expectedSampleCount = completedOverview.Result.CountForEvent(SampleProfileEventProjector.ProviderName, SampleProfileEventProjector.EventId);
        List<SampleEvent> projected = SampleProfileEventProjector.Project(eventsForProjection, projectionQpcFrequency, referenceQpc, fraction => Volatile.Write(ref projectorFractions[5], fraction), expectedSampleCount);
        sampleProjectMs = taskStopwatch.ElapsedMilliseconds;
        return projected;
    }, TaskContinuationOptions.ExecuteSynchronously);

    Task<List<ContentionEvent>> contentionProjectTask = Task.Run(() =>
    {
        Stopwatch taskStopwatch = Stopwatch.StartNew();
        List<ContentionEvent> projected = ContentionEventProjector.Project(eventsForProjection, projectionPointerSize, projectionQpcFrequency, projectionSyncTimeUtc, referenceQpc, fraction => Volatile.Write(ref projectorFractions[6], fraction));
        contentionProjectMs = taskStopwatch.ElapsedMilliseconds;
        return projected;
    });

    Task<ThreadingSummary> threadingProjectTask = Task.Run(() =>
    {
        Stopwatch taskStopwatch = Stopwatch.StartNew();
        ThreadingSummary projected = ThreadingEventProjector.Project(eventsForProjection, projectionPointerSize, projectionQpcFrequency, referenceQpc, fraction => Volatile.Write(ref projectorFractions[7], fraction));
        threadingProjectMs = taskStopwatch.ElapsedMilliseconds;
        return projected;
    });

    Task[] projectorTasks = new Task[]
    {
        gcProjectTask,
        allocationProjectTask,
        exceptionProjectTask,
        eventOverviewTask,
        symbolTableTask,
        sampleProjectTask,
        contentionProjectTask,
        threadingProjectTask
    };

    ReportProjectorProgress(projectorTasks, projectorFractions);

    List<GcEvent> gcEventsForJson = gcProjectTask.Result;
    List<AllocationEvent> allocationEventsForJson = allocationProjectTask.Result;
    List<ExceptionEvent> exceptionEventsForJson = exceptionProjectTask.Result;
    EventOverview eventOverviewForJson = eventOverviewTask.Result;
    MethodSymbolTable symbolTable = symbolTableTask.Result;
    List<SampleEvent> sampleEventsForJson = sampleProjectTask.Result;
    List<ContentionEvent> contentionEventsForJson = contentionProjectTask.Result;
    ThreadingSummary threadingSummaryForJson = threadingProjectTask.Result;

    ProgressReporter.CompletePhase();
    long projectorsMs = phaseStopwatch.ElapsedMilliseconds;
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

    // Captured before `file` is dropped below: the export phase resolves
    // every event's stack through this table (see StackTable.cs), so it has
    // to outlive the NettraceFile that owns it. This retains no more than
    // before - the decoded stacks were previously reachable from every
    // EventRecord/SampleEvent's own long[] field anyway.
    StackTable stackTable = file.Stacks;

    // Nothing past this point ever reads file/file.Events again -
    // GcEventProjector.Project, AllocationEventProjector.Project,
    // ExceptionEventProjector.Project, EventOverviewBuilder.Build,
    // MethodSymbolTable.Build, SampleProfileEventProjector.Project, and
    // ContentionEventProjector.Project above all just iterate it and hand
    // back brand-new derived structures; none of them stash a reference to
    // the list itself. But `file` is still a GC root for the rest of this
    // method's stack frame regardless, so without dropping it here, every
    // gen2 GC during the export call below still has to trace
    // file.Events's full backing array - a real 5-minute capture holds
    // 4.29M+ EventRecord structs, each carrying 5 reference-typed fields
    // (ProviderName/EventName/Fields/PayloadBuffer - one fewer since stacks
    // became an index, see StackTable.cs) - tens of millions
    // of pointer slots per full mark pass. Confirmed via dotnet-trace
    // gc-verbose as the actual dominant per-gen2-pause cost - the raw
    // byte[] file buffer it's decoded from has zero embedded object
    // references and costs the mark phase nothing to trace no matter how
    // large it is, despite looking like the obvious culprit by raw size.
    file = null;

    string processName = Path.GetFileNameWithoutExtension(filePath);

    // The export phase's own progress reporting (5 sub-writer phases) is driven
    // entirely from inside GcJsonExporter.WriteToFile itself - see that
    // method's own comment for why it calls ProgressReporter directly
    // rather than taking an onProgress parameter like every phase above.
    ExportTiming exportTiming = GcJsonExporter.WriteToFile(jsonOutputPath, gcEventsForJson, allocationEventsForJson, exceptionEventsForJson, eventOverviewForJson, sampleEventsForJson, contentionEventsForJson, threadingSummaryForJson, stackTable, symbolTable, processName, ticksBinaryPath, captureDurationMSec, out CpuProfileJsonExporter.SampleTimeline cpuSampleTimeline);
    long exportMs = phaseStopwatch.ElapsedMilliseconds;
    phaseStopwatch.Restart();

    // The binary container the extension will consume instead of the JSON -
    // see Binary/BinaryCaptureFormat.cs for the format and why it exists.
    // Written from the SAME in-memory results the JSON writer above just
    // consumed, in the same run, which is what makes --json usable as an
    // oracle: BinaryCaptureDiffTests reads both and compares them field for
    // field. Sections are migrated off JSON one at a time, so for now this
    // is written IN ADDITION to the JSON rather than instead of it.
    if (binaryOutputPath != null)
    {
        using (BinaryCaptureWriter captureWriter = BinaryCaptureWriter.Create(binaryOutputPath))
        {
            if (cpuSampleTimeline != null)
            {
                CpuBinarySections.WriteSampleTimeline(captureWriter, cpuSampleTimeline);
            }
        }
    }

    // Timed separately rather than folded into export= above: this write is
    // outside GcJsonExporter.WriteToFile, and as the migration moves sections
    // off JSON this number is the one that grows while export= shrinks, so
    // keeping them apart is what makes that trade visible run to run. Reads
    // 0ms without --binary, which is the extension's current path.
    long binaryExportMs = phaseStopwatch.ElapsedMilliseconds;

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
    // export=' own (alloc=..,exc=..,cpu=..,cont=..,gc=..) breakdown is
    // permanent, not throwaway, instrumentation - added specifically so
    // Progress/ProgressPlan.cs's own export sub-writer weight constants can
    // be recalibrated against a real capture with a single CLI run (read this
    // line) rather than needing scaffolding re-added each time - see that
    // file's own header comment.
    //
    // It is "export", not "jsonExport", because that phase has not written
    // only JSON for a while: alloc= includes the allocation-tick BINARY
    // sidecar (AllocationSummaryBuilder.WriteTicks - the ticks array never
    // went into the JSON at all), and binary= below covers the --binary
    // capture container, which used to sit outside every timer in this line.
    Console.Error.WriteLine(
        $"Timing: read={readMs}ms ({totalEventCount} events) " +
        $"projectors={projectorsMs}ms wall, concurrent[" +
        $"gcProject={gcProjectMs}ms ({gcEventsForJson.Count} GCs) " +
        $"allocationProject={allocationProjectMs}ms ({allocationEventsForJson.Count} ticks) " +
        $"exceptionProject={exceptionProjectMs}ms ({exceptionEventsForJson.Count} exceptions) " +
        $"eventOverview={eventOverviewMs}ms ({eventOverviewForJson.EventTypes.Count} distinct event types) " +
        $"symbolTable={symbolTableMs}ms " +
        $"sampleProject={sampleProjectMs}ms ({sampleEventsForJson.Count} samples) " +
        $"contentionProject={contentionProjectMs}ms ({contentionEventsForJson.Count} contentions) " +
        $"threadingProject={threadingProjectMs}ms ({threadingSummaryForJson.Adjustments.Count} pool adjustments)] " +
        $"export={exportMs}ms(alloc={exportTiming.AllocationMs}ms,exc={exportTiming.ExceptionMs}ms,cpu={exportTiming.CpuMs}ms,cont={exportTiming.ContentionMs}ms,gc={exportTiming.GcMs}ms) " +
        $"binaryExport={binaryExportMs}ms " +
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
        sampleEventIdStackLen[record.EventId] = file.Stacks.FramesAt(record.StackIndex).Length;
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
