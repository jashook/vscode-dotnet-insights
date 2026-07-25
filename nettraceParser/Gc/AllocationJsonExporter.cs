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

        JsonObject summary = new JsonObject();
        summary["totalSampledBytes"] = totalSampledBytes;
        summary["distinctTypeCount"] = statsByType.Count;
        summary["totalTickCount"] = allocationEvents.Count;
        summary["topTypes"] = topTypesArray;
        summary["ticks"] = ticksArray;

        return summary;
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
