////////////////////////////////////////////////////////////////////////////////
// Module: TraceEventGcReader.cs
//
// Notes:
// Ground truth for GC event data, computed via
// Microsoft.Diagnostics.Tracing.TraceEvent - the same library PerfView's GC
// Stats view and dotnet-trace's own report tooling are built on - instead of
// nettraceParser's hand-rolled decoder (see nettraceParser/Gc/*.cs). Reading
// a .nettrace file this way needs no ETW/Windows dependency:
// EventPipeEventSource is pure managed and reads the file's own EventPipe
// blocks directly, the same as it does on the platform dotnet-trace itself
// runs on.
//
// TraceGC/GCHeapStats/GCGlobalHeapHistory's own field names (Number,
// GenerationSize0..4, FinalYoungestDesired, ...) already match
// nettraceParser's GcEvent/ClrGcHeapStats/ClrGcGlobalHeapHistory almost
// exactly - both decode the same CLR ETW manifest, TraceEvent's
// ClrTraceEventParser.cs was nettraceParser's own reference for it (see
// Gc/ClrGcTypes.cs's header comment) - so this reader is a thin projection,
// not a reimplementation.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GroundTruth {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class TraceEventGcReader
{
    // Reads every completed GC across every process in the capture (not just
    // one process) - nettraceParser's own GcEventProjector.Project makes the
    // same choice: it filters the whole EventRecord stream by provider name
    // only, with no per-process split, so a capture that happened to include
    // more than one process would be double-counted identically on both
    // sides rather than diverging for an unrelated reason.
    public static List<GcTruthRecord> Read(string tracePath)
    {
        List<GcTruthRecord> records = new List<GcTruthRecord>();

        using (EventPipeEventSource source = new EventPipeEventSource(tracePath))
        {
            TraceLoadedDotNetRuntimeExtensions.NeedLoadedDotNetRuntimes(source);
            source.Process();

            foreach (TraceProcess process in TraceProcessesExtensions.Processes(source))
            {
                TraceLoadedDotNetRuntime runtime = TraceLoadedDotNetRuntimeExtensions.LoadedDotNetRuntime(process);

                if (runtime == null || runtime.GC == null || runtime.GC.GCs == null)
                {
                    continue;
                }

                foreach (TraceGC gc in runtime.GC.GCs)
                {
                    // IsComplete+HeapStats mirrors nettraceParser's own HasEnd/
                    // HasHeapStats gate - only fully-correlated GCs are
                    // comparable; an in-flight GC at the end of the capture has
                    // no counterpart on nettraceParser's side either.
                    //
                    // GlobalHeapHistory is NOT required here, deliberately -
                    // verified against a real Server GC capture that TraceEvent
                    // itself does not always associate a distinct
                    // GlobalHeapHistory with an otherwise-IsComplete background
                    // GC (its own internal correlation has the same kind of
                    // ambiguity nettraceParser's GcEventProjector.Project fights
                    // against - see ResolveNextInGeneration's comment there).
                    // HasGlobalHeapHistory below records which is which so the
                    // diff test can skip comparing GlobalHeapHistory-derived
                    // fields (NumHeaps/FinalYoungestDesired/GlobalMechanisms)
                    // for a GC ground truth itself couldn't resolve one for,
                    // rather than comparing against a false default of zero.
                    if (!gc.IsComplete || gc.HeapStats == null)
                    {
                        continue;
                    }

                    GcTruthRecord record = new GcTruthRecord();
                    record.Number = gc.Number;
                    record.Generation = gc.Generation;
                    record.Reason = (int)gc.Reason;
                    record.Type = (int)gc.Type;
                    record.PauseDurationMSec = gc.PauseDurationMSec;
                    record.StartRelativeMSec = gc.StartRelativeMSec;
                    record.PauseStartRelativeMSec = gc.PauseStartRelativeMSec;

                    GCHeapStats heapStats = gc.HeapStats;
                    record.TotalHeapSize = heapStats.TotalHeapSize;
                    record.TotalPromoted = heapStats.TotalPromoted;
                    record.GenerationSize0 = heapStats.GenerationSize0;
                    record.GenerationSize1 = heapStats.GenerationSize1;
                    record.GenerationSize2 = heapStats.GenerationSize2;
                    record.GenerationSize3 = heapStats.GenerationSize3;
                    record.GenerationSize4 = heapStats.GenerationSize4;
                    record.TotalPromotedSize0 = heapStats.TotalPromotedSize0;
                    record.TotalPromotedSize1 = heapStats.TotalPromotedSize1;
                    record.TotalPromotedSize2 = heapStats.TotalPromotedSize2;
                    record.TotalPromotedSize3 = heapStats.TotalPromotedSize3;
                    record.TotalPromotedSize4 = heapStats.TotalPromotedSize4;
                    record.PinnedObjectCount = heapStats.PinnedObjectCount;

                    GCGlobalHeapHistory globalHistory = gc.GlobalHeapHistory;
                    record.HasGlobalHeapHistory = globalHistory != null;
                    if (globalHistory != null)
                    {
                        record.NumHeaps = globalHistory.NumHeaps;
                        record.FinalYoungestDesired = globalHistory.FinalYoungestDesired;
                        record.GlobalMechanisms = (int)globalHistory.GlobalMechanisms;
                    }

                    records.Add(record);
                }
            }
        }

        records.Sort((left, right) => left.Number.CompareTo(right.Number));

        return records;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GroundTruth)
