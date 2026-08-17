////////////////////////////////////////////////////////////////////////////////
// Module: GcJsonExporter.cs
//
// Notes:
// Serializes a List<GcEvent> into exactly the JSON shape the VS Code
// extension's DotnetInsightsGcSnapshotEditor.gcDataFromXml already produces
// (dotnetInsights/src/DotnetInsightsGcSnapshotEditor.ts) - field names and
// nesting copied from that function directly so the extension's existing
// chart-rendering code needs no changes to consume nettrace-derived data.
//
// Writes directly into a Utf8JsonWriter (streamed straight to the output
// file) rather than building a System.Text.Json.Nodes tree and serializing
// it afterward - the tree-then-serialize approach was measured (dotnet-trace,
// real 63MB/76k-allocation-tick capture) as the largest single contributor
// to nettraceParser's wall time, mostly from AllocationSummaryBuilder's tick
// list (see AllocationJsonExporter.cs, which this now composes with directly
// via AllocationSummaryBuilder.Write). Utf8JsonWriter keeps the same
// "structured, typed write calls instead of hand-built interpolated
// strings" safety property this file originally chose JsonNode for.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Gc {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Progress;
using DotnetInsights.NetTrace.Rundown;
using DotnetInsights.NetTrace.Threading;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Per-sub-writer breakdown of the export phase's own total time - returned from
// WriteToFile so Program.cs's own "Timing: ..." diagnostic line (the one
// place this codebase already reports phase timing - see that file's own
// comment) can report it, rather than this class printing its own
// competing diagnostic output. Exists specifically so
// Progress/ProgressPlan.cs's own export sub-writer weight constants
// can be recalibrated against a real capture with a single CLI run (read
// this line) instead of re-adding throwaway Stopwatch instrumentation
// each time - see that file's own header comment.
public readonly struct ExportTiming
{
    public readonly long AllocationMs;
    public readonly long ExceptionMs;
    public readonly long CpuMs;
    public readonly long ContentionMs;
    // Broken out as of the thread-activity classification: this writer used to
    // be a rounding error on the line and now makes its own pass over every
    // CPU sample in the capture (Threading/ThreadActivityProfiler.cs), so
    // leaving it in the unlabelled remainder would hide the largest single
    // thing this phase does that nothing else reports.
    public readonly long ThreadingMs;
    public readonly long GcMs;

    public ExportTiming(long allocationMs, long exceptionMs, long cpuMs, long contentionMs, long threadingMs, long gcMs)
    {
        this.AllocationMs = allocationMs;
        this.ExceptionMs = exceptionMs;
        this.CpuMs = cpuMs;
        this.ContentionMs = contentionMs;
        this.ThreadingMs = threadingMs;
        this.GcMs = gcMs;
    }
}

public static class GcJsonExporter
{
    // ticksBinaryPath: forwarded to AllocationSummaryBuilder.Write - see its
    // own comment on WriteTicks for why the allocation-tick array is a
    // binary sidecar file next to outputPath rather than inline JSON.
    // 1MB, not FileStream's own small default (4096 bytes) - AllocationSummaryBuilder
    // now calls writer.Flush() once per drill-down cell/type (hundreds of
    // times on a real capture) instead of only once at Dispose, so most of
    // those flushes should just append into this buffer rather than each
    // becoming its own write() syscall.
    private const int OutputFileStreamBufferSize = 1024 * 1024;

