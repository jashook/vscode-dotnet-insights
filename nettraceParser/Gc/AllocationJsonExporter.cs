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
// pre-aggregated view.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Gc {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;
using System.Text.Json.Nodes;

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

    private class TypeAllocStats
    {
        public string TypeName;
        public long TotalBytes;
        public int TickCount;
        public int SmallCount;
        public int LargeCount;
        public int PinnedCount;
    }

    public static JsonObject Build(List<AllocationEvent> allocationEvents)
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

        JsonArray topTypesArray = new JsonArray();
        int topTypesCount = sortedStats.Count < TopTypesLimit ? sortedStats.Count : TopTypesLimit;

        for (int typeIndex = 0; typeIndex < topTypesCount; ++typeIndex)
        {
            TypeAllocStats stats = sortedStats[typeIndex];

            JsonObject typeObject = new JsonObject();
            typeObject["TypeName"] = stats.TypeName;
            typeObject["TotalBytes"] = stats.TotalBytes;
            typeObject["TickCount"] = stats.TickCount;
            typeObject["SmallCount"] = stats.SmallCount;
            typeObject["LargeCount"] = stats.LargeCount;
            typeObject["PinnedCount"] = stats.PinnedCount;
            topTypesArray.Add(typeObject);
        }

        JsonArray ticksArray = BuildTicks(allocationEvents);
        JsonObject typeTimeline = BuildTypeTimeline(allocationEvents, sortedStats);

        JsonObject summary = new JsonObject();
        summary["totalSampledBytes"] = totalSampledBytes;
        summary["distinctTypeCount"] = statsByType.Count;
        summary["totalTickCount"] = allocationEvents.Count;
        summary["topTypes"] = topTypesArray;
        summary["ticks"] = ticksArray;
        summary["typeTimeline"] = typeTimeline;

        return summary;
    }

    // Per-second (TypeTimelineBucketWidthMSec) bytes-by-type breakdown for
    // the stacked bar chart under the allocation-rate chart. TypeName is
    // deliberately not carried on individual ticks (BuildTicks/AllocationEvent
    // - see this file's header comment), so this is the only place that
    // needs a per-event/per-type join; the result is normalized into a
    // shared "types" column list plus parallel per-bucket byte arrays
    // (rather than repeating type name strings as JSON object keys in every
    // bucket) to keep the payload compact.
    private static JsonObject BuildTypeTimeline(List<AllocationEvent> allocationEvents, List<TypeAllocStats> sortedStats)
    {
        int chartTypeCount = sortedStats.Count < ChartTopTypesLimit ? sortedStats.Count : ChartTopTypesLimit;

        Dictionary<string, int> columnIndexByType = new Dictionary<string, int>();
        JsonArray typesArray = new JsonArray();

        for (int typeIndex = 0; typeIndex < chartTypeCount; ++typeIndex)
        {
            string typeName = sortedStats[typeIndex].TypeName;
            columnIndexByType[typeName] = typeIndex;
            typesArray.Add(typeName);
        }

        // Always present as the last column, even if every type already fit
        // in the chart's top set (it just stays 0 in that case) - simpler
        // than conditionally omitting it.
        int otherColumnIndex = chartTypeCount;
        typesArray.Add("Other");

        JsonObject result = new JsonObject();
        result["bucketWidthMSec"] = TypeTimelineBucketWidthMSec;
        result["types"] = typesArray;

        if (allocationEvents.Count == 0)
        {
            result["buckets"] = new JsonArray();
            return result;
        }

        double lastRelativeMSec = 0;
        for (int eventIndex = 0; eventIndex < allocationEvents.Count; ++eventIndex)
        {
            if (allocationEvents[eventIndex].RelativeMSec > lastRelativeMSec)
            {
                lastRelativeMSec = allocationEvents[eventIndex].RelativeMSec;
            }
        }

        int bucketCount = (int)(lastRelativeMSec / TypeTimelineBucketWidthMSec) + 1;
        int columnCount = chartTypeCount + 1;
        long[,] bytesByBucketAndType = new long[bucketCount, columnCount];

        for (int eventIndex = 0; eventIndex < allocationEvents.Count; ++eventIndex)
        {
            AllocationEvent allocationEvent = allocationEvents[eventIndex];

            int bucketIndex = (int)(allocationEvent.RelativeMSec / TypeTimelineBucketWidthMSec);
            if (bucketIndex >= bucketCount)
            {
                bucketIndex = bucketCount - 1;
            }

            string typeName = string.IsNullOrEmpty(allocationEvent.TypeName) ? "<unknown>" : allocationEvent.TypeName;

            int columnIndex;
            if (!columnIndexByType.TryGetValue(typeName, out columnIndex))
            {
                columnIndex = otherColumnIndex;
            }

            bytesByBucketAndType[bucketIndex, columnIndex] += allocationEvent.AllocationAmount;
        }

        JsonArray bucketsArray = new JsonArray();
        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            JsonArray bytesByTypeArray = new JsonArray();
            for (int columnIndex = 0; columnIndex < columnCount; ++columnIndex)
            {
                bytesByTypeArray.Add(bytesByBucketAndType[bucketIndex, columnIndex]);
            }

            JsonObject bucketObject = new JsonObject();
            bucketObject["bucketStartMSec"] = bucketIndex * TypeTimelineBucketWidthMSec;
            bucketObject["bytesByType"] = bytesByTypeArray;
            bucketsArray.Add(bucketObject);
        }

        result["buckets"] = bucketsArray;
        return result;
    }

    private static int CompareByTotalBytesDescending(TypeAllocStats left, TypeAllocStats right)
    {
        return right.TotalBytes.CompareTo(left.TotalBytes);
    }

    // Sorted by RelativeMSec defensively (matches GcJsonExporter.cs's own
    // heap-sort-before-serializing precedent) rather than trusting wire
    // order to already be time-ordered.
    private static JsonArray BuildTicks(List<AllocationEvent> allocationEvents)
    {
        List<AllocationEvent> sortedEvents = new List<AllocationEvent>(allocationEvents);
        sortedEvents.Sort(CompareByRelativeMSecAscending);

        JsonArray ticksArray = new JsonArray();

        for (int eventIndex = 0; eventIndex < sortedEvents.Count; ++eventIndex)
        {
            AllocationEvent allocationEvent = sortedEvents[eventIndex];

            JsonObject tickObject = new JsonObject();
            tickObject["RelativeMSec"] = allocationEvent.RelativeMSec;
            tickObject["AllocationAmount"] = allocationEvent.AllocationAmount;
            ticksArray.Add(tickObject);
        }

        return ticksArray;
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
