////////////////////////////////////////////////////////////////////////////////
// Module: AllocationJsonExporter.cs
//
// Notes:
// Builds the "Heap Contents" view's JSON payload from a List<AllocationEvent>
// (raw per-tick samples): a ranked "what's allocating the most" table
// (aggregated by TypeName - see topTypes below) plus the raw ticks
// themselves (RelativeMSec/AllocationAmount only - TypeName is already
// covered by topTypes, so it's dropped per-tick to keep the list lean) so
// the webview can plot every individual allocation-tick event, matching how
// GcJsonExporter.cs already ships full per-GC fidelity rather than a
// pre-aggregated view. Also builds "drillDown": for each (type, 1-second
// bucket) cell the stacked "Allocated by Type Over Time" chart can be
// clicked on, the resolved call stacks (Rundown/MethodSymbolTable.cs) that
// produced that cell's allocations - see BuildDrillDown.
//
// Writes directly into a Utf8JsonWriter rather than building a
// System.Text.Json.Nodes.JsonObject/JsonArray tree first - a real capture's
// ticks list can be tens of thousands of entries, and materializing one
// boxed JsonObject per tick (plus per-field JsonValue wrappers) before ever
// serializing anything was measured (dotnet-trace, real 63MB/76k-tick
// capture) as the single largest contributor to nettraceParser's wall time,
// dwarfing both the binary decode step and the GC-event projection. This
// still gets the same "structured, typed write calls instead of hand-built
// interpolated strings" safety property this file's sibling
// (GcJsonExporter.cs) originally chose JsonNode for - Utf8JsonWriter is the
// same BCL, just without the intermediate object graph.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Gc {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;
using System.Text.Json;

