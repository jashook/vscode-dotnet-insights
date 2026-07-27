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

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
    //
    // ticksBinaryPath: see WriteTicks - the ticks array (11.9M+ entries on a
    // real 5-minute capture) is written to this separate binary sidecar
    // file instead of as JSON, with only a small descriptor object left in
    // the JSON output pointing at it (by convention/caller-known path, not
    // embedded - see Program.cs).
    public static void Write(Utf8JsonWriter writer, List<AllocationEvent> allocationEvents, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable, string ticksBinaryPath)
    {
        // Sorted once, in place, up front - every pass below (including
        // WriteTicks, which used to make its own defensive copy+sort just
        // for itself) is order-independent except WriteTicks' own output
        // order, so one shared sort replaces what was previously a separate
        // ~11.9M-element copy+sort pass late in this method. Also makes
        // ComputeBucketCount below O(1) (the last element is now the max
        // RelativeMSec) instead of an O(n) scan - and it no longer needs to
        // run twice (once each for WriteTypeTimeline and
        // BuildDrillDownAggregates).
        allocationEvents.Sort(CompareByRelativeMSecAscending);

        int bucketCount = allocationEvents.Count == 0 ? 0 : ComputeBucketIndex(allocationEvents[allocationEvents.Count - 1].RelativeMSec, int.MaxValue) + 1;

        Dictionary<string, TypeAllocStats> statsByType = new Dictionary<string, TypeAllocStats>();
        long totalSampledBytes = 0;

        Span<AllocationEvent> allocationEventsSpan = CollectionsMarshal.AsSpan(allocationEvents);
        for (int eventIndex = 0; eventIndex < allocationEventsSpan.Length; ++eventIndex)
        {
            ref readonly AllocationEvent allocationEvent = ref allocationEventsSpan[eventIndex];
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
        WriteTicks(writer, allocationEvents, ticksBinaryPath);

        writer.WritePropertyName("typeTimeline");
        WriteTypeTimeline(writer, allocationEvents, sortedStats, chartTypeCount, columnIndexByType, bucketCount);

        // Single pass over every tick builds both drill-down shapes at once
        // (see BuildDrillDownAggregates) - cheaper than two independent
        // O(totalTicks) passes now that there are two things to aggregate.
        DrillDownAggregates aggregates = BuildDrillDownAggregates(allocationEvents, columnIndexByType, typeIndexByName, topTypesCount, stacksById, bucketCount);

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

    // Per-second (TypeTimelineBucketWidthMSec) bytes-by-type breakdown for
    // the stacked bar chart under the allocation-rate chart. TypeName is
    // deliberately not carried on individual ticks (WriteTicks/AllocationEvent
    // - see this file's header comment), so this is the only place that
    // needs a per-event/per-type join; the result is normalized into a
    // shared "types" column list plus parallel per-bucket byte arrays
    // (rather than repeating type name strings as JSON object keys in every
    // bucket) to keep the payload compact.
    private static void WriteTypeTimeline(Utf8JsonWriter writer, List<AllocationEvent> allocationEvents, List<TypeAllocStats> sortedStats, int chartTypeCount, Dictionary<string, int> columnIndexByType, int bucketCount)
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

        Span<AllocationEvent> allocationEventsSpan = CollectionsMarshal.AsSpan(allocationEvents);
        for (int eventIndex = 0; eventIndex < allocationEventsSpan.Length; ++eventIndex)
        {
            ref readonly AllocationEvent allocationEvent = ref allocationEventsSpan[eventIndex];
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
    private static DrillDownAggregates BuildDrillDownAggregates(List<AllocationEvent> allocationEvents, Dictionary<string, int> columnIndexByType, Dictionary<string, int> typeIndexByName, int topTypesCount, Dictionary<int, long[]> stacksById, int bucketCount)
    {
        DrillDownAggregates aggregates = new DrillDownAggregates();
        // Keyed by (typeIndex, bucketIndex) as a value-type tuple, not the
        // formatted "typeIndex:bucketIndex" string used in the JSON output -
        // a string-interpolate-then-hash per tick was measured (dotnet-trace,
        // real 76k-tick capture) as a meaningful chunk of this method's cost.
        // The formatted string is built once per distinct cell instead, in
        // WriteCellDrillDown - there are far fewer cells than ticks.
        aggregates.ByCell = new Dictionary<(int, int), Dictionary<int, StackAggregate>>();
        aggregates.ByType = new Dictionary<int, StackAggregate>[topTypesCount];

        Span<AllocationEvent> allocationEventsSpan = CollectionsMarshal.AsSpan(allocationEvents);
        for (int eventIndex = 0; eventIndex < allocationEventsSpan.Length; ++eventIndex)
        {
            ref readonly AllocationEvent allocationEvent = ref allocationEventsSpan[eventIndex];
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
    // Also writes the cell's true totalBytes/totalTickCount/distinctStackCount
    // (summed over every distinct stack, before the cap is applied) - a cell
    // with more than DrillDownStacksPerCellLimit distinct call stacks would
    // otherwise have no way for a consumer to recover its real total (the
    // one the chart bar was actually drawn from) from the capped list alone,
    // which previously made the drill-down view's own displayed percentages
    // silently disagree with the bar they were opened from.
    private static void WriteCellDrillDown(Utf8JsonWriter writer, Dictionary<(int TypeIndex, int BucketIndex), Dictionary<int, StackAggregate>> stacksByCell, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("cells");
        writer.WriteStartObject();

        foreach (KeyValuePair<(int TypeIndex, int BucketIndex), Dictionary<int, StackAggregate>> cellEntry in stacksByCell)
        {
            List<StackAggregate> cellStackList = new List<StackAggregate>(cellEntry.Value.Values);
            cellStackList.Sort((left, right) => right.TotalBytes.CompareTo(left.TotalBytes));

            long cellTotalBytes = 0;
            int cellTotalTickCount = 0;
            for (int stackIndex = 0; stackIndex < cellStackList.Count; ++stackIndex)
            {
                cellTotalBytes += cellStackList[stackIndex].TotalBytes;
                cellTotalTickCount += cellStackList[stackIndex].TickCount;
            }

            int stackCount = cellStackList.Count < DrillDownStacksPerCellLimit ? cellStackList.Count : DrillDownStacksPerCellLimit;

            writer.WritePropertyName($"{cellEntry.Key.TypeIndex}:{cellEntry.Key.BucketIndex}");
            writer.WriteStartObject();
            writer.WriteNumber("totalBytes", cellTotalBytes);
            writer.WriteNumber("totalTickCount", cellTotalTickCount);
            writer.WriteNumber("distinctStackCount", cellStackList.Count);
            writer.WritePropertyName("stacks");
            writer.WriteStartArray();
            for (int stackIndex = 0; stackIndex < stackCount; ++stackIndex)
            {
                WriteStackAggregate(writer, cellStackList[stackIndex], stacksById, symbolTable);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
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
    // just whichever one chart segment happened to be clicked. Also writes
    // the type's true totalBytes/totalTickCount/distinctStackCount (summed
    // before the cap is applied) for the same reason WriteCellDrillDown
    // does - a type with more distinct call stacks than the cap needs a way
    // to recover its real (topTypes-matching) total from something other
    // than summing the possibly-truncated stacks array.
    private static void WriteTypeDrillDown(Utf8JsonWriter writer, Dictionary<int, StackAggregate>[] stacksByType, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
        writer.WriteStartArray();

        for (int typeIndex = 0; typeIndex < stacksByType.Length; ++typeIndex)
        {
            Dictionary<int, StackAggregate> typeStacks = stacksByType[typeIndex];

            writer.WriteStartObject();

            if (typeStacks != null)
            {
                List<StackAggregate> stackList = new List<StackAggregate>(typeStacks.Values);
                stackList.Sort((left, right) => right.TotalBytes.CompareTo(left.TotalBytes));

                long typeTotalBytes = 0;
                int typeTotalTickCount = 0;
                for (int stackIndex = 0; stackIndex < stackList.Count; ++stackIndex)
                {
                    typeTotalBytes += stackList[stackIndex].TotalBytes;
                    typeTotalTickCount += stackList[stackIndex].TickCount;
                }

                writer.WriteNumber("totalBytes", typeTotalBytes);
                writer.WriteNumber("totalTickCount", typeTotalTickCount);
                writer.WriteNumber("distinctStackCount", stackList.Count);

                int stackCount = stackList.Count < DrillDownStacksPerTypeLimit ? stackList.Count : DrillDownStacksPerTypeLimit;
                writer.WritePropertyName("stacks");
                writer.WriteStartArray();
                for (int stackIndex = 0; stackIndex < stackCount; ++stackIndex)
                {
                    WriteStackAggregate(writer, stackList[stackIndex], stacksById, symbolTable);
                }
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteNumber("totalBytes", 0);
                writer.WriteNumber("totalTickCount", 0);
                writer.WriteNumber("distinctStackCount", 0);
                writer.WritePropertyName("stacks");
                writer.WriteStartArray();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static int CompareByTotalBytesDescending(TypeAllocStats left, TypeAllocStats right)
    {
        return right.TotalBytes.CompareTo(left.TotalBytes);
    }

    // One tick record: 4 bytes RelativeMSec (int32, rounded milliseconds)
    // + 8 bytes AllocationAmount (int64), little-endian.
    private const int TickRecordSize = 12;

    // Buffered in TickWriteBufferRecordCapacity-record chunks rather than
    // one small write per tick - same "few large writes beat many small
    // ones" reasoning as NettraceFile.Read's 16MB read buffer.
    private const int TickWriteBufferRecordCapacity = 65536;

    // allocationEvents is already sorted by RelativeMSec ascending (Write
    // sorts it in place up front - matches GcJsonExporter.cs's own
    // heap-sort-before-serializing precedent of not trusting wire order to
    // already be time-ordered) - no separate copy+sort needed here.
    //
    // The ticks array (11.9M+ entries on a real 5-minute capture) is
    // written to a binary sidecar file instead of as JSON text. Measured
    // (dotnet-trace, this same real capture) as the largest remaining cost
    // in JSON export even after every other tuning pass: ~800ms sorting
    // (unavoidable - needed for chronological output in any format) plus,
    // specific to JSON text, ~900ms of Grisu3 floating-point formatting for
    // RelativeMSec and ~300ms of integer-to-decimal-text formatting for
    // AllocationAmount, on top of ~4x more bytes actually written/read
    // to/from disk than a packed binary representation needs (a JSON tick
    // object like {"RelativeMSec":123456,"AllocationAmount":78901} is ~48
    // text bytes vs. this format's fixed 12 bytes/record). RelativeMSec is
    // rounded to the nearest whole millisecond - every consumer
    // (allocationStats.js's rate chart, AllocationTicksBucketer.ts, the raw
    // scatter view) only ever floors/buckets this value at
    // millisecond-or-coarser granularity, so sub-millisecond precision was
    // never observable even before this format change.
    //
    // The JSON output gets only a small descriptor object here (format tag
    // + record count/size for the reader to size its buffer up front) -
    // not the sidecar's path, which is derived by the caller from the same
    // convention it used to name ticksBinaryPath in the first place (see
    // Program.cs) rather than round-tripped through the JSON.
    private static void WriteTicks(Utf8JsonWriter writer, List<AllocationEvent> allocationEvents, string ticksBinaryPath)
    {
        byte[] buffer = new byte[TickRecordSize * TickWriteBufferRecordCapacity];
        int bufferOffset = 0;

        using (FileStream fileStream = File.Create(ticksBinaryPath))
        {
            Span<AllocationEvent> allocationEventsSpan = CollectionsMarshal.AsSpan(allocationEvents);
            for (int eventIndex = 0; eventIndex < allocationEventsSpan.Length; ++eventIndex)
            {
                ref readonly AllocationEvent allocationEvent = ref allocationEventsSpan[eventIndex];

                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(bufferOffset, 4), (int)Math.Round(allocationEvent.RelativeMSec));
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(bufferOffset + 4, 8), allocationEvent.AllocationAmount);
                bufferOffset += TickRecordSize;

                if (bufferOffset == buffer.Length)
                {
                    fileStream.Write(buffer, 0, bufferOffset);
                    bufferOffset = 0;
                }
            }

            if (bufferOffset > 0)
            {
                fileStream.Write(buffer, 0, bufferOffset);
            }
        }

        writer.WriteStartObject();
        writer.WriteString("format", "binary-v1");
        writer.WriteNumber("recordCount", allocationEvents.Count);
        writer.WriteNumber("bytesPerRecord", TickRecordSize);
        writer.WriteEndObject();
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
