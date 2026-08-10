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
// The same topTypes/typeTimeline/drillDown/typeDrillDown shape is written
// twice (see WriteTypeBreakdown, shared by both) - once for every tick
// (top level) and once more, nested under "loh", scoped to only
// AllocationKind.Large ticks (the runtime sets this per-tick at allocation
// time whenever that allocation went to the LOH - no new capture needed).
// Both calls share one bucketCount so the two views use the same time axis
// - the webview's LOH-only filter toggle just points its existing
// rendering at allocationSummary.loh instead of allocationSummary itself,
// with no new rendering code needed for either the chart or its drill-down.
//
// Every stack's "frames" (in both drillDown and typeDrillDown, in both the
// top-level and "loh" scopes) is an array of integer indices into a single
// shared allocationSummary.methodNames array (see MethodNameInterner), not
// raw resolved-name strings - a consumer resolves a frame via
// methodNames[frameIndex]. The same call paths recur across many distinct
// (type, time-bucket) cells in a real capture, and writing the full string
// every time was measured on a real ~10-minute production capture as
// ballooning the JSON to 1.5GB (past Node's V8 string-length limit) despite
// the ticks array already living in a separate binary sidecar (see
// WriteTicks) - interning cut that same capture's stack data by roughly two
// orders of magnitude.
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

