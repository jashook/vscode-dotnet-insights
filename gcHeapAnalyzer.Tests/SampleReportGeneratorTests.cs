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
        Assert.NotNull(report.TopPohTypes);
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

    // This sample's POH generation stats (Generations[4]) are all zero - no
    // POH activity in this synthetic scenario - so TopPohTypes should be
    // present but empty, not null and not populated with unrelated data.
    [Fact]
    public void Generate_TopPohTypesListIsPresentButEmptyForZeroPohActivity()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.NotNull(report.TopPohTypes);
        Assert.Empty(report.TopPohTypes);
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

    // Each generation's own histogram must sum to that same generation's
    // FreeBytes/FreeChunkCount - otherwise the per-generation view (added so
    // Gen2's fragmentation shape isn't drowned out by Gen0/Gen1's much
    // higher chunk counts in the aggregate) would silently disagree with
    // the generation table sitting right next to it in the UI.
    [Fact]
    public void Generate_EachGenerationHistogramSumsMatchThatGenerationsFreeBytesAndCount()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        for (int genIndex = 0; genIndex < report.Generations.Length; ++genIndex)
        {
            GenerationStats gen = report.Generations[genIndex];

            long bucketCountSum = 0;
            long bucketBytesSum = 0;
            for (int bucketIndex = 0; bucketIndex < gen.Histogram.Length; ++bucketIndex)
            {
                bucketCountSum += gen.Histogram[bucketIndex].Count;
                bucketBytesSum += gen.Histogram[bucketIndex].TotalBytes;
            }

            Assert.True(gen.FreeChunkCount == bucketCountSum,
                $"{gen.Label}: FreeChunkCount ({gen.FreeChunkCount}) != histogram count sum ({bucketCountSum})");
            Assert.True(gen.FreeBytes == bucketBytesSum,
                $"{gen.Label}: FreeBytes ({gen.FreeBytes}) != histogram bytes sum ({bucketBytesSum})");
        }
    }

    // The cross-generation aggregate (report.FreeChunks.Histogram) must
    // equal the sum, bucket-by-bucket, of all five generations' own
    // histograms - this is a structural invariant of real HeapAnalyzer
    // output (every free object is tallied into both places), and the
    // synthetic sample should honor it too so it stays a believable stand-in
    // for a real capture.
    [Fact]
    public void Generate_AggregateHistogramEqualsSumOfPerGenerationHistograms()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        for (int bucketIndex = 0; bucketIndex < report.FreeChunks.Histogram.Length; ++bucketIndex)
        {
            long expectedCount = 0;
            long expectedBytes = 0;
            for (int genIndex = 0; genIndex < report.Generations.Length; ++genIndex)
            {
                expectedCount += report.Generations[genIndex].Histogram[bucketIndex].Count;
                expectedBytes += report.Generations[genIndex].Histogram[bucketIndex].TotalBytes;
            }

            FreeChunkBucket aggregateBucket = report.FreeChunks.Histogram[bucketIndex];
            Assert.True(aggregateBucket.Count == expectedCount,
                $"Bucket '{aggregateBucket.Label}': aggregate count ({aggregateBucket.Count}) != sum across generations ({expectedCount})");
            Assert.True(aggregateBucket.TotalBytes == expectedBytes,
                $"Bucket '{aggregateBucket.Label}': aggregate bytes ({aggregateBucket.TotalBytes}) != sum across generations ({expectedBytes})");
        }
    }

    // Root-cause signal this sample exists to demonstrate: at least one
    // Gen2 hole must be bracketed by a pinned object (the permanent,
    // non-compactable case) and at least one must NOT be (showing the tool
    // can also surface a Gen2 hole that pinning doesn't explain).
    [Fact]
    public void Generate_Gen2LargeChunksIncludeBothPinnedAndNonPinnedAdjacency()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        bool sawPinnedAdjacency = false;
        bool sawNonPinnedAdjacency = false;

        for (int chunkIndex = 0; chunkIndex < report.FreeChunks.LargeChunks.Count; ++chunkIndex)
        {
            LargeFreeChunk chunk = report.FreeChunks.LargeChunks[chunkIndex];
            if (chunk.Generation != 2)
            {
                continue;
            }

            if (chunk.PrecedingIsPinned || chunk.FollowingIsPinned)
            {
                sawPinnedAdjacency = true;
            }
            else
            {
                sawNonPinnedAdjacency = true;
            }
        }

        Assert.True(sawPinnedAdjacency, "Expected at least one Gen2 hole bracketed by a pinned object");
        Assert.True(sawNonPinnedAdjacency, "Expected at least one Gen2 hole with no pinned neighbor");
    }

    // LOH fragmentation in this sample is deliberately never pinning-caused
    // (LOH objects are rarely pinned via GCHandle in practice) - a different
    // root cause (buffer-size mismatch) than Gen2's story above.
    [Fact]
    public void Generate_LohLargeChunksHaveNoPinnedAdjacency()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        for (int chunkIndex = 0; chunkIndex < report.FreeChunks.LargeChunks.Count; ++chunkIndex)
        {
            LargeFreeChunk chunk = report.FreeChunks.LargeChunks[chunkIndex];
            if (chunk.Generation != 3)
            {
                continue;
            }

            Assert.False(chunk.PrecedingIsPinned, $"LOH chunk at {chunk.Address} should not have a pinned preceding neighbor");
            Assert.False(chunk.FollowingIsPinned, $"LOH chunk at {chunk.Address} should not have a pinned following neighbor");
        }
    }

    [Fact]
    public void Generate_SegmentsListIsNonEmptyAndOccupancyPctIsWithinZeroToOneHundred()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.True(report.Segments.Count > 0);

        for (int segmentIndex = 0; segmentIndex < report.Segments.Count; ++segmentIndex)
        {
            SegmentOccupancy segment = report.Segments[segmentIndex];
            Assert.True(segment.OccupancyPct >= 0.0 && segment.OccupancyPct <= 100.0,
                $"Segment {segment.Address} has out-of-range occupancy {segment.OccupancyPct}");
            Assert.StartsWith("0x", segment.Address);
        }
    }

    // Per-generation segment committed/live bytes should reconcile with
    // that generation's own totals - otherwise the segment-occupancy view
    // would show numbers that don't add up against the generation table
    // right next to it.
    [Fact]
    public void Generate_Gen2SegmentBytesSumMatchesGen2CommittedAndObjectBytes()
    {
        FragmentationReport report = SampleReportGenerator.Generate();
        GenerationStats gen2 = report.Generations[2];

        long committedSum = 0;
        long liveSum = 0;
        for (int segmentIndex = 0; segmentIndex < report.Segments.Count; ++segmentIndex)
        {
            SegmentOccupancy segment = report.Segments[segmentIndex];
            if (segment.Generation != 2)
            {
                continue;
            }

            committedSum += segment.CommittedBytes;
            liveSum += segment.LiveBytes;
        }

        Assert.Equal(gen2.CommittedBytes, committedSum);
        Assert.Equal(gen2.ObjectBytes, liveSum);
    }

    [Fact]
    public void Generate_SegmentMapsListHasOneEntryPerSegment()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        Assert.Equal(report.Segments.Count, report.SegmentMaps.Count);
    }

    // Each SegmentMap's blocks must reconcile with the SegmentOccupancy entry
    // sharing its Address - otherwise the block-strip visualization and the
    // occupancy table sitting next to it in the UI would silently disagree
    // about how much of the segment is actually live vs free.
    [Fact]
    public void Generate_EachSegmentMapBlocksSumMatchesItsSegmentOccupancyEntry()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        for (int mapIndex = 0; mapIndex < report.SegmentMaps.Count; ++mapIndex)
        {
            SegmentMap segmentMap = report.SegmentMaps[mapIndex];

            SegmentOccupancy matchingOccupancy = null;
            for (int segmentIndex = 0; segmentIndex < report.Segments.Count; ++segmentIndex)
            {
                if (report.Segments[segmentIndex].Address == segmentMap.Address)
                {
                    matchingOccupancy = report.Segments[segmentIndex];
                    break;
                }
            }

            Assert.True(matchingOccupancy != null, $"No SegmentOccupancy entry found for SegmentMap at {segmentMap.Address}");

            long liveSum = 0;
            long gapSum = 0;
            for (int blockIndex = 0; blockIndex < segmentMap.Blocks.Count; ++blockIndex)
            {
                SegmentBlock block = segmentMap.Blocks[blockIndex];
                if (block.IsGap)
                {
                    gapSum += block.Bytes;
                }
                else
                {
                    liveSum += block.Bytes;
                }
            }

            Assert.True(liveSum == matchingOccupancy.LiveBytes,
                $"Segment {segmentMap.Address}: block live sum ({liveSum}) != SegmentOccupancy.LiveBytes ({matchingOccupancy.LiveBytes})");
            Assert.True(gapSum == matchingOccupancy.CommittedBytes - matchingOccupancy.LiveBytes,
                $"Segment {segmentMap.Address}: block gap sum ({gapSum}) != committed-minus-live ({matchingOccupancy.CommittedBytes - matchingOccupancy.LiveBytes})");
        }
    }

    // Root-cause signal this feature exists to show visually: the sample's
    // worst (25%-occupancy) Gen2 segment should have at least one pinned
    // live block bracketing at least one gap - the "why can't this segment
    // be reclaimed" story the block-strip view is meant to make obvious.
    [Fact]
    public void Generate_WorstGen2SegmentMapHasAPinnedBlockAndAGap()
    {
        FragmentationReport report = SampleReportGenerator.Generate();

        SegmentMap worstSegmentMap = null;
        double worstOccupancy = 101.0;
        for (int segmentIndex = 0; segmentIndex < report.Segments.Count; ++segmentIndex)
        {
            SegmentOccupancy segment = report.Segments[segmentIndex];
            if (segment.Generation == 2 && segment.OccupancyPct < worstOccupancy)
            {
                worstOccupancy = segment.OccupancyPct;
                for (int mapIndex = 0; mapIndex < report.SegmentMaps.Count; ++mapIndex)
                {
                    if (report.SegmentMaps[mapIndex].Address == segment.Address)
                    {
                        worstSegmentMap = report.SegmentMaps[mapIndex];
                        break;
                    }
                }
            }
        }

        Assert.NotNull(worstSegmentMap);

        bool sawPinnedBlock = false;
        bool sawGap = false;
        for (int blockIndex = 0; blockIndex < worstSegmentMap.Blocks.Count; ++blockIndex)
        {
            SegmentBlock block = worstSegmentMap.Blocks[blockIndex];
            if (block.IsGap) { sawGap = true; }
            if (block.HasPinnedObject) { sawPinnedBlock = true; }
        }

        Assert.True(sawPinnedBlock, "Expected the worst Gen2 segment's map to include a pinned block");
        Assert.True(sawGap, "Expected the worst Gen2 segment's map to include a gap");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapAnalyzer.Tests)
