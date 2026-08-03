////////////////////////////////////////////////////////////////////////////////
// Module: TypeDrillDownBuilderTests.cs
//
// Notes:
// AllocationSummaryBuilder's typeDrillDown output (WriteTypeDrillDown, a
// private method - exercised here only through the public Write entry
// point, same as topTypes/drillDown already are) is what the global ranked
// "top allocating types" table links to: for a given type, every resolved
// call stack that allocated it *anywhere in the whole capture*, unlike
// drillDown (DrillDownBuilderTests.cs), which is scoped to one (type,
// 1-second bucket) chart cell. The behaviors that actually distinguish it
// from drillDown get dedicated coverage: stacks from different time
// buckets must merge into one entry (that's the whole point - "the full
// picture", not one slice of it), and every one of the (up to
// TopTypesLimit=100) globally-ranked types must be covered, not just the
// chart's own top ChartTopTypesLimit=8.
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

public class TypeDrillDownBuilderTests
{
    private static AllocationEvent MakeEvent(string typeName, long amount, double relativeMSec, int stackId)
    {
        return new AllocationEvent(default, relativeMSec, amount, GCAllocationKind.Small, typeName, heapIndex: 0, stackId: stackId);
    }

    private static EventRecord MakeRundownEvent(long startAddress, int size, string name)
    {
        byte[] payload = new PayloadBuilder()
            .WriteAddress(1, 8).WriteAddress(2, 8).WriteAddress(startAddress, 8)
            .WriteInt32(size).WriteInt32(0x06000001).WriteInt32(0)
            .WriteUnicodeString("").WriteUnicodeString(name).WriteUnicodeString("sig")
            .ToArray();

        return new EventRecord("Microsoft-Windows-DotNETRuntimeRundown", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stackId: 0, fields: null, payload, payloadOffset: 0, payload.Length);
    }

