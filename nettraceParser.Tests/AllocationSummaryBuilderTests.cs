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

using System;
using System.Buffers.Binary;
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
        return new AllocationEvent(default, relativeMSec, amount, kind, typeName, heapIndex: 0, stackId: 0);
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
    //
    // ticks is now a binary sidecar file (see WriteTicks), not inline JSON -
    // this helper reads that file back and reconstructs the old
    // [{RelativeMSec, AllocationAmount}, ...] JsonArray shape in place of
    // the small {format, recordCount, bytesPerRecord} descriptor object
    // Write() actually emits, so every existing ticks[...] assertion below
    // needed no changes.
    private static JsonObject Build(List<AllocationEvent> events)
    {
        string ticksBinaryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");

        try
        {
            JsonObject summary;

            using (MemoryStream stream = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
                {
                    AllocationSummaryBuilder.Write(writer, events, EmptyStacksById(), EmptySymbolTable(), ticksBinaryPath);
                }

                summary = (JsonObject)JsonNode.Parse(stream.ToArray());
            }

            summary["ticks"] = ReadTicksBinaryAsJsonArray(ticksBinaryPath);
            return summary;
        }
        finally
        {
            if (File.Exists(ticksBinaryPath))
            {
                File.Delete(ticksBinaryPath);
            }
        }
    }

    // Builds real JSON text and parses it back (rather than assembling a
    // JsonArray directly via JsonValue.Create) so the resulting nodes are
    // JsonElement-backed exactly like every other node in the parsed
    // summary - matches existing assertions like ticks[0]["RelativeMSec"]
    // .GetValue<double>() against a value this format stores as an integer,
    // which a CLR-value-backed JsonValue<int> does not support widening for
    // the same way a parsed JsonElement does.
    private static JsonArray ReadTicksBinaryAsJsonArray(string ticksBinaryPath)
    {
        byte[] bytes = File.ReadAllBytes(ticksBinaryPath);

        using (MemoryStream stream = new MemoryStream())
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();

                for (int offset = 0; offset < bytes.Length; offset += 12)
                {
                    int relativeMSec = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
                    long allocationAmount = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 4, 8));

                    writer.WriteStartObject();
                    writer.WriteNumber("RelativeMSec", relativeMSec);
                    writer.WriteNumber("AllocationAmount", allocationAmount);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            return (JsonArray)JsonNode.Parse(stream.ToArray());
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

    // "loh" mirrors the top-level totalSampledBytes/topTypes/typeTimeline/
    // drillDown/typeDrillDown shape exactly (see WriteTypeBreakdown), scoped
    // to AllocationKind.Large ticks only - these tests pin that the filter
    // actually excludes Small/Pinned ticks rather than aliasing the
    // top-level view, and that byte totals reconcile against only the
    // included ticks.
    [Fact]
    public void Build_LohSectionOnlyIncludesLargeKindTicks()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("Small.Type", 100, GCAllocationKind.Small, 0),
            MakeEvent("Pinned.Type", 200, GCAllocationKind.Pinned, 1),
            MakeEvent("Big.Type", 5000, GCAllocationKind.Large, 2),
            MakeEvent("Big.Type", 3000, GCAllocationKind.Large, 3)
        };

        JsonObject summary = Build(events);
        JsonObject loh = summary["loh"].AsObject();

        Assert.Equal(8000, loh["totalSampledBytes"].GetValue<long>());
        Assert.Equal(2, loh["totalTickCount"].GetValue<int>());
        Assert.Equal(1, loh["distinctTypeCount"].GetValue<int>());

        JsonArray lohTopTypes = loh["topTypes"].AsArray();
        Assert.Single(lohTopTypes);
        Assert.Equal("Big.Type", lohTopTypes[0]["TypeName"].GetValue<string>());
        Assert.Equal(8000, lohTopTypes[0]["TotalBytes"].GetValue<long>());

        // The top-level (unfiltered) view must be unaffected by loh's presence.
        Assert.Equal(8300, summary["totalSampledBytes"].GetValue<long>());
        Assert.Equal(4, summary["totalTickCount"].GetValue<int>());
        Assert.Equal(3, summary["distinctTypeCount"].GetValue<int>());
    }

    [Fact]
    public void Build_LohSectionIsEmptyButPresentWhenNoLargeKindTicksExist()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("Small.Type", 100, GCAllocationKind.Small, 0),
            MakeEvent("Pinned.Type", 200, GCAllocationKind.Pinned, 1)
        };

        JsonObject summary = Build(events);
        JsonObject loh = summary["loh"].AsObject();

        Assert.Equal(0, loh["totalSampledBytes"].GetValue<long>());
        Assert.Equal(0, loh["totalTickCount"].GetValue<int>());
        Assert.Empty(loh["topTypes"].AsArray());
    }

    // Pins the wire format itself (WriteTicks writes a binary sidecar file,
    // not inline JSON - see its own comment for why), bypassing the Build()
    // helper's reconstruction so the raw {format, recordCount,
    // bytesPerRecord} descriptor and the sidecar file's actual bytes are
    // both checked directly, the same way PayloadReaderTests.cs/
    // ClrGcTypesTests.cs pin other byte-level formats in this codebase.
    [Fact]
    public void Build_WritesTicksDescriptorAndMatchingBinarySidecarFile()
    {
        List<AllocationEvent> events = new List<AllocationEvent>
        {
            MakeEvent("A", 12345, GCAllocationKind.Small, 10.4),
            MakeEvent("B", 67890, GCAllocationKind.Large, 20.6)
        };

        string ticksBinaryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");

        try
        {
            JsonObject summary;
            using (MemoryStream stream = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
                {
                    AllocationSummaryBuilder.Write(writer, events, EmptyStacksById(), EmptySymbolTable(), ticksBinaryPath);
                }

                summary = (JsonObject)JsonNode.Parse(stream.ToArray());
            }

            JsonObject ticksDescriptor = summary["ticks"].AsObject();
            Assert.Equal("binary-v1", ticksDescriptor["format"].GetValue<string>());
            Assert.Equal(2, ticksDescriptor["recordCount"].GetValue<int>());
            Assert.Equal(12, ticksDescriptor["bytesPerRecord"].GetValue<int>());

            byte[] bytes = File.ReadAllBytes(ticksBinaryPath);
            Assert.Equal(24, bytes.Length);

            // Sorted ascending by RelativeMSec (10.4 rounds to 10, 20.6 rounds to 21).
            Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)));
            Assert.Equal(12345L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(4, 8)));
            Assert.Equal(21, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4)));
            Assert.Equal(67890L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16, 8)));
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