    // cpuSampleTimeline: the CPU timeline this export just computed, handed
    // back so Binary/CpuBinarySections.cs can encode the SAME values into the
    // binary container in the same run - that shared origin is what lets
    // --json act as an oracle the binary section is diffed against.
    public static ExportTiming WriteToFile(string outputPath, List<GcEvent> gcEvents, List<AllocationEvent> allocationEvents, List<ExceptionEvent> exceptionEvents, EventOverview eventOverview, List<SampleEvent> sampleEvents, List<ContentionEvent> contentionEvents, ThreadingSummary threadingSummary, StackTable stackTable, MethodSymbolTable symbolTable, string processName, string ticksBinaryPath, double captureDurationMSec, out CpuProfileJsonExporter.SampleTimeline cpuSampleTimeline)
    {
        // Stays null when this capture has no CPU samples at all - the same
        // condition under which the "cpuProfile" JSON key is never written.
        cpuSampleTimeline = null;

        // Permanent (not throwaway) per-sub-writer timing - see
        // ExportTiming's own comment on why.
        Stopwatch subStopwatch = Stopwatch.StartNew();
        long allocationMs;
        long exceptionMs;
        long cpuMs;
        long contentionMs;
        long threadingMs;
        long gcMs;

        // Splits the jsonExport phase's own global percent range (see
        // Progress/ProgressPlan.cs's own header comment on why this is the
        // one place a per-item weight estimate compares genuinely
        // comparable things - 5 writers in the SAME phase of the SAME run)
        // across its 5 sub-writers, using THIS run's real counts. A no-op
        // to compute even when progress reporting is disabled entirely
        // (ProgressReporter.BeginPhase/CompletePhase below are themselves
        // no-ops in that case - see that class's own `enabled` gate) - so
        // this method takes no onProgress parameter of its own and instead
        // calls ProgressReporter directly, unlike NettraceFile.Read/the
        // projectors/the two instrumented sub-writers below (which are
        // each a single continuous pass, not a multi-phase orchestrator
        // dispatching to differently-labeled sub-phases the way this
        // method is).
        ExportSubWriterRanges subWriterRanges = ProgressPlan.PlanExportSubWriters(gcEvents.Count, allocationEvents.Count, exceptionEvents.Count, sampleEvents.Count, contentionEvents.Count);

        using (FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, OutputFileStreamBufferSize))
        using (Utf8JsonWriter writer = new Utf8JsonWriter(fileStream))
        {
            writer.WriteStartObject();

            writer.WriteString("processName", processName);

            // "allocationSummary" is only meaningful for nettrace input - the
            // .gcinfo/XML path never sets this key, which is what the extension's
            // GcSnapshotRenderer.ts uses (alongside its own explicit sourceFormat
            // parameter) to decide whether the nettrace-only "Heap Contents" view
            // has anything to show. See AllocationJsonExporter.cs.
            writer.WritePropertyName("allocationSummary");
            ProgressReporter.BeginPhase("Exporting allocation summary", subWriterRanges.Allocation.Start, subWriterRanges.Allocation.End);
            subStopwatch.Restart();
            AllocationSummaryBuilder.Write(writer, allocationEvents, stackTable, symbolTable, ticksBinaryPath, ProgressReporter.ReportFraction);
            allocationMs = subStopwatch.ElapsedMilliseconds;
            ProgressReporter.CompletePhase();

            // "exceptionSummary" is only meaningful for nettrace input, same
            // as allocationSummary above - the .gcinfo/XML path never sets
            // this key either. See ExceptionJsonExporter.cs. No internal
            // fine-grained tracking (unlike allocation/cpu below) - bounded
            // by real exception counts (tens of thousands, not millions, on
            // every real capture measured so far), so a start/complete snap
            // is visually indistinguishable from tracking it internally.
            writer.WritePropertyName("exceptionSummary");
            ProgressReporter.BeginPhase("Exporting exception summary", subWriterRanges.Exception.Start, subWriterRanges.Exception.End);
            subStopwatch.Restart();
            ExceptionJsonExporter.Write(writer, exceptionEvents, stackTable, symbolTable);
            exceptionMs = subStopwatch.ElapsedMilliseconds;
            ProgressReporter.CompletePhase();

            // "eventOverview" is also nettrace-only (same reasoning), but
            // unlike allocationSummary/exceptionSummary above it's always
            // meaningful whenever it's present at all - every nettrace
            // capture has *some* events, even one with zero GCs/allocations/
            // exceptions. See Overview/EventOverviewBuilder.cs. No dedicated
            // progress phase - bounded by distinct event TYPE count (dozens,
            // not per-event), negligible next to any of the 5 real
            // sub-writer phases either side of it.
            writer.WritePropertyName("eventOverview");
            writer.WriteStartObject();
            writer.WriteNumber("totalEventCount", eventOverview.TotalEventCount);
            writer.WritePropertyName("eventTypes");
            writer.WriteStartArray();
            for (int eventTypeIndex = 0; eventTypeIndex < eventOverview.EventTypes.Count; ++eventTypeIndex)
            {
                EventTypeCount eventTypeCount = eventOverview.EventTypes[eventTypeIndex];
                writer.WriteStartObject();
                writer.WriteString("providerName", eventTypeCount.ProviderName);
                writer.WriteString("displayName", eventTypeCount.DisplayName);
                writer.WriteNumber("eventId", eventTypeCount.EventId);
                writer.WriteNumber("count", eventTypeCount.Count);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            // "timeBreakdown" is also nettrace-only (same reasoning) - the
            // .gcinfo/XML path never sets this key either. See
            // Overview/TimeBreakdownBuilder.cs for the exact-vs-estimated
            // split between its four percentages. No dedicated progress
            // phase - a single pass over already-in-memory lists, negligible
            // next to any real sub-writer phase either side of it.
            TimeBreakdown timeBreakdown = TimeBreakdownBuilder.Build(gcEvents, contentionEvents, sampleEvents, stackTable, symbolTable, captureDurationMSec);

            writer.WritePropertyName("timeBreakdown");
            writer.WriteStartObject();
            writer.WriteBoolean("hasCaptureDuration", timeBreakdown.HasCaptureDuration);
            writer.WriteNumber("captureDurationMSec", timeBreakdown.CaptureDurationMSec);
            writer.WriteNumber("gcPercent", timeBreakdown.GcPercent);
            writer.WriteNumber("gcPauseMSec", timeBreakdown.GcPauseMSec);
            writer.WriteNumber("contentionPercent", timeBreakdown.ContentionPercent);
            writer.WriteNumber("contentionWaitMSec", timeBreakdown.ContentionWaitMSec);
            writer.WriteNumber("averageThreadsBlocked", timeBreakdown.AverageThreadsBlocked);
            writer.WriteBoolean("hasCpuSampleBreakdown", timeBreakdown.HasCpuSampleBreakdown);
            writer.WriteNumber("idlePercent", timeBreakdown.IdlePercent);
            writer.WriteNumber("cpuBoundPercent", timeBreakdown.CpuBoundPercent);
            writer.WriteEndObject();

            // "cpuProfile" is also nettrace-only (same reasoning as
            // allocationSummary/exceptionSummary above) - the .gcinfo/XML
            // path never sets this key either. See Cpu/CpuProfileJsonExporter.cs.
            writer.WritePropertyName("cpuProfile");
            ProgressReporter.BeginPhase("Exporting CPU profile", subWriterRanges.Cpu.Start, subWriterRanges.Cpu.End);
            subStopwatch.Restart();
            cpuSampleTimeline = CpuProfileJsonExporter.Write(writer, sampleEvents, stackTable, symbolTable, ProgressReporter.ReportFraction);
            cpuMs = subStopwatch.ElapsedMilliseconds;
            ProgressReporter.CompletePhase();

            // "contentionSummary" is also nettrace-only (same reasoning) -
            // the .gcinfo/XML path never sets this key. See
            // Contention/ContentionJsonExporter.cs. No internal tracking,
            // same reasoning as exceptionSummary above.
            writer.WritePropertyName("contentionSummary");
            ProgressReporter.BeginPhase("Exporting contention summary", subWriterRanges.Contention.Start, subWriterRanges.Contention.End);
            subStopwatch.Restart();
            ContentionJsonExporter.Write(writer, contentionEvents, stackTable, symbolTable);
            contentionMs = subStopwatch.ElapsedMilliseconds;
            ProgressReporter.CompletePhase();

            // "threadingSummary" is nettrace-only, same reasoning as the
            // blocks above. Written after contention because its stall
            // correlation reads the CPU samples, and its own method names are
            // interned into a pool it owns (the Threading view is rendered
            // independently of the drill-down tables).
            writer.WritePropertyName("threadingSummary");
            List<string> threadingMethodNames = new List<string>();
            Dictionary<string, int> threadingMethodNameIndexByName = new Dictionary<string, int>();
            ProgressReporter.BeginPhase("Classifying threads", subWriterRanges.Threading.Start, subWriterRanges.Threading.End);
            subStopwatch.Restart();
            ThreadingJsonExporter.Write(writer, threadingSummary, sampleEvents, contentionEvents, stackTable, symbolTable, threadingMethodNames, threadingMethodNameIndexByName);
            threadingMs = subStopwatch.ElapsedMilliseconds;
            ProgressReporter.CompletePhase();

            writer.WritePropertyName("threadingMethodNames");
            writer.WriteStartArray();
            for (int nameIndex = 0; nameIndex < threadingMethodNames.Count; ++nameIndex)
            {
                writer.WriteStringValue(threadingMethodNames[nameIndex]);
            }
            writer.WriteEndArray();

            writer.WritePropertyName("gcData");
            writer.WriteStartArray();
            ProgressReporter.BeginPhase("Exporting GC data", subWriterRanges.Gc.Start, subWriterRanges.Gc.End);
            subStopwatch.Restart();

            for (int gcIndex = 0; gcIndex < gcEvents.Count; ++gcIndex)
            {
                if ((gcIndex & ProgressReporter.IndexProgressMask) == 0)
                {
                    ProgressReporter.ReportFraction(gcIndex / (double)gcEvents.Count);
                }

                GcEvent gcEvent = gcEvents[gcIndex];
                string reasonName = gcEvent.Reason.ToString();

                writer.WriteStartObject();
                writer.WritePropertyName("data");
                writer.WriteStartObject();

                writer.WriteNumber("Gen0MinSize", gcEvent.FinalYoungestDesired);
                writer.WriteNumber("generation", gcEvent.Generation);
                writer.WriteNumber("GenerationSize0", gcEvent.GenerationSize0);
                writer.WriteNumber("GenerationSize1", gcEvent.GenerationSize1);
                writer.WriteNumber("GenerationSize2", gcEvent.GenerationSize2);
                writer.WriteNumber("GenerationSizeLOH", gcEvent.GenerationSize3);
                writer.WriteNumber("GenerationSizePOH", gcEvent.GenerationSize4);
                writer.WriteNumber("Id", gcEvent.Id);
                // ISO-8601 (round-trip format) - directly parseable by JS's `new Date(...)`.
                // Converted to the machine's local timezone (gcEvent.Timestamp is UTC) so the
                // offset in the string reflects wall-clock time here, not UTC.
                writer.WriteString("DateTime", gcEvent.Timestamp.ToLocalTime().ToString("o"));
                writer.WriteString("kind", reasonName);
                writer.WriteNumber("NumHeaps", gcEvent.NumHeaps);
                writer.WriteNumber("PauseDurationMSec", gcEvent.PauseDurationMSec);
                writer.WriteNumber("PauseEndRelativeMSec", gcEvent.PauseEndRelativeMSec);
                writer.WriteNumber("PauseStartRelativeMSec", gcEvent.PauseStartRelativeMSec);
                writer.WriteString("Reason", reasonName);
                writer.WriteNumber("TotalHeapSize", gcEvent.TotalHeapSize);
                writer.WriteNumber("TotalPromoted", gcEvent.TotalPromotedSize0);
                writer.WriteNumber("TotalPromotedLOH", gcEvent.TotalPromotedSize3);
                writer.WriteNumber("TotalPromotedPOH", gcEvent.TotalPromotedSize4);
                writer.WriteNumber("TotalPromotedSize0", gcEvent.TotalPromotedSize0);
                writer.WriteNumber("TotalPromotedSize1", gcEvent.TotalPromotedSize1);
                writer.WriteNumber("TotalPromotedSize2", gcEvent.TotalPromotedSize2);
                // Matches an existing quirk in gcDataFromXml where Type reuses the
                // Reason text rather than a true concurrent/non-concurrent label -
                // kept for consistency with what the renderer already expects.
                writer.WriteString("Type", reasonName);
                writer.WriteNumber("GCDurationMSec", gcEvent.PauseDurationMSec);
                writer.WriteNumber("PinnedObjectCount", gcEvent.PinnedObjectCount);
                writer.WriteNumber("GlobalMechanisms", (int)gcEvent.GlobalMechanisms);

                // gcEvent.Heaps is populated in wire-arrival order (see
                // GcEventProjector.cs's GCPerHeapHistory handling), which for a
                // multi-heap (server GC) capture is not guaranteed to match
                // physical heap order. Both this array's own position and the
                // extension's per-heap charts/tables treat array position as the
                // heap number, so sorting by the heap's own reported HeapIndex
                // here is what makes "Heap N" actually mean heap N.
                List<ClrGcHeap> sortedHeaps = new List<ClrGcHeap>(gcEvent.Heaps);
                sortedHeaps.Sort((ClrGcHeap left, ClrGcHeap right) => left.HeapIndex.CompareTo(right.HeapIndex));

                writer.WritePropertyName("Heaps");
                writer.WriteStartArray();
                for (int heapIndex = 0; heapIndex < sortedHeaps.Count; ++heapIndex)
                {
                    ClrGcHeap heap = sortedHeaps[heapIndex];

                    writer.WriteStartObject();
                    writer.WriteNumber("HeapIndex", heap.HeapIndex);

                    writer.WritePropertyName("Generations");
                    writer.WriteStartObject();
                    for (int genIndex = 0; genIndex < heap.Generations.Length; ++genIndex)
                    {
                        ref readonly ClrGcGeneration gen = ref heap.Generations[genIndex];

                        writer.WritePropertyName(genIndex.ToString());
                        writer.WriteStartObject();
                        writer.WriteNumber("Fragmentation", gen.Fragmentation);
                        writer.WriteNumber("FreeListSpaceAfter", gen.FreeListSpaceAfter);
                        writer.WriteNumber("FreeListSpaceBefore", gen.FreeListSpaceBefore);
                        writer.WriteNumber("FreeObjSpaceAfter", gen.FreeObjSpaceAfter);
                        writer.WriteNumber("FreeObjSpaceBefore", gen.FreeObjSpaceBefore);
                        writer.WriteNumber("Id", genIndex);
                        writer.WriteNumber("In", gen.In);
                        writer.WriteNumber("NewAllocation", gen.NewAllocation);
                        writer.WriteNumber("NonePinnedSurv", gen.NonePinnedSurv);
                        writer.WriteNumber("ObjSizeAfter", gen.ObjSizeAfter);
                        writer.WriteNumber("ObjSpaceBefore", gen.ObjSpaceBefore);
                        writer.WriteNumber("Out", gen.Out);
                        writer.WriteNumber("PinnedSurv", gen.PinnedSurv);
                        writer.WriteNumber("SizeAfter", gen.SizeAfter);
                        writer.WriteNumber("SizeBefore", gen.SizeBefore);
                        writer.WriteNumber("SurvRate", gen.SurvRate);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
                writer.WriteEndObject();

                // Utf8JsonWriter never auto-flushes on its own - this loop
                // writes ~20 top-level fields plus a full Heaps/Generations
                // breakdown (17 fields x 5 generations x however many
                // heaps) per GC, with no flush anywhere in it before this
                // fix, so its internal ArrayBufferWriter<byte> buffer had
                // to keep doubling (System.Array.Resize) across the WHOLE
                // gcData array before any of it reached disk - same root
                // cause as AllocationJsonExporter.cs's own
                // WriteCellDrillDown/WriteTypeDrillDown and
                // Cpu/CpuProfileJsonExporter.cs's own WriteFlameTreeNode
                // (see both files' matching comments). Confirmed via
                // dotnet-trace profiling nettraceParser's own process
                // against a real capture with a nontrivial GC/heap count:
                // this exact call chain (WriteToFile -> Utf8JsonWriter.
                // WriteNumber -> ... -> Array.Resize) was ~34% of the
                // WHOLE process's sampled CPU time - the single largest
                // remaining cost even after fixing the same bug in
                // CpuProfileJsonExporter.cs. Flushing once per GC (not
                // once per heap/generation - a real capture's GC count is
                // small enough that finer granularity isn't needed) bounds
                // the buffer to roughly one GC's own JSON.
                writer.Flush();
            }

            gcMs = subStopwatch.ElapsedMilliseconds;
            ProgressReporter.CompletePhase();
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return new ExportTiming(allocationMs, exceptionMs, cpuMs, contentionMs, threadingMs, gcMs);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
