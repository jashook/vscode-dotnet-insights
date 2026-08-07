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

using System;
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
    // AllocationJsonExporter.cs now aggregates stacks by AllocationEvent.Stack's
    // own array reference, not by a raw StackId int (see EventRecord.cs's own
    // comment on why - StackId values are recyclable in the real wire format).
    // This cache is what lets these tests keep using a readable synthetic int
    // "stackId" while still giving two MakeEvent calls for the "same" stack
    // the exact same array instance, which is what real aggregation now
    // requires - EventBlock.cs achieves the same thing in production by
    // resolving against the shared StacksById dictionary at parse time.
    // stackId 0 always means "no stack" (Array.Empty<long>()), matching the
    // real pipeline's own convention.
    private static readonly Dictionary<int, long[]> stackCache = new Dictionary<int, long[]>();

    // address = stackId * 100 is an arbitrary but stable, distinct-per-id
    // convention - tests that need MethodSymbolTable to resolve a stack's
    // frame to a real name build their rundown data at this same address
    // (see MakeRundownEvent's call sites below).
    private static long[] StackFor(int stackId)
    {
        if (stackId == 0)
        {
            return Array.Empty<long>();
        }

        long[] stack;
        if (!stackCache.TryGetValue(stackId, out stack))
        {
            stack = new long[] { stackId * 100 };
            stackCache[stackId] = stack;
        }

        return stack;
    }

    private static AllocationEvent MakeEvent(string typeName, long amount, double relativeMSec, int stackId, GCAllocationKind kind = GCAllocationKind.Small)
    {
        return new AllocationEvent(default, relativeMSec, amount, kind, typeName, heapIndex: 0, stack: StackFor(stackId));
    }

    // AllocationSummaryBuilder.Write streams directly to a Utf8JsonWriter
    // (see AllocationJsonExporter.cs for why) rather than returning a
    // JsonObject - write to an in-memory buffer and parse it back so these
    // tests can keep asserting against the real output shape.
    private static JsonObject Build(List<AllocationEvent> events, MethodSymbolTable symbolTable)
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
                    AllocationSummaryBuilder.Write(writer, events, symbolTable, ticksBinaryPath);
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

    // frames arrays hold integer indices into the shared
    // allocationSummary.methodNames pool now (see AllocationJsonExporter.cs's
    // MethodNameInterner), not raw strings - resolves one frame back to its
    // name the same way a real consumer would.
    private static string ResolveFrameName(JsonObject summary, JsonNode frameIndexNode)
    {
        return summary["methodNames"][frameIndexNode.GetValue<int>()].GetValue<string>();
    }

    private static EventRecord MakeRundownEvent(long startAddress, int size, string name)
    {
        byte[] payload = new PayloadBuilder()
            .WriteAddress(1, 8).WriteAddress(2, 8).WriteAddress(startAddress, 8)
            .WriteInt32(size).WriteInt32(0x06000001).WriteInt32(0)
            .WriteUnicodeString("").WriteUnicodeString(name).WriteUnicodeString("sig")
            .ToArray();

        return new EventRecord("Microsoft-Windows-DotNETRuntimeRundown", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stack: Array.Empty<long>(), fields: null, payload, payloadOffset: 0, payload.Length);
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

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>
        {
            MakeRundownEvent(1000, 10, "MethodTen"),
            MakeRundownEvent(2000, 10, "MethodTwenty")
        }, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);

        JsonObject summary = Build(events, symbolTable);
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
        Assert.Equal("MethodTen", ResolveFrameName(summary, bucket0Stacks[0]["frames"][0]));
        Assert.Equal(50, bucket0Stacks[1]["totalBytes"].GetValue<long>());
        Assert.Equal("MethodTwenty", ResolveFrameName(summary, bucket0Stacks[1]["frames"][0]));

        JsonArray bucket1Stacks = cells["0:1"]["stacks"].AsArray();
        Assert.Single(bucket1Stacks);
        Assert.Equal(999, bucket1Stacks[0]["totalBytes"].GetValue<long>());
        Assert.Equal(999, cells["0:1"]["totalBytes"].GetValue<long>());
    }

    [Fact]
    public void Build_DrillDown_FoldsDistinctStacksSharingTheSameLeafFrameIntoOneEntry()
    {
        // Two genuinely distinct full call stacks (different callers) that
        // share the same leaf (immediate allocating) frame - see
        // AllocationJsonExporter.FoldByLeafFrame's own doc comment for why
        // ranking/capping raw full stacks directly (the original design)
        // could silently drop a large but diffuse real allocator whose
        // bytes were spread across many slightly-different call paths, none
        // individually big enough to make the cap.
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            new AllocationEvent(timestamp: default, relativeMSec: 100, allocationAmount: 300, allocationKind: GCAllocationKind.Small, typeName: "TypeA", heapIndex: 0, stack: new long[] { 1000, 2000 }),
            new AllocationEvent(timestamp: default, relativeMSec: 100, allocationAmount: 700, allocationKind: GCAllocationKind.Small, typeName: "TypeA", heapIndex: 0, stack: new long[] { 1000, 3000 })
        };

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>
        {
            MakeRundownEvent(1000, 10, "SharedLeaf"),
            MakeRundownEvent(2000, 10, "CallerA"),
            MakeRundownEvent(3000, 10, "CallerB")
        }, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);

        JsonObject summary = Build(events, symbolTable);
        JsonObject cell = summary["drillDown"]["cells"]["0:0"].AsObject();
        JsonArray cellStacks = cell["stacks"].AsArray();

        // Two distinct full stacks, but one folded entry - the two callers
        // are genuinely different, only their shared leaf makes them fold.
        Assert.Single(cellStacks);
        Assert.Equal(1000, cellStacks[0]["totalBytes"].GetValue<long>());
        Assert.Equal(2, cellStacks[0]["tickCount"].GetValue<int>());
        Assert.Equal(2, cellStacks[0]["distinctStackCount"].GetValue<int>());
        Assert.Equal("SharedLeaf", ResolveFrameName(summary, cellStacks[0]["frames"][0]));

        // The true cell-level totals (raw, pre-fold distinct stack count)
        // are unaffected by folding - both real distinct stacks still count.
        Assert.Equal(2, cell["distinctStackCount"].GetValue<int>());
        Assert.Equal(1000, cell["totalBytes"].GetValue<long>());
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

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord> { MakeRundownEvent(100, 10, "Method") }, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);

        JsonObject summary = Build(events, symbolTable);
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
        // Both events have no captured stack (Array.Empty<long>()) - real
        // stack resolution happens once, eagerly, at parse time now (see
        // EventBlock.cs), so there's no "dangling id not found in a lookup
        // table" case left at this layer to construct separately; every
        // AllocationEvent's Stack is already whatever it's ever going to be.
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("TypeA", 100, relativeMSec: 0, stackId: 0),
            MakeEvent("TypeA", 200, relativeMSec: 0, stackId: 0)
        };

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
        JsonObject summary = Build(events, symbolTable);

        JsonArray cellStacks = summary["drillDown"]["cells"]["0:0"]["stacks"].AsArray();

        Assert.Single(cellStacks);
        Assert.Equal(300, cellStacks[0]["totalBytes"].GetValue<long>());
        Assert.Equal(2, cellStacks[0]["tickCount"].GetValue<int>());
        Assert.Equal("<no stack captured>", ResolveFrameName(summary, cellStacks[0]["frames"][0]));
    }

    [Fact]
    public void Build_DrillDown_CapsStacksPerCellAtFiftyKeepingTheLargestByBytes()
    {
        List<AllocationEvent> events = new List<AllocationEvent>();

        for (int stackId = 1; stackId <= 60; ++stackId)
        {
            // Descending bytes by id, so the top 50 kept are ids 1-50.
            events.Add(MakeEvent("TypeA", amount: 1000 - stackId, relativeMSec: 0, stackId: stackId));
        }

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
        JsonObject summary = Build(events, symbolTable);

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

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);

        JsonObject summary = Build(events, symbolTable);
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

    // Confirms a specific, explicitly requested guarantee: when the webview's
    // "LOH Only" filter is active, the drill-down stacks it shows must come
    // only from Large-kind ticks - not a mix of Small/Large stacks for the
    // same type. WriteTypeBreakdown builds "loh"'s drillDown/typeDrillDown
    // from a Large-kind-filtered event list (see AllocationJsonExporter.cs's
    // Write), so a stack that only ever appears on Small-kind ticks for this
    // type must be entirely absent from loh.drillDown/loh.typeDrillDown,
    // even though it's present in the unfiltered top-level drillDown.
    [Fact]
    public void Build_LohDrillDownOnlyIncludesStacksFromLargeKindTicks()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            // stackId 1: only ever a Small-kind tick for TypeA - must not
            // appear anywhere under "loh".
            MakeEvent("TypeA", 100, relativeMSec: 0, stackId: 1, kind: GCAllocationKind.Small),
            // stackId 2: a Large-kind tick for TypeA - must appear under "loh".
            MakeEvent("TypeA", 5000, relativeMSec: 0, stackId: 2, kind: GCAllocationKind.Large)
        };

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);

        JsonObject summary = Build(events, symbolTable);

        // Unfiltered (top-level) drillDown sees both stacks.
        JsonObject allCell = summary["drillDown"]["cells"]["0:0"].AsObject();
        Assert.Equal(2, allCell["distinctStackCount"].GetValue<int>());

        // "loh" drillDown/typeDrillDown must only see the Large-kind stack.
        JsonObject loh = summary["loh"].AsObject();
        JsonObject lohCell = loh["drillDown"]["cells"]["0:0"].AsObject();
        Assert.Equal(1, lohCell["distinctStackCount"].GetValue<int>());
        Assert.Equal(5000, lohCell["totalBytes"].GetValue<long>());

        JsonArray lohStacks = lohCell["stacks"].AsArray();
        Assert.Single(lohStacks);
        Assert.Equal(5000, lohStacks[0]["totalBytes"].GetValue<long>());

        JsonArray lohTypeDrillDown = loh["typeDrillDown"].AsArray();
        Assert.Single(lohTypeDrillDown);
        Assert.Equal(5000, lohTypeDrillDown[0]["totalBytes"].GetValue<long>());
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
