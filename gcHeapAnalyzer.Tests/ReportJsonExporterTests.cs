////////////////////////////////////////////////////////////////////////////////
// Module: ReportJsonExporterTests.cs
//
// Notes:
// Verifies the JSON shape produced by ReportJsonExporter.ToJson matches the
// documented contract in that file's header comment. Tests use a minimal
// hand-built FragmentationReport so each assertion targets exactly one field
// or section without depending on SampleReportGenerator's specific values.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

using DotnetInsights.GcHeapAnalyzer;

using Xunit;

namespace DotnetInsights.GcHeapAnalyzer.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ReportJsonExporterTests
{
    private static FragmentationReport MakeMinimalReport()
    {
        FragmentationReport report = new FragmentationReport();
        report.ProcessId = 9999;
        report.ProcessName = "test-proc";
        report.CaptureTimeUtc = "2026-07-25T12:00:00Z";

        report.Summary = new HeapSummary
        {
            TotalCommittedBytes = 1_048_576,
            TotalObjectBytes    =   786_432,
            TotalFreeBytes      =   262_144,
            FragmentationPct    = 25.0,
            PinnedObjectCount   = 3,
            SegmentCount        = 1
        };

        FreeChunkBucket[] EmptyHistogram() => new FreeChunkBucket[]
        {
            new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,          MaxBytes = 1_023,         Count = 0, TotalBytes = 0 },
            new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,      MaxBytes = 8_191,         Count = 0, TotalBytes = 0 },
            new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,      MaxBytes = 84_999,        Count = 0, TotalBytes = 0 },
            new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,     MaxBytes = 1_048_575,     Count = 0, TotalBytes = 0 },
            new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576,  MaxBytes = long.MaxValue, Count = 0, TotalBytes = 0 }
        };

        report.Generations = new GenerationStats[]
        {
            new GenerationStats { Generation = 0, Label = "Gen0", CommittedBytes = 65536, ObjectBytes = 60000, FreeBytes = 5536, FragmentationPct = 8.4, SegmentCount = 0, FreeChunkCount = 2, Histogram = EmptyHistogram() },
            new GenerationStats { Generation = 1, Label = "Gen1", CommittedBytes = 131072, ObjectBytes = 120000, FreeBytes = 11072, FragmentationPct = 8.4, SegmentCount = 0, FreeChunkCount = 5, Histogram = EmptyHistogram() },
            new GenerationStats
            {
                Generation = 2, Label = "Gen2", CommittedBytes = 524288, ObjectBytes = 360000, FreeBytes = 164288, FragmentationPct = 31.3, SegmentCount = 1, FreeChunkCount = 42,
                Histogram = new FreeChunkBucket[]
                {
                    new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,          MaxBytes = 1_023,         Count = 41, TotalBytes = 33_216 },
                    new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,      MaxBytes = 8_191,         Count = 0,  TotalBytes = 0 },
                    new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,      MaxBytes = 84_999,        Count = 0,  TotalBytes = 0 },
                    new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,     MaxBytes = 1_048_575,     Count = 1,  TotalBytes = 131_072 },
                    new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576,  MaxBytes = long.MaxValue, Count = 0,  TotalBytes = 0 }
                }
            },
            new GenerationStats { Generation = 3, Label = "LOH",  CommittedBytes = 327680, ObjectBytes = 246432, FreeBytes = 81248, FragmentationPct = 24.8, SegmentCount = 0, FreeChunkCount = 7, Histogram = EmptyHistogram() },
            new GenerationStats { Generation = 4, Label = "POH",  CommittedBytes = 0, ObjectBytes = 0, FreeBytes = 0, FragmentationPct = 0.0, SegmentCount = 0, FreeChunkCount = 0, Histogram = EmptyHistogram() }
        };

        report.FreeChunks = new FreeChunkReport
        {
            TotalCount     = 56,
            TotalFreeBytes = 262_144,
            Histogram = new FreeChunkBucket[]
            {
                new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,         MaxBytes = 1_023,      Count = 10, TotalBytes = 5_120 },
                new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,     MaxBytes = 8_191,      Count = 30, TotalBytes = 92_160 },
                new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,     MaxBytes = 84_999,     Count = 14, TotalBytes = 131_072 },
                new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,    MaxBytes = 1_048_575,  Count = 1,  TotalBytes = 98_304 },
                new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576, MaxBytes = long.MaxValue, Count = 1, TotalBytes = 131_072 }
            },
            LargeChunks = new List<LargeFreeChunk>
            {
                new LargeFreeChunk { Address = "0x00007f0000100000", SizeBytes = 131_072, Generation = 2, PrecedingTypeName = "System.Byte[]", PrecedingIsPinned = true,  FollowingTypeName = "System.Byte[]",   FollowingIsPinned = true },
                new LargeFreeChunk { Address = "0x00007f0000200000", SizeBytes = 98_304,  Generation = 3, PrecedingTypeName = "System.Object[]", PrecedingIsPinned = false, FollowingTypeName = "<end of segment>", FollowingIsPinned = false }
            }
        };

        report.Segments = new List<SegmentOccupancy>
        {
            new SegmentOccupancy { Address = "0x00007f0000000000", Generation = 2, CommittedBytes = 524288, LiveBytes = 360000, OccupancyPct = 68.67 }
        };

        report.SegmentMaps = new List<SegmentMap>
        {
            new SegmentMap
            {
                Address = "0x00007f0000000000", Generation = 2,
                Blocks = new List<SegmentBlock>
                {
                    new SegmentBlock { IsGap = false, TypeName = "System.Byte[]", OtherTypeCount = 2, ObjectCount = 5, Bytes = 360000, HasPinnedObject = true },
                    new SegmentBlock { IsGap = true, ObjectCount = 1, Bytes = 164288 }
                }
            }
        };

        report.PinnedObjects = new List<PinnedTypeStat>
        {
            new PinnedTypeStat { TypeName = "System.Byte[]", Generation = 2, Count = 2, TotalBytes = 16_384 },
            new PinnedTypeStat { TypeName = "System.String", Generation = 1, Count = 1, TotalBytes = 512 }
        };

        report.TopLohTypes = new List<TypeStat>
        {
            new TypeStat { TypeName = "System.Byte[]",   Count = 12, TotalBytes = 204_800 },
            new TypeStat { TypeName = "System.Object[]", Count = 3,  TotalBytes = 49_152 }
        };

        report.TopPohTypes = new List<TypeStat>
        {
            new TypeStat { TypeName = "System.Threading.OverlappedData", Count = 5, TotalBytes = 40_960 }
        };

        return report;
    }

    private static JsonObject ParseReport(FragmentationReport report)
    {
        return (JsonObject)JsonNode.Parse(ReportJsonExporter.ToJson(report));
    }

    [Fact]
    public void ToJson_IncludesProcessIdentityFields()
    {
        JsonObject root = ParseReport(MakeMinimalReport());

        Assert.Equal(9999, (int)root["processId"]);
        Assert.Equal("test-proc", (string)root["processName"]);
        Assert.Equal("2026-07-25T12:00:00Z", (string)root["captureTimeUtc"]);
    }

    [Fact]
    public void ToJson_IncludesSummaryWithAllSixFields()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonObject summary = (JsonObject)root["summary"];

        Assert.Equal(1_048_576, (long)summary["totalCommittedBytes"]);
        Assert.Equal(786_432, (long)summary["totalObjectBytes"]);
        Assert.Equal(262_144, (long)summary["totalFreeBytes"]);
        Assert.Equal(25.0, (double)summary["fragmentationPct"]);
        Assert.Equal(3, (int)summary["pinnedObjectCount"]);
        Assert.Equal(1, (int)summary["segmentCount"]);
    }

    [Fact]
    public void ToJson_IncludesGenerationsArrayWithFiveEntries()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray generations = (JsonArray)root["generations"];

        Assert.Equal(5, generations.Count);

        JsonObject gen2 = (JsonObject)generations[2];
        Assert.Equal(2, (int)gen2["generation"]);
        Assert.Equal("Gen2", (string)gen2["label"]);
        Assert.Equal(524288, (long)gen2["committedBytes"]);
        Assert.Equal(360000, (long)gen2["objectBytes"]);
        Assert.Equal(164288, (long)gen2["freeBytes"]);
        Assert.Equal(31.3, (double)gen2["fragmentationPct"]);
        Assert.Equal(1, (int)gen2["segmentCount"]);
        Assert.Equal(42, (int)gen2["freeChunkCount"]);
    }

    [Fact]
    public void ToJson_SerializesUnboundedHistogramBucketMaxAsMinus1()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray histogram = (JsonArray)((JsonObject)root["freeChunks"])["histogram"];

        // Last bucket has MaxBytes = long.MaxValue, must serialize as -1.
        JsonObject lastBucket = (JsonObject)histogram[histogram.Count - 1];
        Assert.Equal(-1, (long)lastBucket["maxBytes"]);
        Assert.Equal("> 1 MB", (string)lastBucket["label"]);
    }

    [Fact]
    public void ToJson_SerializesBoundedHistogramBucketMaxAsActualValue()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray histogram = (JsonArray)((JsonObject)root["freeChunks"])["histogram"];

        JsonObject firstBucket = (JsonObject)histogram[0];
        Assert.Equal(0, (long)firstBucket["minBytes"]);
        Assert.Equal(1_023, (long)firstBucket["maxBytes"]);
        Assert.Equal(10, (long)firstBucket["count"]);
        Assert.Equal(5_120, (long)firstBucket["totalBytes"]);
    }

    [Fact]
    public void ToJson_IncludesLargeChunksWithAddressAndSizeAndGeneration()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray largeChunks = (JsonArray)((JsonObject)root["freeChunks"])["largeChunks"];

        Assert.Equal(2, largeChunks.Count);

        JsonObject firstChunk = (JsonObject)largeChunks[0];
        Assert.Equal("0x00007f0000100000", (string)firstChunk["address"]);
        Assert.Equal(131_072, (long)firstChunk["sizeBytes"]);
        Assert.Equal(2, (int)firstChunk["generation"]);
    }

    // Regression coverage for the Gen2 root-cause investigation this field
    // exists for: a hole bracketed by a pinned object on both sides is a
    // fundamentally different (permanent, non-compactable) problem than one
    // bracketed by ordinary objects - this pins that both the type name and
    // the pinned flag actually reach the JSON on both sides of the gap.
    [Fact]
    public void ToJson_IncludesLargeChunkAdjacencyWithTypeNamesAndPinnedFlags()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray largeChunks = (JsonArray)((JsonObject)root["freeChunks"])["largeChunks"];

        JsonObject pinnedOnBothSides = (JsonObject)largeChunks[0];
        Assert.Equal("System.Byte[]", (string)pinnedOnBothSides["precedingType"]);
        Assert.True((bool)pinnedOnBothSides["precedingIsPinned"]);
        Assert.Equal("System.Byte[]", (string)pinnedOnBothSides["followingType"]);
        Assert.True((bool)pinnedOnBothSides["followingIsPinned"]);

        JsonObject endOfSegment = (JsonObject)largeChunks[1];
        Assert.Equal("System.Object[]", (string)endOfSegment["precedingType"]);
        Assert.False((bool)endOfSegment["precedingIsPinned"]);
        Assert.Equal("<end of segment>", (string)endOfSegment["followingType"]);
        Assert.False((bool)endOfSegment["followingIsPinned"]);
    }

    // Regression coverage for the per-generation histogram this exists for:
    // the aggregate freeChunks.histogram would drown out Gen2's shape under
    // Gen0/Gen1's much higher chunk counts in a real capture, so each
    // generations[] entry carries its own histogram scoped to just that
    // generation.
    [Fact]
    public void ToJson_GenerationsEachIncludeTheirOwnScopedHistogram()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray generations = (JsonArray)root["generations"];

        JsonObject gen2 = (JsonObject)generations[2];
        JsonArray gen2Histogram = (JsonArray)gen2["histogram"];

        Assert.Equal(5, gen2Histogram.Count);

        JsonObject subOneKb = (JsonObject)gen2Histogram[0];
        Assert.Equal(41, (long)subOneKb["count"]);
        Assert.Equal(33_216, (long)subOneKb["totalBytes"]);

        // Gen0's histogram is scoped to Gen0 only - it must not pick up
        // Gen2's bucket values (the two arrays should be independent, not
        // aliased to the same underlying instance).
        JsonObject gen0 = (JsonObject)generations[0];
        JsonObject gen0SubOneKb = (JsonObject)((JsonArray)gen0["histogram"])[0];
        Assert.Equal(0, (long)gen0SubOneKb["count"]);
    }

    [Fact]
    public void ToJson_IncludesSegmentsWithAddressGenerationBytesAndOccupancy()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray segments = (JsonArray)root["segments"];

        Assert.Single(segments);

        JsonObject segment = (JsonObject)segments[0];
        Assert.Equal("0x00007f0000000000", (string)segment["address"]);
        Assert.Equal(2, (int)segment["generation"]);
        Assert.Equal(524288, (long)segment["committedBytes"]);
        Assert.Equal(360000, (long)segment["liveBytes"]);
        Assert.Equal(68.67, (double)segment["occupancyPct"]);
    }

    [Fact]
    public void ToJson_IncludesPinnedObjectsWithTypeNameGenerationCountAndTotalBytes()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray pinnedObjects = (JsonArray)root["pinnedObjects"];

        Assert.Equal(2, pinnedObjects.Count);

        JsonObject firstEntry = (JsonObject)pinnedObjects[0];
        Assert.Equal("System.Byte[]", (string)firstEntry["typeName"]);
        Assert.Equal(2, (int)firstEntry["generation"]);
        Assert.Equal(2, (int)firstEntry["count"]);
        Assert.Equal(16_384, (long)firstEntry["totalBytes"]);
    }

    [Fact]
    public void ToJson_IncludesTopLohTypesWithTypeNameCountAndTotalBytes()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray topLohTypes = (JsonArray)root["topLohTypes"];

        Assert.Equal(2, topLohTypes.Count);

        JsonObject firstEntry = (JsonObject)topLohTypes[0];
        Assert.Equal("System.Byte[]", (string)firstEntry["typeName"]);
        Assert.Equal(12, (int)firstEntry["count"]);
        Assert.Equal(204_800, (long)firstEntry["totalBytes"]);
    }

    // Regression coverage for a real gap: TopPohTypes and TopLohTypes are
    // serialized by the same shared helper (SerializeTypeStats) but are
    // separate lists on FragmentationReport - this pins that topPohTypes
    // isn't accidentally aliased to (or omitted in favor of) topLohTypes.
    [Fact]
    public void ToJson_IncludesTopPohTypesWithTypeNameCountAndTotalBytes()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray topPohTypes = (JsonArray)root["topPohTypes"];

        Assert.Single(topPohTypes);

        JsonObject firstEntry = (JsonObject)topPohTypes[0];
        Assert.Equal("System.Threading.OverlappedData", (string)firstEntry["typeName"]);
        Assert.Equal(5, (int)firstEntry["count"]);
        Assert.Equal(40_960, (long)firstEntry["totalBytes"]);
    }

    [Fact]
    public void ToJson_IncludesSegmentMapsWithBlocksAndPinnedFlag()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonArray segmentMaps = (JsonArray)root["segmentMaps"];

        Assert.Single(segmentMaps);

        JsonObject segmentMap = (JsonObject)segmentMaps[0];
        Assert.Equal("0x00007f0000000000", (string)segmentMap["address"]);
        Assert.Equal(2, (int)segmentMap["generation"]);

        JsonArray blocks = (JsonArray)segmentMap["blocks"];
        Assert.Equal(2, blocks.Count);

        JsonObject liveBlock = (JsonObject)blocks[0];
        Assert.False((bool)liveBlock["isGap"]);
        Assert.Equal("System.Byte[]", (string)liveBlock["typeName"]);
        Assert.Equal(2, (int)liveBlock["otherTypeCount"]);
        Assert.Equal(5, (int)liveBlock["objectCount"]);
        Assert.Equal(360000, (long)liveBlock["bytes"]);
        Assert.True((bool)liveBlock["hasPinnedObject"]);

        JsonObject gapBlock = (JsonObject)blocks[1];
        Assert.True((bool)gapBlock["isGap"]);
        Assert.Equal(164288, (long)gapBlock["bytes"]);
    }

    [Fact]
    public void ToJson_FreeChunksIncludesTotalCountAndTotalFreeBytes()
    {
        JsonObject root = ParseReport(MakeMinimalReport());
        JsonObject freeChunks = (JsonObject)root["freeChunks"];

        Assert.Equal(56, (long)freeChunks["totalCount"]);
        Assert.Equal(262_144, (long)freeChunks["totalFreeBytes"]);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapAnalyzer.Tests)
