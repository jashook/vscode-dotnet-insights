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
// pre-aggregated view. Also builds two drill-down shapes sharing one
// aggregation pass (see BuildDrillDownAggregates): "drillDown", for each
// (type, 1-second bucket) cell the stacked "Allocated by Type Over Time"
// chart can be clicked on, the resolved call stacks that produced that
// cell's allocations; and "typeDrillDown", a parallel array to topTypes -
// for every ranked type the global table shows, every resolved call stack
// that allocated it *anywhere in the whole capture*, not scoped to one
// bucket - lets the global table link a type directly to its full set of
// allocating call stacks.
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

    // typeDrillDown (see WriteTypeDrillDown) merges stacks across the whole
    // capture rather than one 1-second bucket, so it naturally accumulates
    // more distinct call paths per type than a single cell ever would -
    // a higher cap than DrillDownStacksPerCellLimit is deliberate here.
    private const int DrillDownStacksPerTypeLimit = 100;

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
        // Same helper, just indexing every ranked type (up to TopTypesLimit)
        // instead of only the chart's top 8 - typeDrillDown (see
        // WriteTypeDrillDown) needs every type the global topTypes table
        // shows to be drillable, not just the ones with their own chart
        // column.
        Dictionary<string, int> typeIndexByName = BuildColumnIndexByType(sortedStats, topTypesCount);

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

        // Single pass over every tick builds both drill-down shapes at once
        // (see BuildDrillDownAggregates) - cheaper than two independent
        // O(totalTicks) passes now that there are two things to aggregate.
        DrillDownAggregates aggregates = BuildDrillDownAggregates(allocationEvents, columnIndexByType, typeIndexByName, topTypesCount, stacksById);

        writer.WritePropertyName("drillDown");
        WriteCellDrillDown(writer, aggregates.ByCell, stacksById, symbolTable);

        writer.WritePropertyName("typeDrillDown");
        WriteTypeDrillDown(writer, aggregates.ByType, stacksById, symbolTable);

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

    private struct DrillDownAggregates
    {
        public Dictionary<(int TypeIndex, int BucketIndex), Dictionary<int, StackAggregate>> ByCell;
        public Dictionary<int, StackAggregate>[] ByType;
    }

    // Single pass over every tick, building both drill-down shapes at once:
    // ByCell groups by (typeIndex, bucketIndex) for the stacked chart's own
    // top ChartTopTypesLimit(8) types only (see WriteCellDrillDown) - "Other"
    // -column ticks are skipped entirely, not drillable, since mixing many
    // unrelated types under one cell would make "the stack for Other"
    // misleadingly imply it's one thing. ByType groups by type alone
    // (merged across every bucket) for every one of the topTypesCount
    // ranked types the global table shows (see WriteTypeDrillDown) - lets
    // that table link a type directly to its full set of allocating call
    // stacks across the whole capture, not just one chart segment's slice
    // of it.
    //
    // Both aggregations key stacks by StackId, with -1 a synthetic id
    // (distinct from any real StackId, which are always >= 0) standing in
    // for "no stack captured" (StackId 0, or stack-walking wasn't enabled
    // for that tick) - grouped instead of silently dropped, so totals still
    // reconcile exactly with typeTimeline's own per-cell totals.
    private static DrillDownAggregates BuildDrillDownAggregates(List<AllocationEvent> allocationEvents, Dictionary<string, int> columnIndexByType, Dictionary<string, int> typeIndexByName, int topTypesCount, Dictionary<int, long[]> stacksById)
    {
        int bucketCount = ComputeBucketCount(allocationEvents);

        DrillDownAggregates aggregates = new DrillDownAggregates();
        // Keyed by (typeIndex, bucketIndex) as a value-type tuple, not the
        // formatted "typeIndex:bucketIndex" string used in the JSON output -
        // a string-interpolate-then-hash per tick was measured (dotnet-trace,
        // real 76k-tick capture) as a meaningful chunk of this method's cost.
        // The formatted string is built once per distinct cell instead, in
        // WriteCellDrillDown - there are far fewer cells than ticks.
        aggregates.ByCell = new Dictionary<(int, int), Dictionary<int, StackAggregate>>();
        aggregates.ByType = new Dictionary<int, StackAggregate>[topTypesCount];

        for (int eventIndex = 0; eventIndex < allocationEvents.Count; ++eventIndex)
        {
            AllocationEvent allocationEvent = allocationEvents[eventIndex];
            string typeName = string.IsNullOrEmpty(allocationEvent.TypeName) ? "<unknown>" : allocationEvent.TypeName;

            // -1 is a synthetic id, distinct from any real StackId (which
            // are always >= 0) - real StackId 0 conventionally means "no
            // stack" too, so both land in the same bucket. Shared by both
            // aggregations below - it doesn't depend on which one a tick
            // lands in.
            int stackKey = (allocationEvent.StackId == 0 || !stacksById.ContainsKey(allocationEvent.StackId)) ? -1 : allocationEvent.StackId;

            if (bucketCount > 0)
            {
                int chartTypeIndex;
                if (columnIndexByType.TryGetValue(typeName, out chartTypeIndex))
                {
                    int bucketIndex = ComputeBucketIndex(allocationEvent.RelativeMSec, bucketCount);
                    (int TypeIndex, int BucketIndex) cellKey = (chartTypeIndex, bucketIndex);

                    Dictionary<int, StackAggregate> cellStacks;
                    if (!aggregates.ByCell.TryGetValue(cellKey, out cellStacks))
                    {
                        cellStacks = new Dictionary<int, StackAggregate>();
                        aggregates.ByCell[cellKey] = cellStacks;
                    }

                    AddToStackAggregate(cellStacks, stackKey, allocationEvent.AllocationAmount);
                }
            }

            int globalTypeIndex;
            if (typeIndexByName.TryGetValue(typeName, out globalTypeIndex))
            {
                Dictionary<int, StackAggregate> typeStacks = aggregates.ByType[globalTypeIndex];
                if (typeStacks == null)
                {
                    typeStacks = new Dictionary<int, StackAggregate>();
                    aggregates.ByType[globalTypeIndex] = typeStacks;
                }

                AddToStackAggregate(typeStacks, stackKey, allocationEvent.AllocationAmount);
            }
        }

        return aggregates;
    }

    private static void AddToStackAggregate(Dictionary<int, StackAggregate> stacks, int stackKey, long allocationAmount)
    {
        StackAggregate aggregate;
        if (!stacks.TryGetValue(stackKey, out aggregate))
        {
            aggregate = new StackAggregate();
            aggregate.StackId = stackKey;
            stacks[stackKey] = aggregate;
        }

        aggregate.TotalBytes += allocationAmount;
        ++aggregate.TickCount;
    }

    // One stack's { frames, tickCount, totalBytes } JSON object - shared by
    // WriteCellDrillDown and WriteTypeDrillDown so both agree on exactly
    // how a resolved (or unresolved/no-stack) call stack is represented.
    // "leaf-first" frame order, matching Blocks/StackBlock.cs's own decoded
    // IP order.
    private static void WriteStackAggregate(Utf8JsonWriter writer, StackAggregate aggregate, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
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

    // For each (typeIndex, bucketIndex) cell the stacked chart can be
    // clicked on, the resolved call stacks that produced that cell's
    // allocations, ranked by bytes and capped at DrillDownStacksPerCellLimit.
    private static void WriteCellDrillDown(Utf8JsonWriter writer, Dictionary<(int TypeIndex, int BucketIndex), Dictionary<int, StackAggregate>> stacksByCell, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
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
                WriteStackAggregate(writer, cellStackList[stackIndex], stacksById, symbolTable);
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // One entry per ranked type in topTypes above (same order - typeIndex i
    // here corresponds to topTypes[i]), each the resolved call stacks that
    // allocated that type *anywhere in the whole capture*, ranked by bytes
    // and capped at DrillDownStacksPerTypeLimit - unlike "drillDown" above,
    // not scoped to a single 1-second bucket. Lets the global ranked types
    // table link a type directly to its full allocating call stacks, not
    // just whichever one chart segment happened to be clicked.
    private static void WriteTypeDrillDown(Utf8JsonWriter writer, Dictionary<int, StackAggregate>[] stacksByType, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
        writer.WriteStartArray();

        for (int typeIndex = 0; typeIndex < stacksByType.Length; ++typeIndex)
        {
            Dictionary<int, StackAggregate> typeStacks = stacksByType[typeIndex];

            writer.WriteStartArray();

            if (typeStacks != null)
            {
                List<StackAggregate> stackList = new List<StackAggregate>(typeStacks.Values);
                stackList.Sort((left, right) => right.TotalBytes.CompareTo(left.TotalBytes));

                int stackCount = stackList.Count < DrillDownStacksPerTypeLimit ? stackList.Count : DrillDownStacksPerTypeLimit;
                for (int stackIndex = 0; stackIndex < stackCount; ++stackIndex)
                {
                    WriteStackAggregate(writer, stackList[stackIndex], stacksById, symbolTable);
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
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
