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

        report.Generations = new GenerationStats[]
        {
            new GenerationStats { Generation = 0, Label = "Gen0", CommittedBytes = 65536, ObjectBytes = 60000, FreeBytes = 5536, FragmentationPct = 8.4, SegmentCount = 0, FreeChunkCount = 2 },
            new GenerationStats { Generation = 1, Label = "Gen1", CommittedBytes = 131072, ObjectBytes = 120000, FreeBytes = 11072, FragmentationPct = 8.4, SegmentCount = 0, FreeChunkCount = 5 },
            new GenerationStats { Generation = 2, Label = "Gen2", CommittedBytes = 524288, ObjectBytes = 360000, FreeBytes = 164288, FragmentationPct = 31.3, SegmentCount = 1, FreeChunkCount = 42 },
            new GenerationStats { Generation = 3, Label = "LOH",  CommittedBytes = 327680, ObjectBytes = 246432, FreeBytes = 81248, FragmentationPct = 24.8, SegmentCount = 0, FreeChunkCount = 7 },
            new GenerationStats { Generation = 4, Label = "POH",  CommittedBytes = 0, ObjectBytes = 0, FreeBytes = 0, FragmentationPct = 0.0, SegmentCount = 0, FreeChunkCount = 0 }
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
                new LargeFreeChunk { Address = "0x00007f0000100000", SizeBytes = 131_072, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f0000200000", SizeBytes = 98_304,  Generation = 3 }
            }
        };

        report.PinnedObjects = new List<PinnedTypeStat>
        {
            new PinnedTypeStat { TypeName = "System.Byte[]", Generation = 2, Count = 2, TotalBytes = 16_384 },
            new PinnedTypeStat { TypeName = "System.String", Generation = 1, Count = 1, TotalBytes = 512 }
        };

        report.TopLohTypes = new List<LohTypeStat>
        {
            new LohTypeStat { TypeName = "System.Byte[]",   Count = 12, TotalBytes = 204_800 },
            new LohTypeStat { TypeName = "System.Object[]", Count = 3,  TotalBytes = 49_152 }
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
