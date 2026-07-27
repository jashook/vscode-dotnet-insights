////////////////////////////////////////////////////////////////////////////////
// Module: DrillDownBuilderTests.cs
//
// Notes:
// AllocationSummaryBuilder's drillDown output (BuildDrillDown, a private
// method - exercised here only through the public Build entry point, same
// as topTypes/typeTimeline already are) is what the webview's "Drill Down"
// tab renders when a stacked-chart segment is clicked: for a given
// (type, 1-second bucket) cell, the resolved call stacks that produced
// those allocations. Three behaviors get dedicated coverage because
// getting any of them wrong would make the feature actively misleading
// rather than just incomplete: the "Other" column must never be drillable
// (mixing unrelated types under one cell), a tick with no captured stack
// must still be counted (grouped under a placeholder) rather than silently
// vanishing from the totals, and the per-cell stack cap must keep the
// *largest* stacks, not an arbitrary subset.
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

public class DrillDownBuilderTests
{
    private static AllocationEvent MakeEvent(string typeName, long amount, double relativeMSec, int stackId)
    {
        return new AllocationEvent
        {
            TypeName = typeName,
            AllocationAmount = amount,
            AllocationKind = GCAllocationKind.Small,
            RelativeMSec = relativeMSec,
            StackId = stackId
        };
    }

