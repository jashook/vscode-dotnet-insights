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
using System.IO;
using System.Text.Json;

using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

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

    public static void WriteToFile(string outputPath, List<GcEvent> gcEvents, List<AllocationEvent> allocationEvents, List<ExceptionEvent> exceptionEvents, EventOverview eventOverview, MethodSymbolTable symbolTable, string processName, string ticksBinaryPath)
    {
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
            AllocationSummaryBuilder.Write(writer, allocationEvents, symbolTable, ticksBinaryPath);

            // "exceptionSummary" is only meaningful for nettrace input, same
            // as allocationSummary above - the .gcinfo/XML path never sets
            // this key either. See ExceptionJsonExporter.cs.
            writer.WritePropertyName("exceptionSummary");
            ExceptionJsonExporter.Write(writer, exceptionEvents, symbolTable);

            // "eventOverview" is also nettrace-only (same reasoning), but
            // unlike allocationSummary/exceptionSummary above it's always
            // meaningful whenever it's present at all - every nettrace
            // capture has *some* events, even one with zero GCs/allocations/
            // exceptions. See Overview/EventOverviewBuilder.cs.
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

            writer.WritePropertyName("gcData");
            writer.WriteStartArray();

            for (int gcIndex = 0; gcIndex < gcEvents.Count; ++gcIndex)
            {
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
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