using DotnetInsights.NetTrace.Progress;
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
    //
    // Applies to every node in the call-stack tree (see BuildCallerTree/
    // WriteCallerTreeChildren), not just the top level - a node with more
    // than this many distinct children only writes its top
    // DrillDownTreeChildrenLimit by bytes, though its own totalBytes/
    // tickCount/distinctStackCount (and totalChildCount) always reflect
    // every real child, capped or not. One shared cap for both drillDown
    // (per chart cell) and typeDrillDown (whole capture).
    //
    // This alone does NOT bound total output size - an uncapped-*depth*
    // tree with only this per-node breadth cap can still blow up
    // combinatorially for deep, branchy real call stacks (async state
    // machines/closures commonly stay nominally distinct much deeper than
    // framework code eventually converges): confirmed on a real capture,
    // applying only this cap at every level with no further bound produced
    // an 800MB export, up from ~15MB before this per-node cap existed at
    // all. See DrillDownTreeNodeBudgetPerScope/MarkIncludedNodes for the
    // actual size bound - this constant only controls breadth *within* a
    // node the budget already allowed to expand.
    private const int DrillDownTreeChildrenLimit = 50;

    // Bounds how many tree nodes a single scope is allowed to write at all
    // (across the whole tree, not per-node - see MarkIncludedNodes), chosen
    // by a global best-first (biggest TotalBytes first) traversal starting
    // at the root. This is the actual output-size bound
    // (DrillDownTreeChildrenLimit above only shapes breadth within whatever
    // this budget already let through). Every node's own totalBytes/
    // tickCount/distinctStackCount/totalChildCount is always the TRUE
    // aggregate regardless of whether this budget let it be written -
    // only how much of the tree below an already low-priority (small
    // and/or deep) node gets shown is affected, which is exactly the
    // least useful part of the tree to spend the budget on anyway.
    // Confirmed against a real capture that this still preserves the fix
    // this whole tree redesign was for: the highest-byte branches (like a
    // diffuse System.Uri-adjacent allocator) are exactly the ones a
    // byte-priority budget writes first.
    //
    // Two different budgets, not one shared constant - a single (type,
    // 1-second bucket) cell (WriteCellDrillDown) covers a far narrower
    // slice of data than a type's *whole-capture* tree (WriteTypeDrillDown)
    // naturally accumulates, and a real capture can have thousands of
    // cells (chart types x buckets) vs. only ever up to TopTypesLimit(100)
    // typeDrillDown entries - giving every cell the same generous budget
    // as a whole type was the actual root cause of a real regression this
    // tree redesign introduced: 2,408 cells x a 2,000-node budget each
    // produced a 438MB export (97% of it from cells alone) on a real
    // capture, even though per-node breadth was already capped
    // (DrillDownTreeChildrenLimit) and per-node totals were already
    // correct - the budget just wasn't scoped to how much real detail a
    // single narrow cell actually needs.
    private const int DrillDownTreeNodeBudgetPerCell = 60;
    private const int DrillDownTreeNodeBudgetPerType = 2000;

    // Shared sentinel for BuildCallerTree/WriteStackAggregate's "this tick
    // wasn't stack-walked" case - a single source of truth so both agree on
    // the exact string (folding needs it as a real dictionary key, not just
    // a display string).
    private const string NoStackLeafName = "<no stack captured>";

    // Deduplicates resolved method-name strings across every stack this
    // exporter writes (both the "all" and "loh" scopes share one instance -
    // see Write) into a single shared pool, referenced from each stack's
    // "frames" array by integer index instead of repeating the string.
    // Real captures reuse the same handful of hot call paths (framework
    // internals, common allocation sites) across thousands of distinct
    // (type, time-bucket) drill-down cells - without this, a long capture's
    // JSON output scales with total drill-down *stack entries* (cells x
    // per-cell cap), not distinct call paths, and was measured on a real
    // ~10-minute production capture (523 GCs, ~190k stack entries after
    // capping) at 1.5GB - past Node's ~537M-character string limit, which
    // is what made the file unreadable in the VS Code extension even though
    // nettraceParser itself parsed and exported it without error.
    private class MethodNameInterner
    {
        private readonly Dictionary<string, int> indexByName = new Dictionary<string, int>();
        public readonly List<string> NamesInOrder = new List<string>();

        public int Intern(string name)
        {
            int index;
            if (!indexByName.TryGetValue(name, out index))
            {
                index = NamesInOrder.Count;
                indexByName[name] = index;
                NamesInOrder.Add(name);
            }

            return index;
        }
    }

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
    // Local split of THIS method's own onProgress fraction across its own
    // three real full-list passes (WriteTicks, WriteTypeBreakdown for "all"
    // events, WriteTypeBreakdown for "loh" events - FilterByAllocationKind's
    // own scan in between is cheap enough per item, relative to
    // WriteTypeBreakdown's grouping/stack-resolution work, to leave
    // unattributed) - same "callee doesn't need to know about the caller's
    // global weighting" contract as every other onProgress parameter in
    // this codebase (see NettraceFile.Read's own comment), just applied one
    // level deeper here since this method calls WriteTypeBreakdown twice
    // against differently-sized inputs.
    private const double TicksProgressFractionEnd = 0.3;

    // onProgress: THIS METHOD's own 0.0-1.0 completion fraction - null (the
    // default) for every caller except GcJsonExporter.WriteToFile's --json
    // mode dispatch (see that method's own comment on why this file is one
    // of only two JSON sub-writers with internal fine-grained tracking).
    public static void Write(Utf8JsonWriter writer, List<AllocationEvent> allocationEvents, MethodSymbolTable symbolTable, string ticksBinaryPath, Action<double> onProgress = null)
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

        // Shared by both the "all" and "loh" breakdowns below so the two
        // views share one time axis - toggling the webview's LOH-only
        // filter swaps which JSON object it reads from without the chart's
        // x-axis resizing/shifting, since bucket count/width stay fixed.
        int bucketCount = allocationEvents.Count == 0 ? 0 : ComputeBucketIndex(allocationEvents[allocationEvents.Count - 1].RelativeMSec, int.MaxValue) + 1;

        // Shared by both the "all" and "loh" breakdowns below (see
        // MethodNameInterner) - a single pool, referenced by
        // allocationSummary.methodNames, backs every stack's "frames" array
        // in both scopes.
        MethodNameInterner methodNameInterner = new MethodNameInterner();

        // Shared by every BuildCallerTree call this whole Write invocation
        // makes (drillDown AND typeDrillDown, "all" AND "loh") - see
        // BuildCallerTree's own comment on why resolving the same distinct
        // raw stack's frames more than once is pure waste: a given
        // AllocationEvent.Stack array reference commonly feeds BOTH its
        // (type, bucket) cell's tree AND its type's whole-capture tree, and
        // every "loh" stack is by construction also an "all" stack (loh is
        // just a Large-kind filter over the same events) - without this
        // cache, MethodSymbolTable.ResolveId (and its own internal
        // address cache lookup) was measured (dotnet-trace, a real
        // capture) running up to 4x on the same physical stack.
        Dictionary<long[], int[]> frameIdCache = new Dictionary<long[], int[]>(ReferenceEqualityComparer.Instance);

        // Shared by every BuildCallerTree call this whole Write invocation
        // makes, the same way frameIdCache is - see DrillDownTreeNodePool's
        // own comment for why reusing node objects across every scope's
        // tree (not just within one) is safe and worthwhile: measured
        // (dotnet-trace's gc-verbose profile, run against nettraceParser's
        // own process on a real capture) at 435MB across 4,289 allocation-
        // tick samples before this pool existed - the third-largest
        // allocator in the whole process.
        DrillDownTreeNodePool nodePool = new DrillDownTreeNodePool();
        ChildBufferPool bufferPool = new ChildBufferPool();

        // Computed here (moved ahead of WriteTicks/WriteTypeBreakdown below,
        // rather than just before its own "loh" use as before) purely so
        // onProgress's own local range split between the "all" and "loh"
        // WriteTypeBreakdown calls can be weighted by their REAL relative
        // sizes for this run, the same "use this run's own real counts"
        // reasoning Progress/ProgressPlan.cs's own jsonExport sub-writer
        // split uses one level up - filtering is pure/order-independent
        // (see this list's own original comment on why it needs no sort of
        // its own), so computing it earlier changes nothing about output.
        List<AllocationEvent> lohEvents = FilterByAllocationKind(allocationEvents, GCAllocationKind.Large);

        double allBreakdownFractionEnd;
        if (onProgress != null)
        {
            double breakdownTotal = allocationEvents.Count + lohEvents.Count;
            double allShare = breakdownTotal > 0.0 ? (allocationEvents.Count / breakdownTotal) : 1.0;
            allBreakdownFractionEnd = TicksProgressFractionEnd + (allShare * (1.0 - TicksProgressFractionEnd));
        }
        else
        {
            allBreakdownFractionEnd = 1.0;
        }

        writer.WriteStartObject();

        writer.WritePropertyName("ticks");
        WriteTicks(writer, allocationEvents, ticksBinaryPath, ScaleProgress(onProgress, 0.0, TicksProgressFractionEnd));

        WriteTypeBreakdown(writer, allocationEvents, bucketCount, symbolTable, methodNameInterner, frameIdCache, nodePool, bufferPool, ScaleProgress(onProgress, TicksProgressFractionEnd, allBreakdownFractionEnd));

        // Identical breakdown (same totalSampledBytes/topTypes/typeTimeline/
        // drillDown/typeDrillDown field names and shapes as above), scoped
        // to Large-kind (LOH) ticks only - GCAllocationTick's own
        // AllocationKind field is set by the runtime at allocation time, so
        // this needs no new capture/instrumentation, just filtering
        // AllocationEvents already decoded above. Nested under "loh" rather
        // than replacing the top-level fields so the webview can reuse its
        // existing typeTimeline/drillDown/typeDrillDown rendering
        // completely unchanged - a filter toggle just points it at
        // allocationSummary.loh instead of allocationSummary itself. ticks
        // (the raw per-tick scatter, which carries no type/kind - see
        // WriteTicks) isn't duplicated here; the filter only applies to the
        // type-oriented views.
        writer.WritePropertyName("loh");
        writer.WriteStartObject();
        WriteTypeBreakdown(writer, lohEvents, bucketCount, symbolTable, methodNameInterner, frameIdCache, nodePool, bufferPool, ScaleProgress(onProgress, allBreakdownFractionEnd, 1.0));
        writer.WriteEndObject();

        // Written last since it's only fully populated once every stack in
        // both scopes above has been walked - JSON object key order carries
        // no meaning to any consumer here (this is machine-read, not
        // hand-skimmed), so there's no need to reserve space for it earlier.
        writer.WritePropertyName("methodNames");
        writer.WriteStartArray();
        for (int nameIndex = 0; nameIndex < methodNameInterner.NamesInOrder.Count; ++nameIndex)
        {
            writer.WriteStringValue(methodNameInterner.NamesInOrder[nameIndex]);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    // Wraps outer (this whole Write call's own 0.0-1.0 fraction) so a
    // callee reports ITS OWN local 0.0-1.0 without knowing it's actually
    // one of several passes sharing a smaller slice of that range - same
    // composition pattern used one level up between Program.cs/
    // GcJsonExporter.WriteToFile and this class's own onProgress
    // parameter. Returns null (not a null-op lambda) when outer itself is
    // null, so a callee's own `onProgress != null` mask-gate check stays
    // just as cheap as if this class had never been instrumented at all.
    private static Action<double> ScaleProgress(Action<double> outer, double start, double end)
    {
        if (outer == null)
        {
            return null;
        }

        return (double innerFraction) => outer(start + (innerFraction * (end - start)));
    }

    private static List<AllocationEvent> FilterByAllocationKind(List<AllocationEvent> allocationEvents, GCAllocationKind kind)
    {
        List<AllocationEvent> filtered = new List<AllocationEvent>();

        Span<AllocationEvent> allocationEventsSpan = CollectionsMarshal.AsSpan(allocationEvents);
        for (int eventIndex = 0; eventIndex < allocationEventsSpan.Length; ++eventIndex)
        {
            ref readonly AllocationEvent allocationEvent = ref allocationEventsSpan[eventIndex];
            if (allocationEvent.AllocationKind == kind)
            {
                filtered.Add(allocationEvent);
            }
        }

        return filtered;
    }

    // Writes totalSampledBytes/distinctTypeCount/totalTickCount/topTypes/
    // typeTimeline/drillDown/typeDrillDown directly into whichever object is
    // currently open on writer - shared by the top-level ("all events") and
    // "loh" (Large-kind only) sections in Write() so both get identical
    // field names/shapes and the webview needs no branching between them.
    // events must already be sorted ascending by RelativeMSec (true both for
    // the full list, sorted once in Write, and any filtered subset of it,
    // since filtering preserves relative order).
    private static void WriteTypeBreakdown(Utf8JsonWriter writer, List<AllocationEvent> events, int bucketCount, MethodSymbolTable symbolTable, MethodNameInterner methodNameInterner, Dictionary<long[], int[]> frameIdCache, DrillDownTreeNodePool nodePool, ChildBufferPool bufferPool, Action<double> onProgress = null)
    {
        Dictionary<string, TypeAllocStats> statsByType = new Dictionary<string, TypeAllocStats>();
        long totalSampledBytes = 0;

        Span<AllocationEvent> eventsSpan = CollectionsMarshal.AsSpan(events);
        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            if (onProgress != null && (eventIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress(eventIndex / (double)eventsSpan.Length);
            }

            ref readonly AllocationEvent allocationEvent = ref eventsSpan[eventIndex];
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

        writer.WriteNumber("totalSampledBytes", totalSampledBytes);
        writer.WriteNumber("distinctTypeCount", statsByType.Count);
        writer.WriteNumber("totalTickCount", events.Count);

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

        writer.WritePropertyName("typeTimeline");
        WriteTypeTimeline(writer, events, sortedStats, chartTypeCount, columnIndexByType, bucketCount);

        // Single pass over every tick builds both drill-down shapes at once
        // (see BuildDrillDownAggregates) - cheaper than two independent
        // O(totalTicks) passes now that there are two things to aggregate.
        DrillDownAggregates aggregates = BuildDrillDownAggregates(events, columnIndexByType, typeIndexByName, sortedStats, topTypesCount, bucketCount);

        // Every distinct stack frameIdCache will ever see for this whole
        // Write() call is already known at this point: BuildDrillDownAggregates
        // just partitioned every event's stack into exactly one
        // aggregates.ByType[i] dictionary, keyed by the same long[] reference
        // BuildCallerTree looks up below - "loh" (the other WriteTypeBreakdown
        // call sharing this same frameIdCache) only ever sees a subset of
        // those same references, so it contributes ~0 new entries. Summing
        // ByType's counts up front and reserving the space via
        // EnsureCapacity (which only grows, never shrinks, and is exact
        // rather than a guess) avoids the O(log n) series of table resizes
        // frameIdCache would otherwise pay for one entry at a time -
        // measured via dotnet-trace gc-verbose as a real, if modest,
        // contributor to total allocation volume (Entry[long[],int[]][]
        // resize churn).
        int distinctStackCountEstimate = 0;
        for (int typeIndex = 0; typeIndex < aggregates.ByType.Length; ++typeIndex)
        {
            Dictionary<long[], StackAggregate> typeStacks = aggregates.ByType[typeIndex];
            if (typeStacks != null)
            {
                distinctStackCountEstimate += typeStacks.Count;
            }
        }

        frameIdCache.EnsureCapacity(frameIdCache.Count + distinctStackCountEstimate);

        writer.WritePropertyName("drillDown");
        WriteCellDrillDown(writer, aggregates.ByCell, symbolTable, methodNameInterner, frameIdCache, nodePool, bufferPool);

        writer.WritePropertyName("typeDrillDown");
        WriteTypeDrillDown(writer, aggregates.ByType, symbolTable, methodNameInterner, frameIdCache, nodePool, bufferPool);
    }

    // Shared by WriteTypeTimeline and WriteDrillDown so both agree on
    // exactly which types get their own column vs. fall into "Other" -
    // single source of truth for that boundary, per this file's own header
    // comment.
    private static Dictionary<string, int> BuildColumnIndexByType(List<TypeAllocStats> sortedStats, int chartTypeCount)
    {
        Dictionary<string, int> columnIndexByType = new Dictionary<string, int>(chartTypeCount);

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
        public long[] Stack;
        public long TotalBytes;
        public int TickCount;
        // The first tick's own RelativeMSec that contributed to this
        // aggregate - used as a representative timestamp when resolving
        // this stack's frames (see MethodSymbolTable.Resolve), since a
        // StackAggregate can merge many ticks that share the exact same
        // Stack array reference at different real times. An approximation
        // (ticks sharing the identical stack in practice tend to occur
        // close together - the same hot call path re-executing), not a
        // per-tick-exact resolution.
        public double FirstSeenRelativeMSec;
    }

    private struct DrillDownAggregates
    {
        public Dictionary<(int TypeIndex, int BucketIndex), Dictionary<long[], StackAggregate>> ByCell;
        public Dictionary<long[], StackAggregate>[] ByType;
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
    // Both aggregations key stacks by AllocationEvent.Stack's own array
    // reference (ReferenceEqualityComparer, see below), NOT by the raw
    // StackId integer the wire format uses - that integer is recyclable
    // (NetTraceFormat_v5.md's StackBlock section describes a *bounded*
    // cache, so a later StackBlock can legitimately reuse an id an earlier,
    // evicted stack used), so grouping by it would silently merge ticks
    // that only coincidentally share a recycled number, not a real stack.
    // Reference equality is enough (not full content equality) because
    // EventBlock.cs resolves each event's Stack eagerly, at parse time,
    // against the one shared StacksById dictionary - two events that
    // legitimately share the same still-valid stack get back the exact
    // same array instance, and Array.Empty<long>() (the "no stack" value)
    // is itself a single cached instance shared by every no-stack tick, so
    // those still group together too.
    private static DrillDownAggregates BuildDrillDownAggregates(List<AllocationEvent> allocationEvents, Dictionary<string, int> columnIndexByType, Dictionary<string, int> typeIndexByName, List<TypeAllocStats> sortedStats, int topTypesCount, int bucketCount)
    {
        DrillDownAggregates aggregates = new DrillDownAggregates();
        // Keyed by (typeIndex, bucketIndex) as a value-type tuple, not the
        // formatted "typeIndex:bucketIndex" string used in the JSON output -
        // a string-interpolate-then-hash per tick was measured (dotnet-trace,
        // real 76k-tick capture) as a meaningful chunk of this method's cost.
        // The formatted string is built once per distinct cell instead, in
        // WriteCellDrillDown - there are far fewer cells than ticks.
        //
        // columnIndexByType.Count * bucketCount is an exact upper bound on
        // how many distinct (typeIndex, bucketIndex) cells can ever appear
        // (every chart-column type crossed with every time bucket) - not
        // every cell will actually get a tick, but reserving the full grid
        // up front avoids ByCell's own resize churn as new cells are
        // discovered one at a time below.
        aggregates.ByCell = new Dictionary<(int, int), Dictionary<long[], StackAggregate>>(columnIndexByType.Count * bucketCount);
        aggregates.ByType = new Dictionary<long[], StackAggregate>[topTypesCount];

        Span<AllocationEvent> allocationEventsSpan = CollectionsMarshal.AsSpan(allocationEvents);
        for (int eventIndex = 0; eventIndex < allocationEventsSpan.Length; ++eventIndex)
        {
            ref readonly AllocationEvent allocationEvent = ref allocationEventsSpan[eventIndex];
            string typeName = string.IsNullOrEmpty(allocationEvent.TypeName) ? "<unknown>" : allocationEvent.TypeName;
            long[] stackKey = allocationEvent.Stack;

            if (bucketCount > 0)
            {
                int chartTypeIndex;
                if (columnIndexByType.TryGetValue(typeName, out chartTypeIndex))
                {
                    int bucketIndex = ComputeBucketIndex(allocationEvent.RelativeMSec, bucketCount);
                    (int TypeIndex, int BucketIndex) cellKey = (chartTypeIndex, bucketIndex);

                    Dictionary<long[], StackAggregate> cellStacks;
                    if (!aggregates.ByCell.TryGetValue(cellKey, out cellStacks))
                    {
                        cellStacks = new Dictionary<long[], StackAggregate>(ReferenceEqualityComparer.Instance);
                        aggregates.ByCell[cellKey] = cellStacks;
                    }

                    AddToStackAggregate(cellStacks, stackKey, allocationEvent.AllocationAmount, allocationEvent.RelativeMSec);
                }
            }

            int globalTypeIndex;
            if (typeIndexByName.TryGetValue(typeName, out globalTypeIndex))
            {
                Dictionary<long[], StackAggregate> typeStacks = aggregates.ByType[globalTypeIndex];
                if (typeStacks == null)
                {
                    // sortedStats[globalTypeIndex].TickCount (already
                    // computed by WriteTypeBreakdown's own first pass over
                    // events, before this method ever runs) is an exact
                    // upper bound on this type's distinct-stack count - a
                    // StackAggregate can only be created once per distinct
                    // stack, and a type can never have more distinct stacks
                    // than it has ticks. Measured via dotnet-trace
                    // gc-verbose as the single largest reducible allocator
                    // in this whole export after DrillDownTreeNode's own
                    // pooling (Entry[long[],StackAggregate][] resize churn) -
                    // a real capture's single largest type had 140,444
                    // distinct stacks, meaning ~18 resize-and-copy cycles
                    // from an unsized start.
                    typeStacks = new Dictionary<long[], StackAggregate>(sortedStats[globalTypeIndex].TickCount, ReferenceEqualityComparer.Instance);
                    aggregates.ByType[globalTypeIndex] = typeStacks;
                }

                AddToStackAggregate(typeStacks, stackKey, allocationEvent.AllocationAmount, allocationEvent.RelativeMSec);
            }
        }

        return aggregates;
    }

    private static void AddToStackAggregate(Dictionary<long[], StackAggregate> stacks, long[] stackKey, long allocationAmount, double relativeMSec)
    {
        StackAggregate aggregate;
        if (!stacks.TryGetValue(stackKey, out aggregate))
        {
            aggregate = new StackAggregate();
            aggregate.Stack = stackKey;
            aggregate.FirstSeenRelativeMSec = relativeMSec;
            stacks[stackKey] = aggregate;
        }

        aggregate.TotalBytes += allocationAmount;
        ++aggregate.TickCount;
    }

    // One node in the folded call-stack tree - see BuildCallerTree. Children
    // keyed by resolved frame name, so any two raw stacks sharing a
    // leaf-first common prefix merge into the same chain of nodes instead
    // of remaining separate; where they genuinely diverge, the tree
    // actually branches. This is what fixes a real, confirmed bug in an
    // earlier version of this export: WriteCellDrillDown/WriteTypeDrillDown
    // used to rank and cap directly on *raw* per-distinct-full-stack
    // entries, and a real capture can have orders of magnitude more
    // distinct full stacks for one type than distinct actual allocation
    // sites (verified: 140,444 distinct full stacks for System.String, only
    // 130 distinct leaf frames) - many different deeper call paths (varying
    // request parameters, etc.) funnel through the same handful of real
    // allocators. An intermediate fix folded by leaf frame alone, picking
    // one "representative" raw stack per leaf to display - which fixed the
    // top-level ranking (no more real allocator invisible just because its
    // bytes were spread across many individually-small stacks) but
    // introduced a *different* real bug: picking only one representative
    // stack per leaf silently discarded every other real caller for that
    // leaf. Confirmed against a real capture: PerfView showed System.Uri as
    // a top caller of System.String.Ctor, invisible in that design because
    // the one kept representative stack for String.Ctor happened to go
    // through an entirely different caller. Building the real tree - fold
    // at *every* depth, not just the leaf - fixes both problems at once:
    // every real caller permutation is preserved (a node's Children
    // dictionary has one entry per distinct next frame actually observed,
    // not a single arbitrarily-chosen example), and ranking/capping
    // (WriteCallerTreeChildren) still only ever operates on real aggregated
    // groups, never individual raw stacks.
    // Children are stored via a small-map fast path, not a plain
    // Dictionary<int, DrillDownTreeNode> - a real call-stack tree's own
    // shape (see BuildCallerTree) means the overwhelming majority of nodes
    // have exactly one child (a straight, non-branching chain of caller
    // frames - most call stacks don't branch at every frame), and
    // allocating a full hash table (bucket + entry arrays) for that common
    // case was measured (dotnet-trace, a real capture) as this whole
    // export's single largest remaining cost after BuildCallerTree/
    // WriteCallerTreeChildren switched to int-keyed children (see that
    // change's own comment) - `Dictionary.Resize` alone, almost entirely
    // driven by huge numbers of near-empty dictionaries each growing from
    // their initial zero capacity. firstChild covers the 0-or-1-child case
    // with no dictionary at all; moreChildren is only allocated once a
    // second genuinely distinct child arrives, so real branch points (the
    // ones that actually need a hash table) still get one.
    private class DrillDownTreeNode
    {
        public long TotalBytes;
        public int TickCount;
        // How many distinct raw full stacks (see BuildDrillDownAggregates)
        // pass through this exact node - i.e. this node's own subtree size.
        // 1 for a node only ever reached by one real call stack. This is
        // the TRUE total, unaffected by DrillDownTreeChildrenLimit capping
        // which of this node's children actually get written.
        public int DistinctStackCount;
        // Set by MarkIncludedNodes - whether this exact node itself is
        // written to output at all (see WriteCallerTreeChildren, which
        // skips any child not marked Included). False for a node that
        // exists (and has a true, accurate TotalBytes/TickCount/
        // DistinctStackCount/ChildCount, reflected in its PARENT's totals
        // either way) but didn't make the global per-scope node budget.
        public bool Included;

        private bool hasFirstChild;
        private int firstChildFrameId;
        private DrillDownTreeNode firstChild;
        // Keyed by MethodSymbolTable.ResolveId's own frame id (or
        // NoStackFrameId), NOT the resolved name string - see this class's
        // own header comment on why an int key at all, and why this is
        // only allocated for a genuine second-plus child. The actual name
        // is only ever looked up once, per written node, in
        // WriteCallerTreeChildren.
        private Dictionary<int, DrillDownTreeNode> moreChildren;

        public int ChildCount
        {
            get { return (this.hasFirstChild ? 1 : 0) + (this.moreChildren != null ? this.moreChildren.Count : 0); }
        }

        public DrillDownTreeNode GetOrAddChild(int frameId, DrillDownTreeNodePool pool)
        {
            if (this.hasFirstChild && this.firstChildFrameId == frameId)
            {
                return this.firstChild;
            }

            if (!this.hasFirstChild)
            {
                this.hasFirstChild = true;
                this.firstChildFrameId = frameId;
                this.firstChild = pool.Rent();
                return this.firstChild;
            }

            if (this.moreChildren == null)
            {
                // Pre-sized rather than the parameterless constructor (0
                // capacity, forcing an immediate resize on the very first
                // insert) - a real branch point commonly has more than a
                // couple of children, and Dictionary<K,V>.Resize was
                // measured (dotnet-trace, a real capture) as this whole
                // export's single largest cost across the huge number of
                // real branch points a full call-stack tree has. Doesn't
                // eliminate resizing for a node that ends up with hundreds
                // of children (unknowable in advance, built up one raw
                // stack at a time) - just the first one or two, which is
                // where the volume is.
                this.moreChildren = new Dictionary<int, DrillDownTreeNode>(4);
            }

            DrillDownTreeNode child;
            if (!this.moreChildren.TryGetValue(frameId, out child))
            {
                child = pool.Rent();
                this.moreChildren[frameId] = child;
            }

            return child;
        }

        // Clears this node back to its just-constructed state so
        // DrillDownTreeNodePool can hand it out again for an unrelated
        // later tree - see that pool's own comment for why reusing node
        // objects across separate BuildCallerTree calls is safe (each
        // scope's tree is fully consumed - written to JSON - before the
        // next one is built). moreChildren, if this node ever became a
        // branch point, is Cleared rather than nulled out - reusing the
        // Dictionary object itself (not just the node) avoids paying for
        // another one of the exact resize costs this whole node design was
        // built to eliminate, the next time some other node in some other
        // tree needs one.
        public void Reset()
        {
            this.TotalBytes = 0;
            this.TickCount = 0;
            this.DistinctStackCount = 0;
            this.Included = false;
            this.hasFirstChild = false;
            this.firstChildFrameId = 0;
            this.firstChild = null;

            if (this.moreChildren != null)
            {
                this.moreChildren.Clear();
            }
        }

        // Fast path for the overwhelmingly common case (a straight,
        // non-branching chain of caller frames - most call stacks don't
        // branch at every frame): true only when this node has EXACTLY one
        // child, letting the caller skip allocating+sorting a List entirely
        // (a real cost at the volume EnqueueTopChildren/
        // WriteCallerTreeChildren run at - see this class's own header
        // comment) for a node where "sort by bytes" is a no-op anyway.
        public bool TryGetOnlyChild(out int frameId, out DrillDownTreeNode child)
        {
            if (this.hasFirstChild && this.moreChildren == null)
            {
                frameId = this.firstChildFrameId;
                child = this.firstChild;
                return true;
            }

            frameId = 0;
            child = null;
            return false;
        }

        // Appends every (frameId, child) pair into buffer - a plain List
        // fill rather than an allocating iterator/LINQ, since every call
        // site immediately sorts/caps the result anyway.
        public void CollectChildren(List<KeyValuePair<int, DrillDownTreeNode>> buffer)
        {
            if (this.hasFirstChild)
            {
                buffer.Add(new KeyValuePair<int, DrillDownTreeNode>(this.firstChildFrameId, this.firstChild));
            }

            if (this.moreChildren != null)
            {
                foreach (KeyValuePair<int, DrillDownTreeNode> pair in this.moreChildren)
                {
                    buffer.Add(pair);
                }
            }
        }
    }

    // Reuses DrillDownTreeNode objects across every BuildCallerTree call in
    // one Write() invocation, instead of a fresh `new DrillDownTreeNode()`
    // per distinct node - measured directly (dotnet-trace's gc-verbose
    // profile, allocation-tick sampled, run against nettraceParser's own
    // process on a real capture) at 435MB across 4,289 allocation-tick
    // samples, the third-largest allocator in the whole process behind
    // only the whole-file byte[] buffer and the decoded EventRecord[]
    // list - both much harder to avoid, since they're real, necessarily
    // long-lived data. DrillDownTreeNode instances are NOT: a single
    // scope's tree (one chart cell, or one whole type) is fully built,
    // marked, written to JSON, and then entirely discarded before the next
    // scope's tree is even started (WriteCellDrillDown/WriteTypeDrillDown
    // each loop scope-by-scope) - nothing outside that one loop iteration
    // ever holds a reference to a previous tree's nodes. That makes this
    // an ideal arena/pool shape: rent nodes from a flat, steadily-growing
    // list (never shrunk - the largest tree built during this run sets the
    // pool's permanent size) and Reset() them for reuse via a simple index
    // rewind, rather than actually freeing anything and letting the GC
    // reclaim + re-allocate the same shapes over and over.
    private class DrillDownTreeNodePool
    {
        private readonly List<DrillDownTreeNode> nodes = new List<DrillDownTreeNode>();
        private int nextIndex;

        public DrillDownTreeNode Rent()
        {
            DrillDownTreeNode node;
            if (this.nextIndex < this.nodes.Count)
            {
                node = this.nodes[this.nextIndex];
                node.Reset();
            }
            else
            {
                node = new DrillDownTreeNode();
                this.nodes.Add(node);
            }

            ++this.nextIndex;
            return node;
        }

        // Called once per scope, after its tree has been fully written to
        // JSON and is no longer referenced anywhere, before the next
        // scope's tree is built - rewinds the rental index back to the
        // start without touching the underlying node objects themselves
        // (Rent's own Reset() call clears each one lazily, only once it's
        // actually handed out again).
        public void ResetForNextTree()
        {
            this.nextIndex = 0;
        }
    }

    // Pools the transient List<KeyValuePair<int, DrillDownTreeNode>>
    // buffers MarkIncludedNodes/EnqueueTopChildren/WriteCallerTreeChildren
    // each build to sort a node's children before ranking/writing them -
    // measured via dotnet-trace gc-verbose as the next-largest reducible
    // allocator after DrillDownTreeNode itself (~45MB / ~450 allocation
    // ticks on a real capture). Unlike DrillDownTreeNodePool's flat
    // rent-and-reset arena, these buffers are needed by nested recursive
    // calls simultaneously - WriteChildObject recurses into
    // WriteCallerTreeChildren mid-loop over its own children list, so a
    // parent's buffer must stay alive and untouched while a child's own
    // buffer is rented - so this pool is a LIFO free list instead: Rent
    // pops (or allocates fresh), Return pushes back once a call is fully
    // done with its buffer, naturally mirroring the call stack's own
    // push/pop order.
    private class ChildBufferPool
    {
        private readonly List<List<KeyValuePair<int, DrillDownTreeNode>>> freeBuffers = new List<List<KeyValuePair<int, DrillDownTreeNode>>>();

        public List<KeyValuePair<int, DrillDownTreeNode>> Rent(int capacityHint)
        {
            if (this.freeBuffers.Count > 0)
            {
                List<KeyValuePair<int, DrillDownTreeNode>> buffer = this.freeBuffers[this.freeBuffers.Count - 1];
                this.freeBuffers.RemoveAt(this.freeBuffers.Count - 1);
                buffer.Clear();

                if (buffer.Capacity < capacityHint)
                {
                    buffer.Capacity = capacityHint;
                }

                return buffer;
            }

            return new List<KeyValuePair<int, DrillDownTreeNode>>(capacityHint);
        }

        public void Return(List<KeyValuePair<int, DrillDownTreeNode>> buffer)
        {
            this.freeBuffers.Add(buffer);
        }
    }

    // Reserved frame id for the "<no stack captured>" sentinel - guaranteed
    // to never collide with a real MethodSymbolTable.ResolveId result
    // (always >= 0).
    private const int NoStackFrameId = -1;

    // Builds the full call-stack tree from every raw distinct StackAggregate
    // (see BuildDrillDownAggregates) for one scope (a chart cell or a
    // whole type) - not capped, not pre-selected; every real distinct
    // full stack contributes its entire frame chain. Returns a synthetic
    // root (never itself written to JSON - see WriteCallerTreeChildren)
    // whose children are every distinct LEAF (immediate allocating) frame,
    // each of whose own children are every distinct frame that called it,
    // and so on outward through the whole stack - the same "leaf-first"
    // order Stack itself already carries (see EventBlock.cs/StackBlock.cs).
    //
    // Resolves via ResolveId (an int), not Resolve (a string) - see
    // DrillDownTreeNode's own header comment for why; MethodSymbolTable's
    // own address+time resolution is unaffected either way, this only
    // changes what's used as the tree's own child keys.
    //
    // frameIdCache memoizes each distinct raw Stack array's own resolved
    // frame-id sequence (keyed by array reference - see AllocationEvent
    // Stack/StackAggregate's own comments on why reference equality is
    // enough) across every BuildCallerTree call sharing it - see Write's
    // own comment on why that matters: the same physical stack commonly
    // feeds both its cell's tree and its type's tree (two different
    // StackAggregate wrapper objects, but the same underlying Stack array
    // either way), and "loh" stacks are always a subset of "all" stacks,
    // so without this a real capture re-resolved the same stack's frames
    // up to 4x.
    private static DrillDownTreeNode BuildCallerTree(List<StackAggregate> rawStacks, MethodSymbolTable symbolTable, Dictionary<long[], int[]> frameIdCache, DrillDownTreeNodePool nodePool)
    {
        DrillDownTreeNode root = nodePool.Rent();

        for (int stackIndex = 0; stackIndex < rawStacks.Count; ++stackIndex)
        {
            StackAggregate rawStack = rawStacks[stackIndex];
            DrillDownTreeNode current = root;

            if (rawStack.Stack.Length == 0)
            {
                current = current.GetOrAddChild(NoStackFrameId, nodePool);
                AccumulateTreeNode(current, rawStack);
                continue;
            }

            int[] frameIds;
            if (!frameIdCache.TryGetValue(rawStack.Stack, out frameIds))
            {
                frameIds = new int[rawStack.Stack.Length];
                for (int frameIndex = 0; frameIndex < rawStack.Stack.Length; ++frameIndex)
                {
                    frameIds[frameIndex] = symbolTable.ResolveId(rawStack.Stack[frameIndex], rawStack.FirstSeenRelativeMSec);
                }

                frameIdCache[rawStack.Stack] = frameIds;
            }

            for (int frameIndex = 0; frameIndex < frameIds.Length; ++frameIndex)
            {
                current = current.GetOrAddChild(frameIds[frameIndex], nodePool);
                AccumulateTreeNode(current, rawStack);
            }
        }

        return root;
    }

    private static void AccumulateTreeNode(DrillDownTreeNode node, StackAggregate rawStack)
    {
        node.TotalBytes += rawStack.TotalBytes;
        node.TickCount += rawStack.TickCount;
        ++node.DistinctStackCount;
    }

    // Marks which nodes in root's tree are actually written to output at
    // all (node.Included, checked by WriteCallerTreeChildren against each
    // candidate child) - see DrillDownTreeNodeBudgetPerCell/PerType's own
    // comment for why a size bound is needed here at all (an uncapped-depth
    // tree with only a per-node breadth cap can still blow up
    // combinatorially).
    //
    // Root's own direct children - the scope's top-level leaf/allocator
    // frames - are always included unconditionally, up to the per-node
    // breadth cap (DrillDownTreeChildrenLimit), *before* budget spends
    // anything on deeper exploration. This is deliberate, not an
    // oversight: an earlier version of this function let root's own
    // children compete for budget in the same global priority queue as
    // every deeper node, and on a real capture that meant a single
    // dominant top-level allocator could consume the *entire* budget
    // drilling into its own caller chain, leaving smaller sibling
    // allocators completely unlisted - not just shallower, absent - even
    // though each is a real, distinct top-level entry that deserves at
    // least its own row (the same guarantee every earlier version of this
    // export made: every one of up to DrillDownTreeChildrenLimit distinct
    // top-level entries always showed up, even a small one).
    //
    // budget is spent only on *how much deeper* detail gets shown below
    // those guaranteed top-level rows - a max-heap of not-yet-included
    // deeper candidate nodes, keyed by TotalBytes: seed with every
    // guaranteed top-level node's own children, then repeatedly take the
    // single biggest candidate across the *entire* tree, mark it Included
    // (consuming one unit of budget), and add its own children as new
    // candidates. This is what makes the depth budget a global priority
    // order rather than a fixed per-node cap that can't tell "50 huge
    // branches 40 frames deep" from "50 tiny ones 2 frames deep" - the
    // budget always goes to whichever real node is biggest next, anywhere
    // in the tree, so the highest-byte branches (e.g. a diffuse allocator
    // like the SignUri/System.Uri case this tree redesign was for) are
    // exactly the ones guaranteed to survive a tight budget.
    private static void MarkIncludedNodes(DrillDownTreeNode root, int budget, ChildBufferPool bufferPool)
    {
        // budget is a lower-bound-ish hint, not an exact size - each
        // dequeue below can enqueue up to DrillDownTreeChildrenLimit new
        // candidates, so the queue can still grow past this at its peak,
        // but starting from budget instead of 0 avoids the earliest,
        // cheapest-to-avoid resize cycles for every one of the hundreds of
        // cells/types this runs once per, each of this whole export.
        PriorityQueue<DrillDownTreeNode, long> candidates = new PriorityQueue<DrillDownTreeNode, long>(budget);

        // Rented, not `new`'d - see ChildBufferPool's own comment. Returned
        // once this function is done reading from it (nothing else holds a
        // reference to it past this point).
        List<KeyValuePair<int, DrillDownTreeNode>> topLevelPairs = bufferPool.Rent(root.ChildCount);
        root.CollectChildren(topLevelPairs);
        topLevelPairs.Sort((left, right) => right.Value.TotalBytes.CompareTo(left.Value.TotalBytes));
        int topLevelCount = topLevelPairs.Count < DrillDownTreeChildrenLimit ? topLevelPairs.Count : DrillDownTreeChildrenLimit;

        for (int topLevelIndex = 0; topLevelIndex < topLevelCount; ++topLevelIndex)
        {
            DrillDownTreeNode node = topLevelPairs[topLevelIndex].Value;
            node.Included = true;
            EnqueueTopChildren(candidates, node, bufferPool);
        }

        bufferPool.Return(topLevelPairs);

        int remaining = budget;
        while (candidates.Count > 0 && remaining > 0)
        {
            DrillDownTreeNode node = candidates.Dequeue();
            node.Included = true;
            --remaining;

            EnqueueTopChildren(candidates, node, bufferPool);
        }
    }

    private static void EnqueueTopChildren(PriorityQueue<DrillDownTreeNode, long> candidates, DrillDownTreeNode node, ChildBufferPool bufferPool)
    {
        int onlyFrameId;
        DrillDownTreeNode onlyChild;
        if (node.TryGetOnlyChild(out onlyFrameId, out onlyChild))
        {
            candidates.Enqueue(onlyChild, -onlyChild.TotalBytes);
            return;
        }

        int totalChildCount = node.ChildCount;
        if (totalChildCount == 0)
        {
            return;
        }

        List<KeyValuePair<int, DrillDownTreeNode>> children = bufferPool.Rent(totalChildCount);
        node.CollectChildren(children);
        children.Sort((left, right) => right.Value.TotalBytes.CompareTo(left.Value.TotalBytes));

        int childCount = children.Count < DrillDownTreeChildrenLimit ? children.Count : DrillDownTreeChildrenLimit;
        for (int childIndex = 0; childIndex < childCount; ++childIndex)
        {
            DrillDownTreeNode child = children[childIndex].Value;
            candidates.Enqueue(child, -child.TotalBytes);
        }

        bufferPool.Return(children);
    }

    // Writes node's "children" array - shared by WriteCellDrillDown and
    // WriteTypeDrillDown, and by itself recursively, so a caller row's own
    // children (once expanded client-side) are represented identically to
    // a scope's top-level leaf rows. Each child is
    // { frame, totalBytes, tickCount, distinctStackCount, totalChildCount, children }:
    // frame is an integer index into the shared allocationSummary.methodNames
    // pool (see MethodNameInterner) rather than a raw string - the same
    // resolved name recurs across many distinct nodes (framework internals,
    // common allocation sites), and writing it out in full every time is
    // what let a long real capture's JSON balloon past Node's string-length
    // limit despite the ticks array already living in a separate binary
    // sidecar. Sorted by totalBytes descending and capped at both
    // DrillDownTreeChildrenLimit and, more importantly, node.Included (see
    // MarkIncludedNodes - not every node within the breadth cap necessarily
    // made the global per-scope node budget) - totalChildCount is the true
    // count before either restriction (children.Count), letting a consumer
    // tell whether a node's own children list was truncated the same way
    // distinctStackCount/totalBytes/tickCount already let it tell for the
    // node itself.
    private static void WriteCallerTreeChildren(Utf8JsonWriter writer, DrillDownTreeNode node, MethodSymbolTable symbolTable, MethodNameInterner methodNameInterner, ChildBufferPool bufferPool)
    {
        int totalChildCount = node.ChildCount;
        writer.WriteNumber("totalChildCount", totalChildCount);
        writer.WritePropertyName("children");
        writer.WriteStartArray();

        int onlyFrameId;
        DrillDownTreeNode onlyChild;
        if (node.TryGetOnlyChild(out onlyFrameId, out onlyChild))
        {
            // Fast path for the overwhelmingly common single-child case -
            // see DrillDownTreeNode.TryGetOnlyChild's own comment. No
            // List/Sort needed: there's nothing to rank against a sibling
            // that doesn't exist.
            if (onlyChild.Included)
            {
                WriteChildObject(writer, onlyFrameId, onlyChild, symbolTable, methodNameInterner, bufferPool);
            }
        }
        else if (totalChildCount > 0)
        {
            // Rented, not `new`'d - see ChildBufferPool's own comment. Held
            // across the recursive WriteChildObject calls below (each of
            // those rents its own, separate buffer for its own children),
            // then returned once this node's own loop is done with it.
            List<KeyValuePair<int, DrillDownTreeNode>> children = bufferPool.Rent(totalChildCount);
            node.CollectChildren(children);
            children.Sort((left, right) => right.Value.TotalBytes.CompareTo(left.Value.TotalBytes));

            int childCount = children.Count < DrillDownTreeChildrenLimit ? children.Count : DrillDownTreeChildrenLimit;
            for (int childIndex = 0; childIndex < childCount; ++childIndex)
            {
                int frameId = children[childIndex].Key;
                DrillDownTreeNode child = children[childIndex].Value;

                if (!child.Included)
                {
                    continue;
                }

                WriteChildObject(writer, frameId, child, symbolTable, methodNameInterner, bufferPool);
            }

            bufferPool.Return(children);
        }

        writer.WriteEndArray();
    }

    // One child's { frame, totalBytes, tickCount, distinctStackCount,
    // totalChildCount, children } object - shared by WriteCallerTreeChildren's
    // single-child fast path and its sorted-multi-child path so both agree
    // on exactly how a node is written.
    private static void WriteChildObject(Utf8JsonWriter writer, int frameId, DrillDownTreeNode child, MethodSymbolTable symbolTable, MethodNameInterner methodNameInterner, ChildBufferPool bufferPool)
    {
        // Resolved to a string here, once per WRITTEN node, not once per
        // (raw stack, frame) visit during tree building - see
        // DrillDownTreeNode's own header comment for why that distinction
        // is the whole point of keying by id.
        string frameName = frameId == NoStackFrameId ? NoStackLeafName : symbolTable.NameForId(frameId);

        writer.WriteStartObject();
        writer.WriteNumber("frame", methodNameInterner.Intern(frameName));
        writer.WriteNumber("totalBytes", child.TotalBytes);
        writer.WriteNumber("tickCount", child.TickCount);
        writer.WriteNumber("distinctStackCount", child.DistinctStackCount);
        WriteCallerTreeChildren(writer, child, symbolTable, methodNameInterner, bufferPool);
        writer.WriteEndObject();
    }

    // For each (typeIndex, bucketIndex) cell the stacked chart can be
    // clicked on, the full call-stack tree that produced that cell's
    // allocations (see BuildCallerTree/WriteCallerTreeChildren) - every
    // real distinct raw stack folds into it, so ranking/capping only ever
    // happens on real aggregated groups, never by picking among individual
    // raw stacks. Also writes the cell's true totalBytes/totalTickCount/
    // distinctStackCount (summed over every distinct raw stack, before any
    // per-node children cap is applied) - a cell with more distinct call
    // stacks than DrillDownTreeChildrenLimit would otherwise have no way
    // for a consumer to recover its real total (the one the chart bar was
    // actually drawn from) from the capped tree alone, which previously
    // made the drill-down view's own displayed percentages silently
    // disagree with the bar they were opened from.
    private static void WriteCellDrillDown(Utf8JsonWriter writer, Dictionary<(int TypeIndex, int BucketIndex), Dictionary<long[], StackAggregate>> stacksByCell, MethodSymbolTable symbolTable, MethodNameInterner methodNameInterner, Dictionary<long[], int[]> frameIdCache, DrillDownTreeNodePool nodePool, ChildBufferPool bufferPool)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("cells");
        writer.WriteStartObject();

        foreach (KeyValuePair<(int TypeIndex, int BucketIndex), Dictionary<long[], StackAggregate>> cellEntry in stacksByCell)
        {
            List<StackAggregate> cellStackList = new List<StackAggregate>(cellEntry.Value.Values);

            long cellTotalBytes = 0;
            int cellTotalTickCount = 0;
            for (int stackIndex = 0; stackIndex < cellStackList.Count; ++stackIndex)
            {
                cellTotalBytes += cellStackList[stackIndex].TotalBytes;
                cellTotalTickCount += cellStackList[stackIndex].TickCount;
            }

            DrillDownTreeNode tree = BuildCallerTree(cellStackList, symbolTable, frameIdCache, nodePool);
            MarkIncludedNodes(tree, DrillDownTreeNodeBudgetPerCell, bufferPool);

            writer.WritePropertyName($"{cellEntry.Key.TypeIndex}:{cellEntry.Key.BucketIndex}");
            writer.WriteStartObject();
            writer.WriteNumber("totalBytes", cellTotalBytes);
            writer.WriteNumber("totalTickCount", cellTotalTickCount);
            writer.WriteNumber("distinctStackCount", cellStackList.Count);
            WriteCallerTreeChildren(writer, tree, symbolTable, methodNameInterner, bufferPool);
            writer.WriteEndObject();

            // tree is fully written to JSON above and nothing else will
            // ever reference it - safe to hand every node in it back to
            // the pool for the next cell's tree (see
            // DrillDownTreeNodePool's own comment).
            nodePool.ResetForNextTree();

            // Utf8JsonWriter never auto-flushes on its own - without this,
            // its internal ArrayBufferWriter<byte> has to keep doubling
            // (Array.Resize) all the way up to this whole export's entire
            // output size (37MB+ on a real capture) before a single byte
            // reaches disk, since the writer's own Dispose() is the only
            // other place a flush would happen. Confirmed via dotnet-trace
            // gc-verbose as a real, if secondary, allocator (~140MB of
            // discarded intermediate buffers from that doubling series).
            // Flushing once per cell bounds the writer's own buffer to
            // roughly one cell's worth of JSON instead, which
            // FileStream's own buffer (see GcJsonExporter.WriteToFile)
            // then absorbs without a syscall per flush.
            writer.Flush();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // One entry per ranked type in topTypes above (same order - typeIndex i
    // here corresponds to topTypes[i]), each the full call-stack tree that
    // allocated that type *anywhere in the whole capture* (see
    // BuildCallerTree/WriteCallerTreeChildren) - unlike "drillDown" above,
    // not scoped to a single 1-second bucket. Lets the global ranked types
    // table link a type directly to its full allocating call-stack tree,
    // not just whichever one chart segment happened to be clicked. Also
    // writes the type's true totalBytes/totalTickCount/distinctStackCount
    // (summed before any per-node children cap is applied) for the same
    // reason WriteCellDrillDown does - a type with more distinct call
    // stacks than the cap needs a way to recover its real (topTypes-
    // matching) total from something other than summing the possibly-
    // truncated tree.
    private static void WriteTypeDrillDown(Utf8JsonWriter writer, Dictionary<long[], StackAggregate>[] stacksByType, MethodSymbolTable symbolTable, MethodNameInterner methodNameInterner, Dictionary<long[], int[]> frameIdCache, DrillDownTreeNodePool nodePool, ChildBufferPool bufferPool)
    {
        writer.WriteStartArray();

        for (int typeIndex = 0; typeIndex < stacksByType.Length; ++typeIndex)
        {
            Dictionary<long[], StackAggregate> typeStacks = stacksByType[typeIndex];

            writer.WriteStartObject();

            if (typeStacks != null)
            {
                List<StackAggregate> stackList = new List<StackAggregate>(typeStacks.Values);

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

                DrillDownTreeNode tree = BuildCallerTree(stackList, symbolTable, frameIdCache, nodePool);
                MarkIncludedNodes(tree, DrillDownTreeNodeBudgetPerType, bufferPool);
                WriteCallerTreeChildren(writer, tree, symbolTable, methodNameInterner, bufferPool);

                // tree is fully written to JSON above and nothing else
                // will ever reference it - safe to hand every node in it
                // back to the pool for the next type's tree.
                nodePool.ResetForNextTree();
            }
            else
            {
                writer.WriteNumber("totalBytes", 0);
                writer.WriteNumber("totalTickCount", 0);
                writer.WriteNumber("distinctStackCount", 0);
                writer.WriteNumber("totalChildCount", 0);
                writer.WritePropertyName("children");
                writer.WriteStartArray();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();

            // See WriteCellDrillDown's own comment on why this is needed -
            // same reasoning, per type here instead of per cell.
            writer.Flush();
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
    private static void WriteTicks(Utf8JsonWriter writer, List<AllocationEvent> allocationEvents, string ticksBinaryPath, Action<double> onProgress = null)
    {
        byte[] buffer = new byte[TickRecordSize * TickWriteBufferRecordCapacity];
        int bufferOffset = 0;

        using (FileStream fileStream = File.Create(ticksBinaryPath))
        {
            Span<AllocationEvent> allocationEventsSpan = CollectionsMarshal.AsSpan(allocationEvents);
            for (int eventIndex = 0; eventIndex < allocationEventsSpan.Length; ++eventIndex)
            {
                if (onProgress != null && (eventIndex & ProgressReporter.IndexProgressMask) == 0)
                {
                    onProgress(eventIndex / (double)allocationEventsSpan.Length);
                }

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