    // AllocationSummaryBuilder.Write streams directly to a Utf8JsonWriter
    // (see AllocationJsonExporter.cs for why) rather than returning a
    // JsonObject - write to an in-memory buffer and parse it back so these
    // tests can keep asserting against the real output shape.
    private static JsonObject Build(List<AllocationEvent> events, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
            {
                AllocationSummaryBuilder.Write(writer, events, stacksById, symbolTable);
            }

            return (JsonObject)JsonNode.Parse(stream.ToArray());
        }
    }

    private static EventRecord MakeRundownEvent(long startAddress, int size, string name)
    {
        return new EventRecord
        {
            ProviderName = "Microsoft-Windows-DotNETRuntimeRundown",
            EventId = ClrRundownEventIds.MethodDCStartVerbose,
            Version = 1,
            PayloadBytes = new PayloadBuilder()
                .WriteAddress(1, 8).WriteAddress(2, 8).WriteAddress(startAddress, 8)
                .WriteInt32(size).WriteInt32(0x06000001).WriteInt32(0)
                .WriteUnicodeString("").WriteUnicodeString(name).WriteUnicodeString("sig")
                .ToArray()
        };
    }

    [Fact]
    public void Build_DrillDown_GroupsByTypeBucketAndStackIdAndSortsDescendingByBytes()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("TypeA", 100, relativeMSec: 100, stackId: 10),
            MakeEvent("TypeA", 200, relativeMSec: 100, stackId: 10),  // same cell+stack - aggregates
            MakeEvent("TypeA", 50, relativeMSec: 100, stackId: 20),   // same cell, different stack
            MakeEvent("TypeA", 999, relativeMSec: 1500, stackId: 10)  // different bucket - separate cell
        };

        Dictionary<int, long[]> stacksById = new Dictionary<int, long[]>
        {
            { 10, new long[] { 1000 } },
            { 20, new long[] { 2000 } }
        };

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>
        {
            MakeRundownEvent(1000, 10, "MethodTen"),
            MakeRundownEvent(2000, 10, "MethodTwenty")
        }, pointerSize: 8);

        JsonObject summary = Build(events, stacksById, symbolTable);
        JsonObject cells = summary["drillDown"]["cells"].AsObject();

        // Only one type -> typeIndex 0. bucketWidthMSec=1000, so 100ms is
        // bucket 0 and 1500ms is bucket 1.
        JsonObject bucket0Cell = cells["0:0"].AsObject();
        JsonArray bucket0Stacks = bucket0Cell["stacks"].AsArray();
        Assert.Equal(2, bucket0Stacks.Count);
        Assert.Equal(2, bucket0Cell["distinctStackCount"].GetValue<int>());
        Assert.Equal(350, bucket0Cell["totalBytes"].GetValue<long>());
        Assert.Equal(3, bucket0Cell["totalTickCount"].GetValue<int>());
        // Sorted descending by totalBytes: the aggregated stackId=10 (300)
        // before stackId=20 (50).
        Assert.Equal(300, bucket0Stacks[0]["totalBytes"].GetValue<long>());
        Assert.Equal(2, bucket0Stacks[0]["tickCount"].GetValue<int>());
        Assert.Equal("MethodTen", bucket0Stacks[0]["frames"][0].GetValue<string>());
        Assert.Equal(50, bucket0Stacks[1]["totalBytes"].GetValue<long>());
        Assert.Equal("MethodTwenty", bucket0Stacks[1]["frames"][0].GetValue<string>());

        JsonArray bucket1Stacks = cells["0:1"]["stacks"].AsArray();
        Assert.Single(bucket1Stacks);
        Assert.Equal(999, bucket1Stacks[0]["totalBytes"].GetValue<long>());
        Assert.Equal(999, cells["0:1"]["totalBytes"].GetValue<long>());
    }

    [Fact]
    public void Build_DrillDown_ExcludesTicksThatLandInTheOtherColumn()
    {
        // Nine distinct types exceeds ChartTopTypesLimit (8) - the smallest
        // (Type8, 1 byte) is pushed into "Other" and must not appear in any
        // drillDown cell at all.
        List<AllocationEvent> events = new List<AllocationEvent>();
        for (int typeIndex = 0; typeIndex < 9; ++typeIndex)
        {
            events.Add(MakeEvent($"Type{typeIndex}", 900 - (typeIndex * 100), relativeMSec: 0, stackId: 1));
        }

        Dictionary<int, long[]> stacksById = new Dictionary<int, long[]> { { 1, new long[] { 5000 } } };
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord> { MakeRundownEvent(5000, 10, "Method") }, pointerSize: 8);

        JsonObject summary = Build(events, stacksById, symbolTable);
        JsonObject cells = summary["drillDown"]["cells"].AsObject();

        // Eight real types -> eight cells ("0:0".."7:0"), nothing for Type8.
        Assert.Equal(8, cells.Count);

        int totalDrillDownTickCount = 0;
        foreach (KeyValuePair<string, JsonNode> cellEntry in cells)
        {
            foreach (JsonNode stackEntry in cellEntry.Value["stacks"].AsArray())
            {
                totalDrillDownTickCount += stackEntry["tickCount"].GetValue<int>();
            }
        }

        // 9 ticks total, 1 excluded (the Other-column type) - matches
        // totalTickCount (9) minus exactly the one dropped tick.
        Assert.Equal(9, summary["totalTickCount"].GetValue<int>());
        Assert.Equal(8, totalDrillDownTickCount);
    }

    [Fact]
    public void Build_DrillDown_GroupsTicksWithNoCapturedStackUnderAPlaceholder()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("TypeA", 100, relativeMSec: 0, stackId: 0),      // StackId 0 == "no stack"
            MakeEvent("TypeA", 200, relativeMSec: 0, stackId: 999)     // not in stacksById
        };

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8);
        JsonObject summary = Build(events, new Dictionary<int, long[]>(), symbolTable);

        JsonArray cellStacks = summary["drillDown"]["cells"]["0:0"]["stacks"].AsArray();

        Assert.Single(cellStacks);
        Assert.Equal(300, cellStacks[0]["totalBytes"].GetValue<long>());
        Assert.Equal(2, cellStacks[0]["tickCount"].GetValue<int>());
        Assert.Equal("<no stack captured>", cellStacks[0]["frames"][0].GetValue<string>());
    }

    [Fact]
    public void Build_DrillDown_CapsStacksPerCellAtFiftyKeepingTheLargestByBytes()
    {
        List<AllocationEvent> events = new List<AllocationEvent>();
        Dictionary<int, long[]> stacksById = new Dictionary<int, long[]>();

        for (int stackId = 1; stackId <= 60; ++stackId)
        {
            // Descending bytes by id, so the top 50 kept are ids 1-50.
            events.Add(MakeEvent("TypeA", amount: 1000 - stackId, relativeMSec: 0, stackId: stackId));
            stacksById[stackId] = new long[] { stackId * 100 };
        }

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8);
        JsonObject summary = Build(events, stacksById, symbolTable);

        JsonObject cell = summary["drillDown"]["cells"]["0:0"].AsObject();
        JsonArray cellStacks = cell["stacks"].AsArray();

        Assert.Equal(50, cellStacks.Count);
        // Largest kept (stackId=1, amount=999) must be first (descending sort).
        Assert.Equal(999, cellStacks[0]["totalBytes"].GetValue<long>());
        // Smallest kept must still beat every dropped one (stackId 51-60,
        // amounts 949 down to 940).
        long smallestKept = cellStacks[cellStacks.Count - 1]["totalBytes"].GetValue<long>();
        Assert.True(smallestKept >= 950, $"Expected the smallest kept stack to still be >= 950, got {smallestKept}");

        // The whole point of shipping totalBytes/totalTickCount/
        // distinctStackCount alongside the capped array: they must reflect
        // ALL 60 distinct stacks, not just the 50 that made the cut - this
        // is what lets a consumer's percentages agree with the chart bar
        // even when a cell has more distinct call stacks than the cap.
        long trueTotalBytes = 0;
        for (int stackId = 1; stackId <= 60; ++stackId)
        {
            trueTotalBytes += 1000 - stackId;
        }

        Assert.Equal(60, cell["distinctStackCount"].GetValue<int>());
        Assert.Equal(60, cell["totalTickCount"].GetValue<int>());
        Assert.Equal(trueTotalBytes, cell["totalBytes"].GetValue<long>());

        long summedCappedStacksOnly = 0;
        foreach (JsonNode stackEntry in cellStacks)
        {
            summedCappedStacksOnly += stackEntry["totalBytes"].GetValue<long>();
        }

        Assert.True(summedCappedStacksOnly < cell["totalBytes"].GetValue<long>(),
            "Summing only the capped stacks array should undercount the true cell total once the cap is exceeded.");
    }

    [Fact]
    public void Build_DrillDown_CellTotalsReconcileExactlyWithTypeTimelineBelowTheCap()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("TypeA", 100, relativeMSec: 0, stackId: 1),
            MakeEvent("TypeA", 250, relativeMSec: 0, stackId: 2),
            MakeEvent("TypeB", 500, relativeMSec: 1200, stackId: 1)
        };

        Dictionary<int, long[]> stacksById = new Dictionary<int, long[]> { { 1, new long[] { 100 } }, { 2, new long[] { 200 } } };
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8);

        JsonObject summary = Build(events, stacksById, symbolTable);
        JsonObject typeTimeline = summary["typeTimeline"].AsObject();
        JsonObject cells = summary["drillDown"]["cells"].AsObject();

        // TypeA is ranked first (350 total bytes > TypeB's 500? No - 350 <
        // 500, so TypeB is actually typeIndex 0 and TypeA is typeIndex 1.
        // Read the ranking back from topTypes instead of assuming it.
        JsonArray topTypes = summary["topTypes"].AsArray();
        int typeAIndex = topTypes[0]["TypeName"].GetValue<string>() == "TypeA" ? 0 : 1;
        int typeBIndex = 1 - typeAIndex;

        long typeACellBytes = SumCellBytes(cells, $"{typeAIndex}:0");
        long typeATimelineBytes = typeTimeline["buckets"][0]["bytesByType"][typeAIndex].GetValue<long>();
        Assert.Equal(typeATimelineBytes, typeACellBytes);
        Assert.Equal(350, typeACellBytes);

        long typeBCellBytes = SumCellBytes(cells, $"{typeBIndex}:1");
        long typeBTimelineBytes = typeTimeline["buckets"][1]["bytesByType"][typeBIndex].GetValue<long>();
        Assert.Equal(typeBTimelineBytes, typeBCellBytes);
        Assert.Equal(500, typeBCellBytes);
    }

    private static long SumCellBytes(JsonObject cells, string cellKey)
    {
        long total = 0;
        foreach (JsonNode stackEntry in cells[cellKey]["stacks"].AsArray())
        {
            total += stackEntry["totalBytes"].GetValue<long>();
        }

        return total;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