    // AllocationSummaryBuilder.Write streams directly to a Utf8JsonWriter
    // (see AllocationJsonExporter.cs for why) rather than returning a
    // JsonObject - write to an in-memory buffer and parse it back so these
    // tests can keep asserting against the real output shape.
    private static JsonObject Build(List<AllocationEvent> events, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
        // ticks is now a binary sidecar file (see AllocationJsonExporter.cs's
        // WriteTicks) - this file's tests don't assert on ticks directly, so
        // the temp file just needs a valid path to write to and cleanup.
        string ticksBinaryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");

        try
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
                {
                    AllocationSummaryBuilder.Write(writer, events, stacksById, symbolTable, ticksBinaryPath);
                }

                return (JsonObject)JsonNode.Parse(stream.ToArray());
            }
        }
        finally
        {
            if (File.Exists(ticksBinaryPath))
            {
                File.Delete(ticksBinaryPath);
            }
        }
    }

    [Fact]
    public void TypeDrillDown_IsAParallelArrayToTopTypes()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("TypeA", 500, relativeMSec: 0, stackId: 1),
            MakeEvent("TypeB", 200, relativeMSec: 0, stackId: 1)
        };

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
        JsonObject summary = Build(events, new Dictionary<int, long[]>(), symbolTable);

        JsonArray topTypes = summary["topTypes"].AsArray();
        JsonArray typeDrillDown = summary["typeDrillDown"].AsArray();

        Assert.Equal(topTypes.Count, typeDrillDown.Count);
        Assert.Equal(2, typeDrillDown.Count);
    }

    [Fact]
    public void TypeDrillDown_MergesTicksFromDifferentTimeBucketsIntoOneStackEntry()
    {
        // Same type, same stack, but 5 seconds (5 buckets) apart -
        // drillDown (per-cell) would put these in two separate cells;
        // typeDrillDown must merge them into a single stack entry, since
        // it's not scoped to any one bucket.
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("TypeA", 100, relativeMSec: 0, stackId: 1),
            MakeEvent("TypeA", 250, relativeMSec: 5000, stackId: 1)
        };

        Dictionary<int, long[]> stacksById = new Dictionary<int, long[]> { { 1, new long[] { 1000 } } };
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord> { MakeRundownEvent(1000, 10, "Method") }, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);

        JsonObject summary = Build(events, stacksById, symbolTable);
        JsonObject typeAEntry = summary["typeDrillDown"][0].AsObject();
        JsonArray stacksForTypeA = typeAEntry["stacks"].AsArray();

        Assert.Single(stacksForTypeA);
        Assert.Equal(350, stacksForTypeA[0]["totalBytes"].GetValue<long>());
        Assert.Equal(2, stacksForTypeA[0]["tickCount"].GetValue<int>());
        Assert.Equal(350, typeAEntry["totalBytes"].GetValue<long>());
        Assert.Equal(2, typeAEntry["totalTickCount"].GetValue<int>());
        Assert.Equal(1, typeAEntry["distinctStackCount"].GetValue<int>());

        // Contrast: the same two ticks landed in two separate drillDown cells.
        JsonObject cells = summary["drillDown"]["cells"].AsObject();
        Assert.Equal(2, cells.Count);
    }

    [Fact]
    public void TypeDrillDown_CoversEveryRankedTypeNotJustTheChartsTopEight()
    {
        // ChartTopTypesLimit is 8 (private const, AllocationJsonExporter.cs) -
        // nine distinct types here means the 9th has no chart column and no
        // drillDown cell, but must still get its own typeDrillDown entry.
        List<AllocationEvent> events = new List<AllocationEvent>();
        Dictionary<int, long[]> stacksById = new Dictionary<int, long[]>();
        for (int typeIndex = 0; typeIndex < 9; ++typeIndex)
        {
            events.Add(MakeEvent($"Type{typeIndex}", 900 - (typeIndex * 100), relativeMSec: 0, stackId: typeIndex + 1));
            stacksById[typeIndex + 1] = new long[] { (typeIndex + 1) * 1000 };
        }

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
        JsonObject summary = Build(events, stacksById, symbolTable);

        JsonArray typeDrillDown = summary["typeDrillDown"].AsArray();
        Assert.Equal(9, typeDrillDown.Count);

        // The 9th-ranked type (Type8, smallest bytes) has no drillDown cell...
        JsonObject cells = summary["drillDown"]["cells"].AsObject();
        Assert.Equal(8, cells.Count);

        // ...but its typeDrillDown entry (index 8, last-ranked) is still populated.
        JsonArray lastTypeStacks = typeDrillDown[8]["stacks"].AsArray();
        Assert.Single(lastTypeStacks);
        Assert.Equal(100, lastTypeStacks[0]["totalBytes"].GetValue<long>());
    }

    [Fact]
    public void TypeDrillDown_ResolvesFramesLeafFirst()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("TypeA", 500, relativeMSec: 0, stackId: 1)
        };

        Dictionary<int, long[]> stacksById = new Dictionary<int, long[]> { { 1, new long[] { 1000, 2000 } } };
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>
        {
            MakeRundownEvent(1000, 10, "Leaf"),
            MakeRundownEvent(2000, 10, "Caller")
        }, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);

        JsonObject summary = Build(events, stacksById, symbolTable);
        JsonArray methodNames = summary["methodNames"].AsArray();
        JsonArray frames = summary["typeDrillDown"][0]["stacks"][0]["frames"].AsArray();

        Assert.Equal("Leaf", methodNames[frames[0].GetValue<int>()].GetValue<string>());
        Assert.Equal("Caller", methodNames[frames[1].GetValue<int>()].GetValue<string>());
    }

    [Fact]
    public void TypeDrillDown_GroupsTicksWithNoCapturedStackUnderAPlaceholder()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("TypeA", 100, relativeMSec: 0, stackId: 0),      // StackId 0 == "no stack"
            MakeEvent("TypeA", 200, relativeMSec: 3000, stackId: 999)  // not in stacksById
        };

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
        JsonObject summary = Build(events, new Dictionary<int, long[]>(), symbolTable);

        JsonArray stacks = summary["typeDrillDown"][0]["stacks"].AsArray();

        Assert.Single(stacks);
        Assert.Equal(300, stacks[0]["totalBytes"].GetValue<long>());
        Assert.Equal(2, stacks[0]["tickCount"].GetValue<int>());

        int frameIndex = stacks[0]["frames"][0].GetValue<int>();
        Assert.Equal("<no stack captured>", summary["methodNames"][frameIndex].GetValue<string>());
    }

    [Fact]
    public void TypeDrillDown_CapsStacksPerTypeAtOneHundredKeepingTheLargestByBytesButTotalsStillReflectAllOfThem()
    {
        List<AllocationEvent> events = new List<AllocationEvent>();
        Dictionary<int, long[]> stacksById = new Dictionary<int, long[]>();

        for (int stackId = 1; stackId <= 110; ++stackId)
        {
            // Descending bytes by id, so the top 100 kept are ids 1-100.
            events.Add(MakeEvent("TypeA", amount: 1000 - stackId, relativeMSec: 0, stackId: stackId));
            stacksById[stackId] = new long[] { stackId * 100 };
        }

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
        JsonObject summary = Build(events, stacksById, symbolTable);

        JsonObject typeAEntry = summary["typeDrillDown"][0].AsObject();
        JsonArray stacks = typeAEntry["stacks"].AsArray();

        Assert.Equal(100, stacks.Count);
        Assert.Equal(999, stacks[0]["totalBytes"].GetValue<long>());
        long smallestKept = stacks[stacks.Count - 1]["totalBytes"].GetValue<long>();
        Assert.True(smallestKept >= 899, $"Expected the smallest kept stack to still be >= 899, got {smallestKept}");

        // Same reconciliation guarantee as the per-cell cap
        // (DrillDownBuilderTests.Build_DrillDown_CapsStacksPerCellAtFiftyKeepingTheLargestByBytes) -
        // topTypes[0].TotalBytes (the number the global table and its chart
        // bars are built from) must match typeDrillDown[0].totalBytes
        // exactly, even though the "stacks" array itself only lists the top
        // 100 of the 110 distinct call stacks that produced it.
        Assert.Equal(110, typeAEntry["distinctStackCount"].GetValue<int>());
        Assert.Equal(110, typeAEntry["totalTickCount"].GetValue<int>());

        long trueTotalBytes = 0;
        for (int stackId = 1; stackId <= 110; ++stackId)
        {
            trueTotalBytes += 1000 - stackId;
        }

        long topTypesTotalBytes = summary["topTypes"][0]["TotalBytes"].GetValue<long>();
        Assert.Equal(trueTotalBytes, topTypesTotalBytes);
        Assert.Equal(topTypesTotalBytes, typeAEntry["totalBytes"].GetValue<long>());
    }

    [Fact]
    public void TypeDrillDown_IsEmptyArrayForEmptyInputWithoutThrowing()
    {
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
        JsonObject summary = Build(new List<AllocationEvent>(), new Dictionary<int, long[]>(), symbolTable);

        Assert.Empty(summary["typeDrillDown"].AsArray());
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
