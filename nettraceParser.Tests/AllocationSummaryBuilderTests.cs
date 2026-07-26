////////////////////////////////////////////////////////////////////////////////
// Module: AllocationSummaryBuilderTests.cs
//
// Notes:
// AllocationSummaryBuilder.Build is pure aggregation over List<AllocationEvent>
// (no byte-offset decoding involved), so these tests build AllocationEvent
// instances directly rather than raw EventRecord payloads. Three behaviors
// get dedicated coverage because they were each a real design decision
// worth pinning: the ChartTopTypesLimit "Other" bucket for typeTimeline
// (unlike topTypes, which allows up to TopTypesLimit=100), the exact
// byte-for-byte reconciliation between typeTimeline's bucket x type matrix
// and totalSampledBytes (the same invariant already verified manually
// against the real capture before this test file existed), and ticks being
// re-sorted by RelativeMSec regardless of input order.
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class AllocationSummaryBuilderTests
{
    private static AllocationEvent MakeEvent(string typeName, long amount, GCAllocationKind kind, double relativeMSec)
    {
        return new AllocationEvent
        {
            TypeName = typeName,
            AllocationAmount = amount,
            AllocationKind = kind,
            RelativeMSec = relativeMSec,
            HeapIndex = 0
        };
    }

    // These tests don't exercise stack resolution (see
    // DrillDownBuilderTests.cs for that) - an empty stacksById/symbolTable
    // pair is enough to call Build.
    private static Dictionary<int, long[]> EmptyStacksById()
    {
        return new Dictionary<int, long[]>();
    }

    private static MethodSymbolTable EmptySymbolTable()
    {
        return MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8);
    }

    // AllocationSummaryBuilder.Write streams directly to a Utf8JsonWriter
    // (see AllocationJsonExporter.cs for why) rather than returning a
    // JsonObject - write to an in-memory buffer and parse it back so these
    // tests can keep asserting against the real output shape.
    private static JsonObject Build(List<AllocationEvent> events)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
            {
                AllocationSummaryBuilder.Write(writer, events, EmptyStacksById(), EmptySymbolTable());
            }

            return (JsonObject)JsonNode.Parse(stream.ToArray());
        }
    }

    [Fact]
    public void Build_RanksTopTypesDescendingByTotalBytesWithPerKindCounts()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("Small.Type", 100, GCAllocationKind.Small, 0),
            MakeEvent("Small.Type", 100, GCAllocationKind.Small, 1),
            MakeEvent("Big.Type", 5000, GCAllocationKind.Large, 2),
            MakeEvent("Pinned.Type", 50, GCAllocationKind.Pinned, 3)
        };

        JsonObject summary = Build(events);
        JsonArray topTypes = summary["topTypes"].AsArray();

        Assert.Equal(3, topTypes.Count);
        Assert.Equal("Big.Type", topTypes[0]["TypeName"].GetValue<string>());
        Assert.Equal(5000, topTypes[0]["TotalBytes"].GetValue<long>());
        Assert.Equal(1, topTypes[0]["LargeCount"].GetValue<int>());

        Assert.Equal("Small.Type", topTypes[1]["TypeName"].GetValue<string>());
        Assert.Equal(200, topTypes[1]["TotalBytes"].GetValue<long>());
        Assert.Equal(2, topTypes[1]["TickCount"].GetValue<int>());
        Assert.Equal(2, topTypes[1]["SmallCount"].GetValue<int>());

        Assert.Equal("Pinned.Type", topTypes[2]["TypeName"].GetValue<string>());
        Assert.Equal(1, topTypes[2]["PinnedCount"].GetValue<int>());

        Assert.Equal(3, summary["distinctTypeCount"].GetValue<int>());
        Assert.Equal(4, summary["totalTickCount"].GetValue<int>());
        Assert.Equal(5250, summary["totalSampledBytes"].GetValue<long>());
    }

    [Fact]
    public void Build_FallsBackToUnknownForNullOrEmptyTypeName()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent(null, 100, GCAllocationKind.Small, 0),
            MakeEvent("", 200, GCAllocationKind.Small, 1)
        };

        JsonObject summary = Build(events);
        JsonArray topTypes = summary["topTypes"].AsArray();

        Assert.Equal(1, summary["distinctTypeCount"].GetValue<int>());
        Assert.Equal("<unknown>", topTypes[0]["TypeName"].GetValue<string>());
        Assert.Equal(300, topTypes[0]["TotalBytes"].GetValue<long>());
    }

    [Fact]
    public void Build_CapsTopTypesAtOneHundredEvenWithMoreDistinctTypes()
    {
        List<AllocationEvent> events = new List<AllocationEvent>();
        for (int typeIndex = 0; typeIndex < 101; ++typeIndex)
        {
            // Descending bytes by index so sort order is deterministic and
            // the 101st (smallest) type is the one that should be excluded.
            events.Add(MakeEvent($"Type{typeIndex}", 101 - typeIndex, GCAllocationKind.Small, typeIndex));
        }

        JsonObject summary = Build(events);
        JsonArray topTypes = summary["topTypes"].AsArray();

        Assert.Equal(101, summary["distinctTypeCount"].GetValue<int>());
        Assert.Equal(100, topTypes.Count);
        // Type100 (bytes=1, the smallest) is the one excluded - the last
        // *kept* entry is Type99 (bytes=2), the smallest of the top 100.
        Assert.Equal("Type99", topTypes[topTypes.Count - 1]["TypeName"].GetValue<string>());
    }

    [Fact]
    public void Build_TicksAreSortedByRelativeMSecRegardlessOfInputOrder()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("A", 10, GCAllocationKind.Small, 30),
            MakeEvent("B", 20, GCAllocationKind.Small, 10),
            MakeEvent("C", 30, GCAllocationKind.Small, 20)
        };

        JsonObject summary = Build(events);
        JsonArray ticks = summary["ticks"].AsArray();

        Assert.Equal(3, ticks.Count);
        Assert.Equal(10.0, ticks[0]["RelativeMSec"].GetValue<double>());
        Assert.Equal(20, ticks[0]["AllocationAmount"].GetValue<long>());
        Assert.Equal(20.0, ticks[1]["RelativeMSec"].GetValue<double>());
        Assert.Equal(30.0, ticks[2]["RelativeMSec"].GetValue<double>());
    }

    [Fact]
    public void Build_TypeTimelineGroupsTypesBeyondChartTopTypesLimitIntoOther()
    {
        // ChartTopTypesLimit is 8 (private const, AllocationJsonExporter.cs) -
        // nine distinct types here means exactly one must land in "Other".
        List<AllocationEvent> events = new List<AllocationEvent>();
        for (int typeIndex = 0; typeIndex < 9; ++typeIndex)
        {
            events.Add(MakeEvent($"Type{typeIndex}", 900 - (typeIndex * 100), GCAllocationKind.Small, 0));
        }

        JsonObject summary = Build(events);
        JsonObject typeTimeline = summary["typeTimeline"].AsObject();
        JsonArray types = typeTimeline["types"].AsArray();

        Assert.Equal(9, types.Count);
        Assert.Equal("Other", types[types.Count - 1].GetValue<string>());
        // The smallest-byte type (Type8, the 9th/last-ranked) is the one
        // pushed into "Other" - the other eight keep their own column.
        Assert.DoesNotContain(types, type => type.GetValue<string>() == "Type8");
    }

    [Fact]
    public void Build_TypeTimelineBucketsByOneSecondAndReconcilesWithTotalSampledBytes()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("A", 1000, GCAllocationKind.Small, 500),    // bucket 0 (0-999ms)
            MakeEvent("A", 2000, GCAllocationKind.Small, 1500),   // bucket 1 (1000-1999ms)
            MakeEvent("B", 3000, GCAllocationKind.Small, 999)     // bucket 0
        };

        JsonObject summary = Build(events);
        JsonObject typeTimeline = summary["typeTimeline"].AsObject();

        Assert.Equal(1000.0, typeTimeline["bucketWidthMSec"].GetValue<double>());

        JsonArray buckets = typeTimeline["buckets"].AsArray();
        Assert.Equal(2, buckets.Count);
        Assert.Equal(0.0, buckets[0]["bucketStartMSec"].GetValue<double>());
        Assert.Equal(1000.0, buckets[1]["bucketStartMSec"].GetValue<double>());

        long totalAcrossMatrix = 0;
        foreach (JsonObject bucket in buckets)
        {
            foreach (JsonNode bytesForType in bucket["bytesByType"].AsArray())
            {
                totalAcrossMatrix += bytesForType.GetValue<long>();
            }
        }

        Assert.Equal(summary["totalSampledBytes"].GetValue<long>(), totalAcrossMatrix);
        Assert.Equal(6000, totalAcrossMatrix);
    }

    [Fact]
    public void Build_HandlesEmptyInputWithoutThrowing()
    {
        JsonObject summary = Build(new List<AllocationEvent>());

        Assert.Equal(0, summary["totalSampledBytes"].GetValue<long>());
        Assert.Equal(0, summary["distinctTypeCount"].GetValue<int>());
        Assert.Equal(0, summary["totalTickCount"].GetValue<int>());
        Assert.Empty(summary["topTypes"].AsArray());
        Assert.Empty(summary["ticks"].AsArray());
        Assert.Empty(summary["typeTimeline"]["buckets"].AsArray());
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
