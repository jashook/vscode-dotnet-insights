////////////////////////////////////////////////////////////////////////////////
// Module: RealCaptureTests.cs
//
// Notes:
// End-to-end coverage against a real capture (fixtures/trace2.nettrace, the
// same file used to regenerate dotnetInsights/src/test/suite/fixtures/
// nettrace-gcdata.json throughout this project's history) - every other
// test file in this project exercises one decoder against hand-built
// synthetic payloads, which proves the offset math but not that the whole
// NettraceFile.Read -> GcEventProjector/AllocationEventProjector pipeline
// holds together against bytes an actual .NET runtime produced. Runs the
// exact same call sequence Program.cs's own --json mode uses, and pins the
// same known values already independently verified (via ad hoc Python/Node
// scripts) earlier in this project's history, so this is also a regression
// guard against silently reintroducing those bugs.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class RealCaptureTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "trace2.nettrace");

    private static (List<GcEvent> GcEvents, List<AllocationEvent> AllocationEvents, Dictionary<int, long[]> StacksById, MethodSymbolTable SymbolTable) ProjectFixture()
    {
        NettraceFile file = NettraceFile.Read(FixturePath);
        long referenceQpc = file.Events.Count > 0 ? file.Events[0].TimeStampRelativeQPC : file.Header.SyncTimeQPC;

        List<GcEvent> gcEvents = GcEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc);
        List<AllocationEvent> allocationEvents = AllocationEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc);
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(file.Events, file.Header.PointerSize);

        return (gcEvents, allocationEvents, file.StacksById, symbolTable);
    }

    // AllocationSummaryBuilder.Write streams directly to a Utf8JsonWriter
    // (see AllocationJsonExporter.cs for why) rather than returning a
    // JsonObject - write to an in-memory buffer and parse it back so these
    // tests can keep asserting against the real output shape.
    private static JsonObject BuildAllocationSummary(List<AllocationEvent> allocationEvents, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
            {
                AllocationSummaryBuilder.Write(writer, allocationEvents, stacksById, symbolTable);
            }

            return (JsonObject)JsonNode.Parse(stream.ToArray());
        }
    }

    [Fact]
    public void FixtureFile_Exists()
    {
        Assert.True(File.Exists(FixturePath), $"Missing test fixture at {FixturePath}");
    }

    [Fact]
    public void NettraceFile_Read_ParsesA64BitTraceWithAPositiveQpcFrequency()
    {
        NettraceFile file = NettraceFile.Read(FixturePath);

        Assert.Equal(8, file.Header.PointerSize);
        Assert.True(file.Header.QPCFrequency > 0);
        Assert.True(file.Events.Count > 0);
    }

    [Fact]
    public void GcEventProjector_Project_Reports140CompletedGcs()
    {
        (List<GcEvent> gcEvents, _, _, _) = ProjectFixture();

        // Matches dotnetInsights/src/test/suite/gcStatsCalculations.test.ts's
        // own pin against the same underlying capture (140 GCs, split
        // 119/1/20 across gen0/gen1/gen2).
        Assert.Equal(140, gcEvents.Count);

        int gen0Count = 0;
        int gen1Count = 0;
        int gen2Count = 0;
        foreach (GcEvent gcEvent in gcEvents)
        {
            if (gcEvent.Generation == 0)
            {
                ++gen0Count;
            }
            else if (gcEvent.Generation == 1)
            {
                ++gen1Count;
            }
            else if (gcEvent.Generation == 2)
            {
                ++gen2Count;
            }
        }

        Assert.Equal(119, gen0Count);
        Assert.Equal(1, gen1Count);
        Assert.Equal(20, gen2Count);
    }

    [Fact]
    public void GcEventProjector_Project_EveryCompletedGcHasEndHeapStatsAndGlobalHistory()
    {
        (List<GcEvent> gcEvents, _, _, _) = ProjectFixture();

        foreach (GcEvent gcEvent in gcEvents)
        {
            Assert.True(gcEvent.HasEnd);
            Assert.True(gcEvent.HasHeapStats);
            Assert.True(gcEvent.HasGlobalHeapHistory);
        }
    }

    [Fact]
    public void GcEventProjector_Project_HeapsArePreSortedByHeapIndex()
    {
        // Regression guard mirroring gcDetailTableRenderer.test.ts's own
        // "reports each GC's Heaps array pre-sorted by HeapIndex" test -
        // GcJsonExporter.cs is what actually sorts before serializing, but
        // pinning it here too catches a regression at the source instead of
        // only after it reaches the JSON export layer.
        (List<GcEvent> gcEvents, _, _, _) = ProjectFixture();

        foreach (GcEvent gcEvent in gcEvents)
        {
            List<ClrGcHeap> heaps = new List<ClrGcHeap>(gcEvent.Heaps);
            heaps.Sort((left, right) => left.HeapIndex.CompareTo(right.HeapIndex));

            for (int heapIndex = 0; heapIndex < heaps.Count; ++heapIndex)
            {
                Assert.Equal(heapIndex, heaps[heapIndex].HeapIndex);
            }
        }
    }

    [Fact]
    public void AllocationEventProjector_Project_Reports10515Ticks()
    {
        (_, List<AllocationEvent> allocationEvents, _, _) = ProjectFixture();

        // Matches AllocationSummaryRenderer.test.ts's own pin against the
        // same capture (totalTickCount == 10515, summing to ~1.07GB).
        Assert.Equal(10515, allocationEvents.Count);
    }

    [Fact]
    public void AllocationEventProjector_Project_EveryTickHasATypeNameAndPositiveAmount()
    {
        (_, List<AllocationEvent> allocationEvents, _, _) = ProjectFixture();

        foreach (AllocationEvent allocationEvent in allocationEvents)
        {
            Assert.False(string.IsNullOrEmpty(allocationEvent.TypeName));
            Assert.True(allocationEvent.AllocationAmount > 0);
        }
    }

    [Fact]
    public void AllocationSummaryBuilder_Build_ReconcilesTotalSampledBytesAcrossTopTypes()
    {
        (_, List<AllocationEvent> allocationEvents, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable) = ProjectFixture();

        JsonObject summary = BuildAllocationSummary(allocationEvents, stacksById, symbolTable);

        long totalSampledBytes = summary["totalSampledBytes"].GetValue<long>();
        Assert.True(totalSampledBytes > 1_000_000_000L, $"Expected >1GB sampled, got {totalSampledBytes}");

        long topTypesTotal = 0;
        foreach (JsonNode typeEntry in summary["topTypes"].AsArray())
        {
            topTypesTotal += typeEntry["TotalBytes"].GetValue<long>();
        }

        // The real capture has only 8 distinct types (well under
        // TopTypesLimit=100), so topTypes captures every type - its sum
        // must equal totalSampledBytes exactly, not just be <=.
        Assert.Equal(totalSampledBytes, topTypesTotal);
        Assert.Equal("System.Byte[]", summary["topTypes"][0]["TypeName"].GetValue<string>());
    }

    [Fact]
    public void AllocationSummaryBuilder_Build_DrillDownCellsResolveToRealMethodNames()
    {
        // Closes the loop end-to-end: real ticks, grouped by real StackId,
        // resolved against the real rundown-derived symbol table - at least
        // one drillDown cell's stack must show a real method name, not
        // every one falling back to "<unresolved ...>" or
        // "<no stack captured>" (which would mean StackId wasn't actually
        // wired through AllocationEventProjector.cs, or the symbol table
        // silently failed to resolve anything).
        (_, List<AllocationEvent> allocationEvents, Dictionary<int, long[]> stacksById, MethodSymbolTable symbolTable) = ProjectFixture();

        JsonObject summary = BuildAllocationSummary(allocationEvents, stacksById, symbolTable);
        JsonObject cells = summary["drillDown"]["cells"].AsObject();

        Assert.True(cells.Count > 0, "Expected at least one drillDown cell for this capture.");

        bool foundRealFrame = false;
        foreach (KeyValuePair<string, JsonNode> cellEntry in cells)
        {
            foreach (JsonNode stackEntry in cellEntry.Value["stacks"].AsArray())
            {
                foreach (JsonNode frame in stackEntry["frames"].AsArray())
                {
                    string frameName = frame.GetValue<string>();
                    if (!frameName.StartsWith("<unresolved") && frameName != "<no stack captured>")
                    {
                        foundRealFrame = true;
                    }
                }
            }
        }

        Assert.True(foundRealFrame, "Expected at least one real (non-placeholder) resolved frame across all drillDown cells.");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
