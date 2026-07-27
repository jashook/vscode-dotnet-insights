////////////////////////////////////////////////////////////////////////////////
// Module: SampleReportGeneratorTests.cs
//
// Notes:
// Structural and consistency checks for SampleReportGenerator.Generate().
// These guard the sample's internal invariants (counts add up, sort orders
// hold, fragmentation percentages are plausible) so the synthetic report
// stays believable after future edits to its constants.
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.GcHeapAnalyzer;

using Xunit;

namespace DotnetInsights.GcHeapAnalyzer.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class SampleReportGeneratorTests
{
    [Fact]
    public void Generate_ReturnsReportWithAllTopLevelFieldsPopulated()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.True(report.ProcessId > 0);
        Assert.False(string.IsNullOrEmpty(report.ProcessName));
        Assert.False(string.IsNullOrEmpty(report.CaptureTimeUtc));
        Assert.NotNull(report.Summary);
        Assert.NotNull(report.Generations);
        Assert.NotNull(report.FreeChunks);
        Assert.NotNull(report.PinnedObjects);
        Assert.NotNull(report.TopLohTypes);
    }

    [Fact]
    public void Generate_SummaryTotalFreeBytesEqualsObjectPlusFreeLessThanCommitted()
    {
        FragmentationReport report = SampleReportGenerator.Generate();
        HeapSummary summary = report.Summary;

        // Free + object bytes should equal committed (or less, rounding aside).
        Assert.True(summary.TotalFreeBytes + summary.TotalObjectBytes <= summary.TotalCommittedBytes + 1);
        Assert.True(summary.TotalCommittedBytes > 0);
        Assert.True(summary.TotalObjectBytes > 0);
        Assert.True(summary.TotalFreeBytes > 0);
    }

    [Fact]
    public void Generate_SummaryFragmentationPctIsPositiveAndBelowOneHundred()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.True(report.Summary.FragmentationPct > 0.0);
        Assert.True(report.Summary.FragmentationPct < 100.0);
    }

    [Fact]
    public void Generate_GenerationsArrayHasFiveEntries()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.Equal(5, report.Generations.Length);
    }

    [Fact]
    public void Generate_GenerationLabelsAreGen0Gen1Gen2LohPoh()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.Equal("Gen0", report.Generations[0].Label);
        Assert.Equal("Gen1", report.Generations[1].Label);
        Assert.Equal("Gen2", report.Generations[2].Label);
        Assert.Equal("LOH",  report.Generations[3].Label);
        Assert.Equal("POH",  report.Generations[4].Label);
    }

    [Fact]
    public void Generate_Gen2HasHigherFragmentationThanGen0AndGen1()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        double gen2Frag = report.Generations[2].FragmentationPct;
        double gen0Frag = report.Generations[0].FragmentationPct;
        double gen1Frag = report.Generations[1].FragmentationPct;

        Assert.True(gen2Frag > gen0Frag,
            $"Gen2 fragmentation ({gen2Frag}) should exceed Gen0 ({gen0Frag})");
        Assert.True(gen2Frag > gen1Frag,
            $"Gen2 fragmentation ({gen2Frag}) should exceed Gen1 ({gen1Frag})");
    }

    [Fact]
    public void Generate_FreeChunkHistogramHasFiveBuckets()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.Equal(5, report.FreeChunks.Histogram.Length);
    }

    [Fact]
    public void Generate_FreeChunkTotalCountMatchesSumOfHistogramBucketCounts()
    {
        FragmentationReport report = SampleReportGenerator.Generate();
        FreeChunkReport freeChunks = report.FreeChunks;

        long bucketCountSum = 0;
        for (int bucketIndex = 0; bucketIndex < freeChunks.Histogram.Length; ++bucketIndex)
        {
            bucketCountSum += freeChunks.Histogram[bucketIndex].Count;
        }

        Assert.Equal(freeChunks.TotalCount, bucketCountSum);
    }

    [Fact]
    public void Generate_LargeChunksAreOrderedBySizeDescending()
    {
        FragmentationReport report = SampleReportGenerator.Generate();
        List<LargeFreeChunk> largeChunks = report.FreeChunks.LargeChunks;

        Assert.True(largeChunks.Count >= 2, "Expected at least two large free chunks in the sample report");

        for (int chunkIndex = 1; chunkIndex < largeChunks.Count; ++chunkIndex)
        {
            Assert.True(largeChunks[chunkIndex].SizeBytes <= largeChunks[chunkIndex - 1].SizeBytes,
                $"Chunk at index {chunkIndex} ({largeChunks[chunkIndex].SizeBytes} bytes) is larger than chunk at {chunkIndex - 1} ({largeChunks[chunkIndex - 1].SizeBytes} bytes)");
        }
    }

    [Fact]
    public void Generate_AllLargeChunksHavePositiveSizeAndValidAddress()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        for (int chunkIndex = 0; chunkIndex < report.FreeChunks.LargeChunks.Count; ++chunkIndex)
        {
            LargeFreeChunk chunk = report.FreeChunks.LargeChunks[chunkIndex];
            Assert.True(chunk.SizeBytes > 0, $"Chunk at index {chunkIndex} has non-positive size");
            Assert.StartsWith("0x", chunk.Address);
        }
    }

    [Fact]
    public void Generate_PinnedObjectsListIsNonEmpty()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.True(report.PinnedObjects.Count > 0);
    }

    [Fact]
    public void Generate_SummaryPinnedObjectCountMatchesTotalAcrossPinnedEntries()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        int totalPinnedFromList = 0;
        for (int pinnedIndex = 0; pinnedIndex < report.PinnedObjects.Count; ++pinnedIndex)
        {
            totalPinnedFromList += report.PinnedObjects[pinnedIndex].Count;
        }

        Assert.Equal(report.Summary.PinnedObjectCount, totalPinnedFromList);
    }

    [Fact]
    public void Generate_TopLohTypesListIsNonEmpty()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.True(report.TopLohTypes.Count > 0);
    }

    [Fact]
    public void Generate_AllGenerationsHaveNonNegativeFragmentationPct()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        for (int genIndex = 0; genIndex < report.Generations.Length; ++genIndex)
        {
            Assert.True(report.Generations[genIndex].FragmentationPct >= 0.0,
                $"Generation {genIndex} has negative fragmentation");
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapAnalyzer.Tests)