using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class AllocationSummaryBuilder
{
    private const int TopTypesLimit = 100;

    // Separate, much smaller cap than TopTypesLimit - a stacked bar chart
    // with more than a handful of segments becomes an unreadable wall of
    // slivers. Anything outside this top set is folded into a single
    // "Other" column instead of being dropped.
    private const int ChartTopTypesLimit = 8;

    // 1 second, matching allocationStats.js's own DEFAULT_BUCKET_WIDTH_MSEC
    // for the rate chart above this one - kept as an independent constant
    // (not shared code) since one lives in C#, the other in the webview.
    private const double TypeTimelineBucketWidthMSec = 1000;

    // "Other" (the catch-all column beyond ChartTopTypesLimit) is
    // deliberately not drillable - it mixes many unrelated types together,
    // so "the stack for Other" would misleadingly imply it's one thing.
    // Ticks landing in that column simply aren't grouped into any drillDown
    // cell.
    private const int DrillDownStacksPerCellLimit = 50;

    private class TypeAllocStats
    {
        public string TypeName;
        public long TotalBytes;
        public int TickCount;
        public int SmallCount;
        public int LargeCount;
        public int PinnedCount;
    }

    // Writes the "allocationSummary" object (start-to-end, including its own
    // enclosing braces) directly to writer - callers just do
    // writer.WritePropertyName("allocationSummary"); AllocationSummaryBuilder.Write(writer, ...);
    public static void Write(Utf8JsonWriter writer, List<AllocationEvent> allocationEvents, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
        Dictionary<string, TypeAllocStats> statsByType = new Dictionary<string, TypeAllocStats>();
        long totalSampledBytes = 0;

        for (int eventIndex = 0; eventIndex < allocationEvents.Count; ++eventIndex)
        {
            AllocationEvent allocationEvent = allocationEvents[eventIndex];
            string typeName = string.IsNullOrEmpty(allocationEvent.TypeName) ? "<unknown>" : allocationEvent.TypeName;

            TypeAllocStats stats;
            if (!statsByType.TryGetValue(typeName, out stats))
            {
                stats = new TypeAllocStats();
                stats.TypeName = typeName;
                statsByType[typeName] = stats;
            }

            stats.TotalBytes += allocationEvent.AllocationAmount;
            ++stats.TickCount;

            if (allocationEvent.AllocationKind == GCAllocationKind.Large)
            {
                ++stats.LargeCount;
            }
            else if (allocationEvent.AllocationKind == GCAllocationKind.Pinned)
            {
                ++stats.PinnedCount;
            }
            else
            {
                ++stats.SmallCount;
            }

            totalSampledBytes += allocationEvent.AllocationAmount;
        }

        List<TypeAllocStats> sortedStats = new List<TypeAllocStats>(statsByType.Values);
        sortedStats.Sort(CompareByTotalBytesDescending);

        int topTypesCount = sortedStats.Count < TopTypesLimit ? sortedStats.Count : TopTypesLimit;
        int chartTypeCount = sortedStats.Count < ChartTopTypesLimit ? sortedStats.Count : ChartTopTypesLimit;
        Dictionary<string, int> columnIndexByType = BuildColumnIndexByType(sortedStats, chartTypeCount);

        writer.WriteStartObject();

        writer.WriteNumber("totalSampledBytes", totalSampledBytes);
        writer.WriteNumber("distinctTypeCount", statsByType.Count);
        writer.WriteNumber("totalTickCount", allocationEvents.Count);

        writer.WritePropertyName("topTypes");
        writer.WriteStartArray();
        for (int typeIndex = 0; typeIndex < topTypesCount; ++typeIndex)
        {
            TypeAllocStats stats = sortedStats[typeIndex];

            writer.WriteStartObject();
            writer.WriteString("TypeName", stats.TypeName);
            writer.WriteNumber("TotalBytes", stats.TotalBytes);
            writer.WriteNumber("TickCount", stats.TickCount);
            writer.WriteNumber("SmallCount", stats.SmallCount);
            writer.WriteNumber("LargeCount", stats.LargeCount);
            writer.WriteNumber("PinnedCount", stats.PinnedCount);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("ticks");
        WriteTicks(writer, allocationEvents);

        writer.WritePropertyName("typeTimeline");
        WriteTypeTimeline(writer, allocationEvents, sortedStats, chartTypeCount, columnIndexByType);

        writer.WritePropertyName("drillDown");
        WriteDrillDown(writer, allocationEvents, chartTypeCount, columnIndexByType, stacksById, symbolTable);

        writer.WriteEndObject();
    }

    // Shared by WriteTypeTimeline and WriteDrillDown so both agree on
    // exactly which types get their own column vs. fall into "Other" -
    // single source of truth for that boundary, per this file's own header
    // comment.
    private static Dictionary<string, int> BuildColumnIndexByType(List<TypeAllocStats> sortedStats, int chartTypeCount)
    {
        Dictionary<string, int> columnIndexByType = new Dictionary<string, int>();

        for (int typeIndex = 0; typeIndex < chartTypeCount; ++typeIndex)
        {
            columnIndexByType[sortedStats[typeIndex].TypeName] = typeIndex;
        }

        return columnIndexByType;
    }

    private static int ComputeBucketIndex(double relativeMSec, int bucketCount)
    {
        int bucketIndex = (int)(relativeMSec / TypeTimelineBucketWidthMSec);

        if (bucketIndex >= bucketCount)
        {
            bucketIndex = bucketCount - 1;
        }
        else if (bucketIndex < 0)
        {
            bucketIndex = 0;
        }

        return bucketIndex;
    }

    private static int ComputeBucketCount(List<AllocationEvent> allocationEvents)
    {
        if (allocationEvents.Count == 0)
        {
            return 0;
        }

        double lastRelativeMSec = 0;
        for (int eventIndex = 0; eventIndex < allocationEvents.Count; ++eventIndex)
        {
            if (allocationEvents[eventIndex].RelativeMSec > lastRelativeMSec)
            {
                lastRelativeMSec = allocationEvents[eventIndex].RelativeMSec;
            }
        }

        return (int)(lastRelativeMSec / TypeTimelineBucketWidthMSec) + 1;
    }

    // Per-second (TypeTimelineBucketWidthMSec) bytes-by-type breakdown for
    // the stacked bar chart under the allocation-rate chart. TypeName is
    // deliberately not carried on individual ticks (WriteTicks/AllocationEvent
    // - see this file's header comment), so this is the only place that
    // needs a per-event/per-type join; the result is normalized into a
    // shared "types" column list plus parallel per-bucket byte arrays
    // (rather than repeating type name strings as JSON object keys in every
    // bucket) to keep the payload compact.
    private static void WriteTypeTimeline(Utf8JsonWriter writer, List<AllocationEvent> allocationEvents, List<TypeAllocStats> sortedStats, int chartTypeCount, Dictionary<string, int> columnIndexByType)
    {
        writer.WriteStartObject();
        writer.WriteNumber("bucketWidthMSec", TypeTimelineBucketWidthMSec);

        writer.WritePropertyName("types");
        writer.WriteStartArray();
        for (int typeIndex = 0; typeIndex < chartTypeCount; ++typeIndex)
        {
            writer.WriteStringValue(sortedStats[typeIndex].TypeName);
        }

        // Always present as the last column, even if every type already fit
        // in the chart's top set (it just stays 0 in that case) - simpler
        // than conditionally omitting it.
        int otherColumnIndex = chartTypeCount;
        writer.WriteStringValue("Other");
        writer.WriteEndArray();

        int bucketCount = ComputeBucketCount(allocationEvents);
        if (bucketCount == 0)
        {
            writer.WritePropertyName("buckets");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }

        int columnCount = chartTypeCount + 1;
        long[,] bytesByBucketAndType = new long[bucketCount, columnCount];

        for (int eventIndex = 0; eventIndex < allocationEvents.Count; ++eventIndex)
        {
            AllocationEvent allocationEvent = allocationEvents[eventIndex];
            int bucketIndex = ComputeBucketIndex(allocationEvent.RelativeMSec, bucketCount);
            string typeName = string.IsNullOrEmpty(allocationEvent.TypeName) ? "<unknown>" : allocationEvent.TypeName;

            int columnIndex;
            if (!columnIndexByType.TryGetValue(typeName, out columnIndex))
            {
                columnIndex = otherColumnIndex;
            }

            bytesByBucketAndType[bucketIndex, columnIndex] += allocationEvent.AllocationAmount;
        }

        writer.WritePropertyName("buckets");
        writer.WriteStartArray();
        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            writer.WriteStartObject();
            writer.WriteNumber("bucketStartMSec", bucketIndex * TypeTimelineBucketWidthMSec);

            writer.WritePropertyName("bytesByType");
            writer.WriteStartArray();
            for (int columnIndex = 0; columnIndex < columnCount; ++columnIndex)
            {
                writer.WriteNumberValue(bytesByBucketAndType[bucketIndex, columnIndex]);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private class StackAggregate
    {
        public int StackId;
        public long TotalBytes;
        public int TickCount;
    }

    // For each (typeIndex, bucketIndex) cell the stacked chart can be
    // clicked on, groups that cell's ticks by StackId and resolves each
    // distinct stack once (Rundown/MethodSymbolTable.cs) - "leaf-first"
    // frame order, matching Blocks/StackBlock.cs's own decoded IP order.
    // "Other"-column ticks are skipped entirely (not drillable - see this
    // file's header comment); a tick with no captured stack (StackId not in
    // stacksById, e.g. StackId 0 or stack-walking wasn't enabled for that
    // tick) is grouped under a synthetic "no stack captured" entry instead
    // of being silently dropped, so cell totals still reconcile exactly
    // with typeTimeline's own per-cell totals.
    private static void WriteDrillDown(Utf8JsonWriter writer, List<AllocationEvent> allocationEvents, int chartTypeCount, Dictionary<string, int> columnIndexByType, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
        int bucketCount = ComputeBucketCount(allocationEvents);

        // Keyed by (typeIndex, bucketIndex) as a value-type tuple, not the
        // formatted "typeIndex:bucketIndex" string used in the JSON output -
        // this loop runs once per tick (tens of thousands for a busy
        // capture), and a string-interpolate-then-hash per tick was measured
        // (dotnet-trace, real 76k-tick capture) as a meaningful chunk of this
        // method's cost. The formatted string is built once per distinct
        // cell instead, in the write loop below - there are far fewer cells
        // than ticks.
        Dictionary<(int TypeIndex, int BucketIndex), Dictionary<int, StackAggregate>> stacksByCell = new Dictionary<(int, int), Dictionary<int, StackAggregate>>();

        if (bucketCount > 0)
        {
            for (int eventIndex = 0; eventIndex < allocationEvents.Count; ++eventIndex)
            {
                AllocationEvent allocationEvent = allocationEvents[eventIndex];
                string typeName = string.IsNullOrEmpty(allocationEvent.TypeName) ? "<unknown>" : allocationEvent.TypeName;

                int typeIndex;
                if (!columnIndexByType.TryGetValue(typeName, out typeIndex))
                {
                    continue;
                }

                int bucketIndex = ComputeBucketIndex(allocationEvent.RelativeMSec, bucketCount);
                (int TypeIndex, int BucketIndex) cellKey = (typeIndex, bucketIndex);

                Dictionary<int, StackAggregate> cellStacks;
                if (!stacksByCell.TryGetValue(cellKey, out cellStacks))
                {
                    cellStacks = new Dictionary<int, StackAggregate>();
                    stacksByCell[cellKey] = cellStacks;
                }

                // -1 is a synthetic id, distinct from any real StackId
                // (which are always >= 0) - real StackId 0 conventionally
                // means "no stack" too, so both land in the same bucket.
                int stackKey = (allocationEvent.StackId == 0 || !stacksById.ContainsKey(allocationEvent.StackId)) ? -1 : allocationEvent.StackId;

                StackAggregate aggregate;
                if (!cellStacks.TryGetValue(stackKey, out aggregate))
                {
                    aggregate = new StackAggregate();
                    aggregate.StackId = stackKey;
                    cellStacks[stackKey] = aggregate;
                }

                aggregate.TotalBytes += allocationEvent.AllocationAmount;
                ++aggregate.TickCount;
            }
        }

        writer.WriteStartObject();
        writer.WritePropertyName("cells");
        writer.WriteStartObject();

        foreach (KeyValuePair<(int TypeIndex, int BucketIndex), Dictionary<int, StackAggregate>> cellEntry in stacksByCell)
        {
            List<StackAggregate> cellStackList = new List<StackAggregate>(cellEntry.Value.Values);
            cellStackList.Sort((left, right) => right.TotalBytes.CompareTo(left.TotalBytes));

            int stackCount = cellStackList.Count < DrillDownStacksPerCellLimit ? cellStackList.Count : DrillDownStacksPerCellLimit;

            writer.WritePropertyName($"{cellEntry.Key.TypeIndex}:{cellEntry.Key.BucketIndex}");
            writer.WriteStartArray();

            for (int stackIndex = 0; stackIndex < stackCount; ++stackIndex)
            {
                StackAggregate aggregate = cellStackList[stackIndex];

                writer.WriteStartObject();

                writer.WritePropertyName("frames");
                writer.WriteStartArray();
                if (aggregate.StackId == -1)
                {
                    writer.WriteStringValue("<no stack captured>");
                }
                else
                {
                    long[] instructionPointers = stacksById[aggregate.StackId];
                    for (int frameIndex = 0; frameIndex < instructionPointers.Length; ++frameIndex)
                    {
                        writer.WriteStringValue(symbolTable.Resolve(instructionPointers[frameIndex]));
                    }
                }
                writer.WriteEndArray();

                writer.WriteNumber("tickCount", aggregate.TickCount);
                writer.WriteNumber("totalBytes", aggregate.TotalBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static int CompareByTotalBytesDescending(TypeAllocStats left, TypeAllocStats right)
    {
        return right.TotalBytes.CompareTo(left.TotalBytes);
    }

    // Sorted by RelativeMSec defensively (matches GcJsonExporter.cs's own
    // heap-sort-before-serializing precedent) rather than trusting wire
    // order to already be time-ordered.
    private static void WriteTicks(Utf8JsonWriter writer, List<AllocationEvent> allocationEvents)
    {
        List<AllocationEvent> sortedEvents = new List<AllocationEvent>(allocationEvents);
        sortedEvents.Sort(CompareByRelativeMSecAscending);

        writer.WriteStartArray();

        for (int eventIndex = 0; eventIndex < sortedEvents.Count; ++eventIndex)
        {
            AllocationEvent allocationEvent = sortedEvents[eventIndex];

            writer.WriteStartObject();
            writer.WriteNumber("RelativeMSec", allocationEvent.RelativeMSec);
            writer.WriteNumber("AllocationAmount", allocationEvent.AllocationAmount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static int CompareByRelativeMSecAscending(AllocationEvent left, AllocationEvent right)
    {
        return left.RelativeMSec.CompareTo(right.RelativeMSec);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
