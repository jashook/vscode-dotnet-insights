////////////////////////////////////////////////////////////////////////////////
// Module: ContentionJsonExporter.cs
//
// Notes:
// Serializes a List<ContentionEvent> into the "contentionSummary" shape the
// VS Code extension's ContentionRenderer.ts consumes: totalContentionCount/
// totalContentionWaitMSec, a topSites ranking (by total wait time, not count),
// and a per-site folded caller-stack tree (siteDrillDown) plus a time-bucketed
// waitMSecByBucket timeline, so the ranked-sites table can expand to show
// caller chains and the timeline can zoom the ranked list.
//
// Structurally mirrors ExceptionJsonExporter.cs (see that file's header
// comment for the full rationale: plain new'd nodes + running WriteBudget
// counter, no DrillDownTreeNodePool complexity needed at contention volumes).
// The primary metric is totalWaitMSec (double) rather than count (int): sites
// are ranked by total milliseconds of lock wait, and tree nodes carry both
// contentionCount and totalWaitMSec.
//
// Sites are aggregated by the resolved leaf frame id of each event's Stack
// (frame index 0, the innermost/direct lock-acquisition frame) - the same
// fold-by-leaf-frame approach ExceptionJsonExporter uses for exception types.
// Events with no stack get a synthetic "<no stack captured>" leaf, same
// convention as ExceptionJsonExporter and AllocationJsonExporter.
//
// Writes directly into a Utf8JsonWriter, per CLAUDE.md's JSON-serialization
// rule and this codebase's established precedent.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Contention {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ContentionJsonExporter
{
    private const int TopSitesLimit = 50;
    private const int DrillDownTreeChildrenLimit = 12;
    private const int DrillDownTreeNodeBudgetPerSite = 300;

    // Reserved frame id for the "<no stack captured>" sentinel - same
    // convention as ExceptionJsonExporter and AllocationJsonExporter.
    private const int NoStackFrameId = -1;
    private const string NoStackLeafName = "<no stack captured>";

    private const int MaxTimelineBuckets = 100;

    private sealed class SiteStats
    {
        public int LeafFrameId;
        public string LeafFrameName;
        public int ContentionCount;
        public double TotalWaitMSec;
    }

    private sealed class ContentionStackAggregate
    {
        public long[] Stack;
        public int ContentionCount;
        public double TotalWaitMSec;
        public double FirstSeenRelativeMSec;
    }

    private sealed class ContentionTreeNode
    {
        public int ContentionCount;
        public double TotalWaitMSec;
        public int DistinctStackCount;
        public Dictionary<int, ContentionTreeNode> Children = new Dictionary<int, ContentionTreeNode>();
    }

    private sealed class WriteBudget
    {
        public int Remaining;
    }

    public static void Write(Utf8JsonWriter writer, List<ContentionEvent> contentionEvents, MethodSymbolTable symbolTable)
    {
        writer.WriteStartObject();

        writer.WriteNumber("totalContentionCount", contentionEvents.Count);

        if (contentionEvents.Count == 0)
        {
            writer.WriteNumber("totalContentionWaitMSec", 0.0);
            writer.WritePropertyName("topSites");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName("siteDrillDown");
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

        Span<ContentionEvent> eventsSpan = CollectionsMarshal.AsSpan(contentionEvents);

        // Pass 1: compute per-leaf-frame stats and track the overall time range
        // for timeline bucketing.
        Dictionary<int, SiteStats> statsByLeafFrameId = new Dictionary<int, SiteStats>();
        double totalWaitMSec = 0;
        double minRelativeMSec = double.MaxValue;
        double maxRelativeMSec = double.MinValue;

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly ContentionEvent contentionEvent = ref eventsSpan[eventIndex];

            int leafFrameId;
            string leafFrameName;

            if (contentionEvent.Stack.Length == 0)
            {
                leafFrameId = NoStackFrameId;
                leafFrameName = NoStackLeafName;
            }
            else
            {
                leafFrameId = symbolTable.ResolveId(contentionEvent.Stack[0], contentionEvent.RelativeMSec);
                leafFrameName = symbolTable.NameForId(leafFrameId);
            }

            SiteStats stats;

            if (!statsByLeafFrameId.TryGetValue(leafFrameId, out stats))
            {
                stats = new SiteStats();
                stats.LeafFrameId = leafFrameId;
                stats.LeafFrameName = leafFrameName;
                statsByLeafFrameId[leafFrameId] = stats;
            }

            ++stats.ContentionCount;
            stats.TotalWaitMSec += contentionEvent.DurationMSec;
            totalWaitMSec += contentionEvent.DurationMSec;

            if (contentionEvent.RelativeMSec < minRelativeMSec)
            {
                minRelativeMSec = contentionEvent.RelativeMSec;
            }

            if (contentionEvent.RelativeMSec > maxRelativeMSec)
            {
                maxRelativeMSec = contentionEvent.RelativeMSec;
            }
        }

        writer.WriteNumber("totalContentionWaitMSec", totalWaitMSec);

        // Sort by TotalWaitMSec descending (the dimension users care about
        // most for lock contention: who's blocking the most total time).
        List<SiteStats> sortedStats = new List<SiteStats>(statsByLeafFrameId.Values);
        sortedStats.Sort((SiteStats left, SiteStats right) => right.TotalWaitMSec.CompareTo(left.TotalWaitMSec));

        int topSitesCount = sortedStats.Count < TopSitesLimit ? sortedStats.Count : TopSitesLimit;
        Dictionary<int, int> rankIndexByLeafFrameId = new Dictionary<int, int>(topSitesCount);

        for (int siteIndex = 0; siteIndex < topSitesCount; ++siteIndex)
        {
            rankIndexByLeafFrameId[sortedStats[siteIndex].LeafFrameId] = siteIndex;
        }

        writer.WritePropertyName("topSites");
        writer.WriteStartArray();

        for (int siteIndex = 0; siteIndex < topSitesCount; ++siteIndex)
        {
            SiteStats stats = sortedStats[siteIndex];
            double averageWaitMSec = stats.ContentionCount > 0 ? stats.TotalWaitMSec / stats.ContentionCount : 0.0;
            double percentOfTotal = totalWaitMSec > 0 ? stats.TotalWaitMSec * 100.0 / totalWaitMSec : 0.0;

            writer.WriteStartObject();
            writer.WriteString("SiteName", stats.LeafFrameName);
            writer.WriteNumber("ContentionCount", stats.ContentionCount);
            writer.WriteNumber("TotalWaitMSec", stats.TotalWaitMSec);
            writer.WriteNumber("AverageWaitMSec", averageWaitMSec);
            writer.WriteNumber("PercentOfTotalWait", percentOfTotal);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // Compute timeline parameters before pass 2.
        double totalDurationMSec = maxRelativeMSec - minRelativeMSec;
        bool hasTimeline = totalDurationMSec > 0;
        int bucketCount = 0;
        double bucketDurationMSec = 0;
        double[] waitMSecByBucket = null;

        if (hasTimeline)
        {
            bucketCount = contentionEvents.Count < MaxTimelineBuckets ? contentionEvents.Count : MaxTimelineBuckets;
            bucketDurationMSec = totalDurationMSec / bucketCount;
            waitMSecByBucket = new double[bucketCount];
        }

        // Pass 2: build per-site stack aggregates and fill timeline buckets.
        Dictionary<long[], ContentionStackAggregate>[] stacksByRankedSite = new Dictionary<long[], ContentionStackAggregate>[topSitesCount];

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly ContentionEvent contentionEvent = ref eventsSpan[eventIndex];

            int leafFrameId;

            if (contentionEvent.Stack.Length == 0)
            {
                leafFrameId = NoStackFrameId;
            }
            else
            {
                leafFrameId = symbolTable.ResolveId(contentionEvent.Stack[0], contentionEvent.RelativeMSec);
            }

            int siteIndex;

            if (rankIndexByLeafFrameId.TryGetValue(leafFrameId, out siteIndex))
            {
                Dictionary<long[], ContentionStackAggregate> siteStacks = stacksByRankedSite[siteIndex];

                if (siteStacks == null)
                {
                    siteStacks = new Dictionary<long[], ContentionStackAggregate>(ReferenceEqualityComparer.Instance);
                    stacksByRankedSite[siteIndex] = siteStacks;
                }

                ContentionStackAggregate aggregate;

                if (!siteStacks.TryGetValue(contentionEvent.Stack, out aggregate))
                {
                    aggregate = new ContentionStackAggregate();
                    aggregate.Stack = contentionEvent.Stack;
                    aggregate.FirstSeenRelativeMSec = contentionEvent.RelativeMSec;
                    siteStacks[contentionEvent.Stack] = aggregate;
                }

                ++aggregate.ContentionCount;
                aggregate.TotalWaitMSec += contentionEvent.DurationMSec;
            }

            if (hasTimeline)
            {
                double offset = contentionEvent.RelativeMSec - minRelativeMSec;
                int bucketIndex = (int)(offset / bucketDurationMSec);

                if (bucketIndex >= bucketCount)
                {
                    bucketIndex = bucketCount - 1;
                }

                waitMSecByBucket[bucketIndex] += contentionEvent.DurationMSec;
            }
        }

        // Shared per-Write call state for the drill-down trees.
        Dictionary<long[], int[]> frameIdCache = new Dictionary<long[], int[]>(ReferenceEqualityComparer.Instance);
        List<string> methodNames = new List<string>();
        Dictionary<string, int> methodNameIndexByName = new Dictionary<string, int>();

        writer.WritePropertyName("siteDrillDown");
        writer.WriteStartArray();

        for (int siteIndex = 0; siteIndex < topSitesCount; ++siteIndex)
        {
            Dictionary<long[], ContentionStackAggregate> siteStacks = stacksByRankedSite[siteIndex];

            writer.WriteStartObject();

            if (siteStacks != null && siteStacks.Count > 0)
            {
                List<ContentionStackAggregate> stackList = new List<ContentionStackAggregate>(siteStacks.Values);

                int typeTotalCount = 0;
                double typeTotalWaitMSec = 0;

                for (int stackIndex = 0; stackIndex < stackList.Count; ++stackIndex)
                {
                    typeTotalCount += stackList[stackIndex].ContentionCount;
                    typeTotalWaitMSec += stackList[stackIndex].TotalWaitMSec;
                }

                writer.WriteNumber("contentionCount", typeTotalCount);
                writer.WriteNumber("totalWaitMSec", typeTotalWaitMSec);
                writer.WriteNumber("distinctStackCount", stackList.Count);

                ContentionTreeNode tree = BuildCallerTree(stackList, symbolTable, frameIdCache);
                WriteBudget budget = new WriteBudget();
                budget.Remaining = DrillDownTreeNodeBudgetPerSite;
                WriteCallerTreeChildren(writer, tree, symbolTable, methodNames, methodNameIndexByName, budget);
            }
            else
            {
                writer.WriteNumber("contentionCount", 0);
                writer.WriteNumber("totalWaitMSec", 0.0);
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
        // per-bucket wait-time breakdown for the chart.
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
            writer.WritePropertyName("waitMSecByBucket");
            writer.WriteStartArray();

            for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
            {
                writer.WriteNumberValue(waitMSecByBucket[bucketIndex]);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WritePropertyName("methodNames");
        writer.WriteStartArray();

        for (int nameIndex = 0; nameIndex < methodNames.Count; ++nameIndex)
        {
            writer.WriteStringValue(methodNames[nameIndex]);
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    // Builds the folded caller-stack tree for one contention site, accumulating
    // both contentionCount and totalWaitMSec at every node - mirrors
    // ExceptionJsonExporter.BuildCallerTree's own algorithm and rationale (fold
    // at every depth, not just the leaf, to preserve every real caller
    // permutation).
    private static ContentionTreeNode BuildCallerTree(List<ContentionStackAggregate> rawStacks, MethodSymbolTable symbolTable, Dictionary<long[], int[]> frameIdCache)
    {
        ContentionTreeNode root = new ContentionTreeNode();

        for (int stackIndex = 0; stackIndex < rawStacks.Count; ++stackIndex)
        {
            ContentionStackAggregate rawStack = rawStacks[stackIndex];
            ContentionTreeNode current = root;

            if (rawStack.Stack.Length == 0)
            {
                current = GetOrAddChild(current, NoStackFrameId);
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
                current = GetOrAddChild(current, frameIds[frameIndex]);
                AccumulateTreeNode(current, rawStack);
            }
        }

        return root;
    }

    private static ContentionTreeNode GetOrAddChild(ContentionTreeNode node, int frameId)
    {
        ContentionTreeNode child;

        if (!node.Children.TryGetValue(frameId, out child))
        {
            child = new ContentionTreeNode();
            node.Children[frameId] = child;
        }

        return child;
    }

    private static void AccumulateTreeNode(ContentionTreeNode node, ContentionStackAggregate rawStack)
    {
        node.ContentionCount += rawStack.ContentionCount;
        node.TotalWaitMSec += rawStack.TotalWaitMSec;
        ++node.DistinctStackCount;
    }

    // Writes node's "children" array - { frame, contentionCount, totalWaitMSec,
    // distinctStackCount, totalChildCount, children }, recursing depth-first.
    // Children are sorted by totalWaitMSec descending (primary metric) and
    // capped at DrillDownTreeChildrenLimit per node plus the WriteBudget total.
    // Mirrors ExceptionJsonExporter.WriteCallerTreeChildren's own shape, with
    // totalWaitMSec replacing count as the sort key.
    private static void WriteCallerTreeChildren(Utf8JsonWriter writer, ContentionTreeNode node, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName, WriteBudget budget)
    {
        writer.WriteNumber("totalChildCount", node.Children.Count);
        writer.WritePropertyName("children");
        writer.WriteStartArray();

        if (node.Children.Count > 0 && budget.Remaining > 0)
        {
            List<KeyValuePair<int, ContentionTreeNode>> children = new List<KeyValuePair<int, ContentionTreeNode>>(node.Children);
            children.Sort((KeyValuePair<int, ContentionTreeNode> left, KeyValuePair<int, ContentionTreeNode> right) => right.Value.TotalWaitMSec.CompareTo(left.Value.TotalWaitMSec));

            int childCount = children.Count < DrillDownTreeChildrenLimit ? children.Count : DrillDownTreeChildrenLimit;

            for (int childIndex = 0; childIndex < childCount && budget.Remaining > 0; ++childIndex)
            {
                int frameId = children[childIndex].Key;
                ContentionTreeNode child = children[childIndex].Value;
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
                writer.WriteNumber("contentionCount", child.ContentionCount);
                writer.WriteNumber("totalWaitMSec", child.TotalWaitMSec);
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

} // end of namespace(DotnetInsights.NetTrace.Contention)
