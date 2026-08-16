////////////////////////////////////////////////////////////////////////////////
// Module: ExceptionJsonExporter.cs
//
// Notes:
// Serializes a List<ExceptionEvent> into the "exceptionSummary" shape the
// VS Code extension's ExceptionSummaryRenderer.ts consumes: totalExceptionCount/
// distinctTypeCount, a topTypes ranking (by throw count, not bytes), and a
// per-type folded caller-stack tree (typeDrillDown) so the ranked-types
// table can link a type directly to its throw-site stacks - the exception
// analog of Gc/AllocationJsonExporter.cs's topTypes/typeDrillDown.
//
// Deliberately a much simpler, non-pooled version of that file's caller-
// tree algorithm (fold by resolved frame id at every depth - see
// BuildCallerTree's own comment): AllocationJsonExporter's
// DrillDownTreeNodePool/ChildBufferPool/priority-queue node budget exist to
// amortize real allocation-tick volume (millions of ticks per capture).
// Exception volume is orders of magnitude smaller even in a pathological
// exceptions-as-control-flow capture, so plain `new`'d nodes and a simple
// running write-budget counter (WriteBudget, capping total nodes written
// per type rather than doing a global biggest-first priority order) are
// enough to keep output bounded without that machinery's complexity.
//
// Writes directly into a Utf8JsonWriter (never builds a JsonNode tree),
// per CLAUDE.md's JSON-serialization rule and this codebase's established
// precedent (see AllocationJsonExporter.cs's own header comment).
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Exceptions {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ExceptionJsonExporter
{
    private const int TopTypesLimit = 50;
    private const int DrillDownTreeChildrenLimit = 12;

    // Same cap as CpuProfileJsonExporter.WriteTimeline/ContentionJsonExporter's
    // own timeline - bounds a huge capture's timeline to a fixed number of
    // buckets while still giving small captures one bucket per exception
    // rather than mostly-empty ones.
    private const int MaxTimelineBuckets = 100;

    // Per-type total nodes written to typeDrillDown, across the whole
    // recursive tree - bounds output size for a pathological single type
    // with a huge, highly-branching set of distinct throw-site stacks
    // (see this file's own header comment on why this is a much simpler
    // stand-in for AllocationJsonExporter's MarkIncludedNodes budget).
    private const int DrillDownTreeNodeBudgetPerType = 300;

    // Reserved frame id for the "<no stack captured>" sentinel - guaranteed
    // to never collide with a real MethodSymbolTable.ResolveId result
    // (always >= 0), same convention as AllocationJsonExporter.cs.
    private const int NoStackFrameId = -1;
    private const string NoStackLeafName = "<no stack captured>";

    private sealed class TypeExceptionStats
    {
        public string TypeName;
        public int Count;
        public string SampleMessage;
    }

    // One distinct raw throw-site Stack array reference for one exception
    // type, with how many exceptions of that type shared it - same
    // reference-equality-is-enough reasoning as AllocationJsonExporter.cs's
    // StackAggregate (EventBlock.cs resolves each event's Stack eagerly
    // against one shared StacksById dictionary, so two throws that
    // legitimately share a still-valid stack get back the exact same array
    // instance).
    private sealed class ExceptionStackAggregate
    {
        public int StackIndex;
        public int Count;
        public double FirstSeenRelativeMSec;
    }

    // One node in the folded call-stack tree, keyed by resolved frame id at
    // every depth (not just the leaf) - see AllocationJsonExporter.cs's
    // DrillDownTreeNode header comment for why folding at every depth
    // (rather than only by leaf frame) matters: it preserves every real
    // caller permutation instead of silently discarding all but one
    // representative stack per leaf.
    private sealed class ExceptionTreeNode
    {
        public int Count;
        public int DistinctStackCount;
        public Dictionary<int, ExceptionTreeNode> Children = new Dictionary<int, ExceptionTreeNode>();
    }

    private sealed class WriteBudget
    {
        public int Remaining;
    }

    public static void Write(Utf8JsonWriter writer, List<ExceptionEvent> exceptionEvents, StackTable stackTable, MethodSymbolTable symbolTable)
    {
        writer.WriteStartObject();

        writer.WriteNumber("totalExceptionCount", exceptionEvents.Count);

        if (exceptionEvents.Count == 0)
        {
            writer.WriteNumber("distinctTypeCount", 0);
            writer.WritePropertyName("topTypes");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName("typeDrillDown");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName("timeline");
            writer.WriteNullValue();
            writer.WritePropertyName("methodNames");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }

        Span<ExceptionEvent> eventsSpan = CollectionsMarshal.AsSpan(exceptionEvents);

        // Same pass also tracks the overall time range for timeline
        // bucketing (mirrors ContentionJsonExporter.Write's own pass-1) -
        // no separate scan needed since this one already visits every event.
        double minRelativeMSec = double.MaxValue;
        double maxRelativeMSec = double.MinValue;

        Dictionary<string, TypeExceptionStats> statsByType = new Dictionary<string, TypeExceptionStats>();
        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly ExceptionEvent exceptionEvent = ref eventsSpan[eventIndex];
            string typeName = string.IsNullOrEmpty(exceptionEvent.ExceptionType) ? "<unknown>" : exceptionEvent.ExceptionType;

            TypeExceptionStats stats;
            if (!statsByType.TryGetValue(typeName, out stats))
            {
                stats = new TypeExceptionStats();
                stats.TypeName = typeName;
                stats.SampleMessage = exceptionEvent.ExceptionMessage;
                statsByType[typeName] = stats;
            }

            ++stats.Count;

            if (exceptionEvent.RelativeMSec < minRelativeMSec)
            {
                minRelativeMSec = exceptionEvent.RelativeMSec;
            }

            if (exceptionEvent.RelativeMSec > maxRelativeMSec)
            {
                maxRelativeMSec = exceptionEvent.RelativeMSec;
            }
        }

        List<TypeExceptionStats> sortedStats = new List<TypeExceptionStats>(statsByType.Values);
        sortedStats.Sort((left, right) => right.Count.CompareTo(left.Count));

        int topTypesCount = sortedStats.Count < TopTypesLimit ? sortedStats.Count : TopTypesLimit;
        Dictionary<string, int> typeIndexByName = new Dictionary<string, int>(topTypesCount);
        for (int typeIndex = 0; typeIndex < topTypesCount; ++typeIndex)
        {
            typeIndexByName[sortedStats[typeIndex].TypeName] = typeIndex;
        }

        writer.WriteNumber("distinctTypeCount", statsByType.Count);

        writer.WritePropertyName("topTypes");
        writer.WriteStartArray();
        for (int typeIndex = 0; typeIndex < topTypesCount; ++typeIndex)
        {
            TypeExceptionStats stats = sortedStats[typeIndex];

            writer.WriteStartObject();
            writer.WriteString("TypeName", stats.TypeName);
            writer.WriteNumber("Count", stats.Count);
            writer.WriteNumber("PercentOfTotal", stats.Count * 100.0 / exceptionEvents.Count);
            writer.WriteString("SampleMessage", stats.SampleMessage ?? string.Empty);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Timeline parameters, computed before pass 2 (mirrors
        // ContentionJsonExporter.Write's own ordering) - null when there's no
        // meaningful time range (a single instant, or all exceptions at the
        // same RelativeMSec).
        double totalDurationMSec = maxRelativeMSec - minRelativeMSec;
        bool hasTimeline = totalDurationMSec > 0;
        int bucketCount = 0;
        double bucketDurationMSec = 0;
        int[] countByBucket = null;
        int[][] typeSelfByBucket = null;

        if (hasTimeline)
        {
            bucketCount = exceptionEvents.Count < MaxTimelineBuckets ? exceptionEvents.Count : MaxTimelineBuckets;
            bucketDurationMSec = totalDurationMSec / bucketCount;
            countByBucket = new int[bucketCount];

            typeSelfByBucket = new int[topTypesCount][];
            for (int typeIndex = 0; typeIndex < topTypesCount; ++typeIndex)
            {
                typeSelfByBucket[typeIndex] = new int[bucketCount];
            }
        }

        // Group every event's raw Stack by (typeIndex, stack array
        // reference) - mirrors AllocationJsonExporter.cs's
        // BuildDrillDownAggregates, just without the (typeIndex, bucketIndex)
        // cell dimension this feature doesn't have. Also fills the timeline
        // buckets above, since this pass already visits every event.
        Dictionary<int, ExceptionStackAggregate>[] stacksByType = new Dictionary<int, ExceptionStackAggregate>[topTypesCount];
        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly ExceptionEvent exceptionEvent = ref eventsSpan[eventIndex];
            string typeName = string.IsNullOrEmpty(exceptionEvent.ExceptionType) ? "<unknown>" : exceptionEvent.ExceptionType;

            int bucketIndex = -1;
            if (hasTimeline)
            {
                bucketIndex = (int)((exceptionEvent.RelativeMSec - minRelativeMSec) / bucketDurationMSec);
                if (bucketIndex >= bucketCount)
                {
                    bucketIndex = bucketCount - 1;
                }

                ++countByBucket[bucketIndex];
            }

            int typeIndex;
            if (!typeIndexByName.TryGetValue(typeName, out typeIndex))
            {
                continue;
            }

            if (hasTimeline)
            {
                ++typeSelfByBucket[typeIndex][bucketIndex];
            }

            Dictionary<int, ExceptionStackAggregate> typeStacks = stacksByType[typeIndex];
            if (typeStacks == null)
            {
                typeStacks = new Dictionary<int, ExceptionStackAggregate>();
                stacksByType[typeIndex] = typeStacks;
            }

            ExceptionStackAggregate aggregate;
            if (!typeStacks.TryGetValue(exceptionEvent.StackIndex, out aggregate))
            {
                aggregate = new ExceptionStackAggregate();
                aggregate.StackIndex = exceptionEvent.StackIndex;
                aggregate.FirstSeenRelativeMSec = exceptionEvent.RelativeMSec;
                typeStacks[exceptionEvent.StackIndex] = aggregate;
            }

            ++aggregate.Count;
        }

        // Shared by every BuildCallerTree call below - the same physical
        // Stack array can recur across many exceptions of the same type
        // (a hot throw site firing repeatedly), so caching each distinct
        // array's resolved frame-id sequence avoids re-resolving it once
        // per occurrence.
        Dictionary<int, int[]> frameIdCache = new Dictionary<int, int[]>();
        List<string> methodNames = new List<string>();
        Dictionary<string, int> methodNameIndexByName = new Dictionary<string, int>();

        writer.WritePropertyName("typeDrillDown");
        writer.WriteStartArray();
        for (int typeIndex = 0; typeIndex < topTypesCount; ++typeIndex)
        {
            Dictionary<int, ExceptionStackAggregate> typeStacks = stacksByType[typeIndex];

            writer.WriteStartObject();

            if (typeStacks != null && typeStacks.Count > 0)
            {
                List<ExceptionStackAggregate> stackList = new List<ExceptionStackAggregate>(typeStacks.Values);

                int typeTotalCount = 0;
                for (int stackIndex = 0; stackIndex < stackList.Count; ++stackIndex)
                {
                    typeTotalCount += stackList[stackIndex].Count;
                }

                writer.WriteNumber("count", typeTotalCount);
                writer.WriteNumber("distinctStackCount", stackList.Count);

                ExceptionTreeNode tree = BuildCallerTree(stackList, stackTable, symbolTable, frameIdCache);
                WriteBudget budget = new WriteBudget();
                budget.Remaining = DrillDownTreeNodeBudgetPerType;
                WriteCallerTreeChildren(writer, tree, symbolTable, methodNames, methodNameIndexByName, budget);
            }
            else
            {
                writer.WriteNumber("count", 0);
                writer.WriteNumber("distinctStackCount", 0);
                writer.WriteNumber("totalChildCount", 0);
                writer.WritePropertyName("children");
                writer.WriteStartArray();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Timeline: null if there's no meaningful time range, otherwise the
        // per-bucket throw-count breakdown for the chart - countByBucket is
        // every exception (mirrors CpuProfileJsonExporter's samplesByBucket),
        // typeSelfByBucket is the per-ranked-type breakdown (mirrors that
        // file's methodSelfByBucket) that lets hiding a type in the ranked
        // table also subtract its own contribution from the chart, the same
        // way a hidden CPU method already does.
        writer.WritePropertyName("timeline");

        if (!hasTimeline)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("minRelativeMSec", minRelativeMSec);
            writer.WriteNumber("totalDurationMSec", totalDurationMSec);
            writer.WriteNumber("bucketDurationMSec", bucketDurationMSec);
            writer.WriteNumber("bucketCount", bucketCount);

            writer.WritePropertyName("countByBucket");
            writer.WriteStartArray();
            for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
            {
                writer.WriteNumberValue(countByBucket[bucketIndex]);
            }
            writer.WriteEndArray();

            writer.WritePropertyName("typeSelfByBucket");
            writer.WriteStartArray();
            for (int typeIndex = 0; typeIndex < topTypesCount; ++typeIndex)
            {
                writer.WriteStartArray();
                for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
                {
                    writer.WriteNumberValue(typeSelfByBucket[typeIndex][bucketIndex]);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        // Written last since it's only fully populated once every type's
        // tree has been walked - JSON object key order carries no meaning
        // here, same as AllocationJsonExporter.cs's own methodNames.
        writer.WritePropertyName("methodNames");
        writer.WriteStartArray();
        for (int nameIndex = 0; nameIndex < methodNames.Count; ++nameIndex)
        {
            writer.WriteStringValue(methodNames[nameIndex]);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    // Builds the full call-stack tree from every distinct raw
    // ExceptionStackAggregate for one type - not capped, not pre-selected;
    // every real distinct throw-site stack contributes its entire frame
    // chain, leaf (throw site) first. See AllocationJsonExporter.cs's
    // BuildCallerTree for the fuller rationale (folding at every depth, not
    // just the leaf) this mirrors.
    private static ExceptionTreeNode BuildCallerTree(List<ExceptionStackAggregate> rawStacks, StackTable stackTable, MethodSymbolTable symbolTable, Dictionary<int, int[]> frameIdCache)
    {
        ExceptionTreeNode root = new ExceptionTreeNode();

        for (int stackIndex = 0; stackIndex < rawStacks.Count; ++stackIndex)
        {
            ExceptionStackAggregate rawStack = rawStacks[stackIndex];
            ExceptionTreeNode current = root;

            long[] stackFrames = stackTable.FramesAt(rawStack.StackIndex);
            if (stackFrames.Length == 0)
            {
                current = GetOrAddChild(current, NoStackFrameId);
                AccumulateTreeNode(current, rawStack);
                continue;
            }

            int[] frameIds;
            if (!frameIdCache.TryGetValue(rawStack.StackIndex, out frameIds))
            {
                frameIds = new int[stackFrames.Length];
                for (int frameIndex = 0; frameIndex < stackFrames.Length; ++frameIndex)
                {
                    frameIds[frameIndex] = symbolTable.ResolveId(stackFrames[frameIndex], rawStack.FirstSeenRelativeMSec);
                }

                frameIdCache[rawStack.StackIndex] = frameIds;
            }

            for (int frameIndex = 0; frameIndex < frameIds.Length; ++frameIndex)
            {
                current = GetOrAddChild(current, frameIds[frameIndex]);
                AccumulateTreeNode(current, rawStack);
            }
        }

        return root;
    }

    private static ExceptionTreeNode GetOrAddChild(ExceptionTreeNode node, int frameId)
    {
        ExceptionTreeNode child;
        if (!node.Children.TryGetValue(frameId, out child))
        {
            child = new ExceptionTreeNode();
            node.Children[frameId] = child;
        }

        return child;
    }

    private static void AccumulateTreeNode(ExceptionTreeNode node, ExceptionStackAggregate rawStack)
    {
        node.Count += rawStack.Count;
        ++node.DistinctStackCount;
    }

    // Writes node's "children" array - { frame, count, distinctStackCount,
    // totalChildCount, children }, recursing depth-first. frame is an
    // integer index into the "methodNames" pool (see Write) rather than a
    // raw string, same string-interning reasoning as
    // AllocationJsonExporter.cs. Sorted by count descending and capped at
    // DrillDownTreeChildrenLimit per node plus the shared budget's total
    // node count across the whole tree - totalChildCount is always the true
    // count before either restriction, so a consumer can tell a truncated
    // children list from a genuinely short one.
    private static void WriteCallerTreeChildren(Utf8JsonWriter writer, ExceptionTreeNode node, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName, WriteBudget budget)
    {
        writer.WriteNumber("totalChildCount", node.Children.Count);
        writer.WritePropertyName("children");
        writer.WriteStartArray();

        if (node.Children.Count > 0 && budget.Remaining > 0)
        {
            List<KeyValuePair<int, ExceptionTreeNode>> children = new List<KeyValuePair<int, ExceptionTreeNode>>(node.Children);
            children.Sort((left, right) => right.Value.Count.CompareTo(left.Value.Count));

            int childCount = children.Count < DrillDownTreeChildrenLimit ? children.Count : DrillDownTreeChildrenLimit;
            for (int childIndex = 0; childIndex < childCount && budget.Remaining > 0; ++childIndex)
            {
                int frameId = children[childIndex].Key;
                ExceptionTreeNode child = children[childIndex].Value;
                --budget.Remaining;

                string frameName = frameId == NoStackFrameId ? NoStackLeafName : symbolTable.NameForId(frameId);

                int frameNameIndex;
                if (!methodNameIndexByName.TryGetValue(frameName, out frameNameIndex))
                {
                    frameNameIndex = methodNames.Count;
                    methodNames.Add(frameName);
                    methodNameIndexByName[frameName] = frameNameIndex;
                }

                writer.WriteStartObject();
                writer.WriteNumber("frame", frameNameIndex);
                writer.WriteNumber("count", child.Count);
                writer.WriteNumber("distinctStackCount", child.DistinctStackCount);
                WriteCallerTreeChildren(writer, child, symbolTable, methodNames, methodNameIndexByName, budget);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Exceptions)
