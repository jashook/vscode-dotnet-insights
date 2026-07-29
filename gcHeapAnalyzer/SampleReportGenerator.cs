////////////////////////////////////////////////////////////////////////////////
// Module: SampleReportGenerator.cs
//
// Notes:
// Produces a realistic but synthetic FragmentationReport without attaching to
// any process. Used for:
//   - Testing the VS Code webview integration on macOS where task_for_pid
//     requires root (SIP restriction — live attach works on Linux/Windows
//     without elevation for same-user processes).
//   - Giving new users a concrete example of what the output looks like.
//
// The numbers are modelled on a real service with a fragmented Gen2 heap:
// 50 long-lived pinned System.Byte[] GCHandles promoting to Gen2 create
// holes after the arrays they were pinning near get collected, and the LOH
// has fragmentation from a mix of large response buffers of varying sizes.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.GcHeapAnalyzer {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class SampleReportGenerator
{
    public static FragmentationReport Generate()
    {
        FragmentationReport report = new FragmentationReport();
        report.ProcessId = 12345;
        report.ProcessName = "sample-service";
        report.CaptureTimeUtc = DateTime.UtcNow.ToString("o");

        report.Summary = new HeapSummary
        {
            TotalCommittedBytes = 314_572_800,   // ~300 MB committed
            TotalObjectBytes    = 223_222_512,   // ~213 MB live objects
            TotalFreeBytes      =  91_350_288,   // ~87 MB fragmentation holes
            FragmentationPct    = 29.04,
            PinnedObjectCount   = 52,
            SegmentCount        = 6
        };

        report.Generations = new GenerationStats[]
        {
            new GenerationStats
            {
                Generation      = 0,
                Label           = "Gen0",
                CommittedBytes  = 4_194_304,
                ObjectBytes     = 3_932_160,
                FreeBytes       =   262_144,
                FragmentationPct = 6.25,
                SegmentCount    = 0,
                FreeChunkCount  = 8,
                Histogram = new FreeChunkBucket[]
                {
                    new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,          MaxBytes = 1_023,      Count = 0, TotalBytes = 0 },
                    new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,      MaxBytes = 8_191,      Count = 0, TotalBytes = 0 },
                    new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,      MaxBytes = 84_999,     Count = 8, TotalBytes = 262_144 },
                    new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,     MaxBytes = 1_048_575,  Count = 0, TotalBytes = 0 },
                    new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576,  MaxBytes = -1,         Count = 0, TotalBytes = 0 }
                }
            },
            new GenerationStats
            {
                Generation      = 1,
                Label           = "Gen1",
                CommittedBytes  = 12_582_912,
                ObjectBytes     = 11_534_336,
                FreeBytes       =  1_048_576,
                FragmentationPct = 8.33,
                SegmentCount    = 0,
                FreeChunkCount  = 24,
                Histogram = new FreeChunkBucket[]
                {
                    new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,          MaxBytes = 1_023,      Count = 0,  TotalBytes = 0 },
                    new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,      MaxBytes = 8_191,      Count = 0,  TotalBytes = 0 },
                    new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,      MaxBytes = 84_999,     Count = 24, TotalBytes = 1_048_576 },
                    new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,     MaxBytes = 1_048_575,  Count = 0,  TotalBytes = 0 },
                    new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576,  MaxBytes = -1,         Count = 0,  TotalBytes = 0 }
                }
            },
            // Gen2's own histogram is the one that actually matters for this
            // sample's story: a handful of huge (>1 MB) holes bracketed by
            // pinned System.Byte[] buffers (see LargeChunks' Preceding/
            // FollowingIsPinned below) plus a long tail of small (<8 KB)
            // holes from ordinary promotion churn - two different
            // fragmentation causes coexisting in the same generation.
            new GenerationStats
            {
                Generation      = 2,
                Label           = "Gen2",
                CommittedBytes  = 167_772_160,
                ObjectBytes     = 92_274_688,
                FreeBytes       = 75_497_472,
                FragmentationPct = 45.0,
                SegmentCount    = 3,
                FreeChunkCount  = 1847,
                Histogram = new FreeChunkBucket[]
                {
                    new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,          MaxBytes = 1_023,      Count = 600,  TotalBytes =    300_000 },
                    new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,      MaxBytes = 8_191,      Count = 1200, TotalBytes =  4_800_000 },
                    new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,      MaxBytes = 84_999,     Count = 37,   TotalBytes =  1_125_920 },
                    new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,     MaxBytes = 1_048_575,  Count = 5,    TotalBytes =  1_114_112 },
                    new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576,  MaxBytes = -1,         Count = 5,    TotalBytes = 68_157_440 }
                }
            },
            // LOH's fragmentation is a completely different shape than
            // Gen2's - no pinning involved at all (see LargeChunks below,
            // all FollowingIsPinned/PrecedingIsPinned false for Generation
            // 3), just a mix of large response buffers whose varying sizes
            // don't fit neatly into the holes previous ones left behind.
            new GenerationStats
            {
                Generation      = 3,
                Label           = "LOH",
                CommittedBytes  = 130_023_424,
                ObjectBytes     = 115_481_328,
                FreeBytes       =  14_542_096,
                FragmentationPct = 11.18,
                SegmentCount    = 2,
                FreeChunkCount  = 38,
                Histogram = new FreeChunkBucket[]
                {
                    new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,          MaxBytes = 1_023,      Count = 10, TotalBytes =      6_000 },
                    new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,      MaxBytes = 8_191,      Count = 15, TotalBytes =     97_920 },
                    new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,      MaxBytes = 84_999,     Count = 3,  TotalBytes =    180_000 },
                    new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,     MaxBytes = 1_048_575,  Count = 6,  TotalBytes =  1_675_264 },
                    new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576,  MaxBytes = -1,         Count = 4,  TotalBytes = 12_582_912 }
                }
            },
            new GenerationStats
            {
                Generation      = 4,
                Label           = "POH",
                CommittedBytes  = 0,
                ObjectBytes     = 0,
                FreeBytes       = 0,
                FragmentationPct = 0.0,
                SegmentCount    = 0,
                FreeChunkCount  = 0,
                Histogram = new FreeChunkBucket[]
                {
                    new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,          MaxBytes = 1_023,      Count = 0, TotalBytes = 0 },
                    new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,      MaxBytes = 8_191,      Count = 0, TotalBytes = 0 },
                    new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,      MaxBytes = 84_999,     Count = 0, TotalBytes = 0 },
                    new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,     MaxBytes = 1_048_575,  Count = 0, TotalBytes = 0 },
                    new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576,  MaxBytes = -1,         Count = 0, TotalBytes = 0 }
                }
            }
        };

        // Cross-generation aggregate - each bucket here is the sum of the
        // same-labelled bucket across all five Generations[].Histogram
        // arrays above (real HeapAnalyzer output has this same relationship
        // by construction, since every free object is tallied into both its
        // own generation's histogram and this global one).
        report.FreeChunks = new FreeChunkReport
        {
            TotalCount     = 1917,
            TotalFreeBytes = 91_350_288,
            Histogram = new FreeChunkBucket[]
            {
                new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,          MaxBytes = 1_023,      Count = 610,  TotalBytes =    306_000 },
                new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,      MaxBytes = 8_191,      Count = 1215, TotalBytes =  4_897_920 },
                new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,      MaxBytes = 84_999,     Count = 72,   TotalBytes =  2_616_640 },
                new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,     MaxBytes = 1_048_575,  Count = 11,   TotalBytes =  2_789_376 },
                new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576,  MaxBytes = -1,         Count = 9,    TotalBytes = 80_740_352 }
            },
            // Preceding/FollowingIsPinned tells the two generations' very
            // different stories apart at a glance: Gen2's holes are mostly
            // bracketed by a pinned System.Byte[] on at least one side (the
            // GC can't compact past it, so the hole is permanent regardless
            // of how many more collections run) - except the last Gen2 entry
            // below, deliberately left with two *non*-pinned neighbors to
            // show this tool can also surface a Gen2 hole that pinning
            // doesn't explain (see the wider fragmentation-causes
            // discussion this sample is meant to support). LOH's holes are
            // never pinned on either side - a pure buffer-size-mismatch
            // story instead.
            LargeChunks = new List<LargeFreeChunk>
            {
                new LargeFreeChunk { Address = "0x00007f2c40000000", SizeBytes = 20_971_520, Generation = 2, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = true,  FollowingTypeName = "System.Byte[]",        FollowingIsPinned = true },
                new LargeFreeChunk { Address = "0x00007f2c50000000", SizeBytes = 18_874_368, Generation = 2, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = true,  FollowingTypeName = "MyApp.RequestContext",  FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c60000000", SizeBytes = 15_728_640, Generation = 2, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = true,  FollowingTypeName = "System.Byte[]",        FollowingIsPinned = true },
                new LargeFreeChunk { Address = "0x00007f2c70000000", SizeBytes = 11_534_336, Generation = 2, PrecedingTypeName = "System.String",        PrecedingIsPinned = true,  FollowingTypeName = "System.Byte[]",        FollowingIsPinned = true },
                new LargeFreeChunk { Address = "0x00007f2c30000000", SizeBytes =  5_242_880, Generation = 3, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = false, FollowingTypeName = "System.Byte[]",        FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c20000000", SizeBytes =  3_670_016, Generation = 3, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = false, FollowingTypeName = "System.Object[]",      FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c10000000", SizeBytes =  2_097_152, Generation = 3, PrecedingTypeName = "System.Object[]",     PrecedingIsPinned = false, FollowingTypeName = "System.Byte[]",        FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c08000000", SizeBytes =  1_572_864, Generation = 3, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = false, FollowingTypeName = "System.Byte[]",        FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c05000000", SizeBytes =  1_048_576, Generation = 2, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = true,  FollowingTypeName = "System.Object[]",      FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c04000000", SizeBytes =    786_432, Generation = 3, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = false, FollowingTypeName = "System.Char[]",        FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c03800000", SizeBytes =    524_288, Generation = 2, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = true,  FollowingTypeName = "System.Byte[]",        FollowingIsPinned = true },
                new LargeFreeChunk { Address = "0x00007f2c03400000", SizeBytes =    393_216, Generation = 3, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = false, FollowingTypeName = "System.Byte[]",        FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c03000000", SizeBytes =    262_144, Generation = 2, PrecedingTypeName = "MyApp.Buffer",         PrecedingIsPinned = false, FollowingTypeName = "System.Byte[]",        FollowingIsPinned = true },
                new LargeFreeChunk { Address = "0x00007f2c02c00000", SizeBytes =    196_608, Generation = 3, PrecedingTypeName = "System.Char[]",        PrecedingIsPinned = false, FollowingTypeName = "System.Byte[]",        FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c02800000", SizeBytes =    131_072, Generation = 2, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = true,  FollowingTypeName = "System.Byte[]",        FollowingIsPinned = true },
                new LargeFreeChunk { Address = "0x00007f2c02600000", SizeBytes =    114_688, Generation = 3, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = false, FollowingTypeName = "System.Byte[]",        FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c02400000", SizeBytes =    106_496, Generation = 2, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = true,  FollowingTypeName = "MyApp.RequestContext",  FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c02200000", SizeBytes =     98_304, Generation = 3, PrecedingTypeName = "System.Byte[]",       PrecedingIsPinned = false, FollowingTypeName = "System.Object[]",      FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c02000000", SizeBytes =     90_112, Generation = 2, PrecedingTypeName = "MyApp.Buffer",         PrecedingIsPinned = false, FollowingTypeName = "MyApp.Buffer",          FollowingIsPinned = false },
                new LargeFreeChunk { Address = "0x00007f2c01e00000", SizeBytes =     86_016, Generation = 3, PrecedingTypeName = "System.Object[]",     PrecedingIsPinned = false, FollowingTypeName = "System.Byte[]",        FollowingIsPinned = false }
            }
        };

        // Three Gen2 segments (matching Generations[2].SegmentCount) and two
        // LOH segments (matching Generations[3].SegmentCount) - committed/
        // live bytes per segment sum exactly to each generation's
        // CommittedBytes/ObjectBytes above. The first Gen2 segment is the
        // one worth investigating: 25% occupancy means three quarters of
        // its committed memory is unreachable-but-not-reclaimed space,
        // consistent with the pinned-buffer holes recorded in LargeChunks.
        report.Segments = new List<SegmentOccupancy>
        {
            new SegmentOccupancy { Address = "0x00007f2c00000000", Generation = 2, CommittedBytes = 60_000_000, LiveBytes = 15_000_000, OccupancyPct = 25.0 },
            new SegmentOccupancy { Address = "0x00007f2c80000000", Generation = 2, CommittedBytes = 60_000_000, LiveBytes = 45_000_000, OccupancyPct = 75.0 },
            new SegmentOccupancy { Address = "0x00007f2c90000000", Generation = 2, CommittedBytes = 47_772_160, LiveBytes = 32_274_688, OccupancyPct = 67.57 },
            new SegmentOccupancy { Address = "0x00007f2ca0000000", Generation = 3, CommittedBytes = 65_011_712, LiveBytes = 60_000_000, OccupancyPct = 92.29 },
            new SegmentOccupancy { Address = "0x00007f2cb0000000", Generation = 3, CommittedBytes = 65_011_712, LiveBytes = 55_481_328, OccupancyPct = 85.34 }
        };

        // Address-ordered block map for each segment above (Address values
        // match report.Segments so a caller can correlate the two) - each
        // segment's non-gap block Bytes sum to its LiveBytes and gap block
        // Bytes sum to (CommittedBytes - LiveBytes) exactly, same invariant
        // as the histogram/aggregate consistency elsewhere in this sample.
        // The first Gen2 segment tells this sample's whole story on its
        // own: two pinned System.Byte[] runs bracketing two large gaps,
        // simplified here to a handful of coalesced blocks rather than the
        // (illustrative-only) one-box-per-object mockup this feature was
        // modelled on - a real Gen2 segment can hold thousands of objects.
        report.SegmentMaps = new List<SegmentMap>
        {
            new SegmentMap
            {
                Address = "0x00007f2c00000000", Generation = 2,
                Blocks = new List<SegmentBlock>
                {
                    new SegmentBlock { IsGap = false, TypeName = "System.Byte[]",        OtherTypeCount = 0, ObjectCount = 12,  Bytes =  5_000_000, HasPinnedObject = true },
                    new SegmentBlock { IsGap = true,                                      OtherTypeCount = 0, ObjectCount = 1,   Bytes = 20_000_000 },
                    new SegmentBlock { IsGap = false, TypeName = "MyApp.RequestContext",  OtherTypeCount = 2, ObjectCount = 340, Bytes =  4_000_000, HasPinnedObject = false },
                    new SegmentBlock { IsGap = true,                                      OtherTypeCount = 0, ObjectCount = 1,   Bytes = 15_000_000 },
                    new SegmentBlock { IsGap = false, TypeName = "System.Byte[]",        OtherTypeCount = 0, ObjectCount = 8,   Bytes =  6_000_000, HasPinnedObject = true },
                    new SegmentBlock { IsGap = true,                                      OtherTypeCount = 0, ObjectCount = 1,   Bytes = 10_000_000 }
                }
            },
            new SegmentMap
            {
                Address = "0x00007f2c80000000", Generation = 2,
                Blocks = new List<SegmentBlock>
                {
                    new SegmentBlock { IsGap = false, TypeName = "System.Byte[]",       OtherTypeCount = 1, ObjectCount = 50,  Bytes = 30_000_000, HasPinnedObject = true },
                    new SegmentBlock { IsGap = true,                                     OtherTypeCount = 0, ObjectCount = 1,   Bytes = 10_000_000 },
                    new SegmentBlock { IsGap = false, TypeName = "MyApp.RequestContext", OtherTypeCount = 0, ObjectCount = 500, Bytes = 15_000_000, HasPinnedObject = false },
                    new SegmentBlock { IsGap = true,                                     OtherTypeCount = 0, ObjectCount = 1,   Bytes =  5_000_000 }
                }
            },
            new SegmentMap
            {
                Address = "0x00007f2c90000000", Generation = 2,
                Blocks = new List<SegmentBlock>
                {
                    new SegmentBlock { IsGap = false, TypeName = "System.Byte[]",       OtherTypeCount = 0, ObjectCount = 20,  Bytes = 20_274_688, HasPinnedObject = false },
                    new SegmentBlock { IsGap = true,                                     OtherTypeCount = 0, ObjectCount = 1,   Bytes =  9_497_472 },
                    new SegmentBlock { IsGap = false, TypeName = "MyApp.RequestContext", OtherTypeCount = 1, ObjectCount = 200, Bytes = 12_000_000, HasPinnedObject = false },
                    new SegmentBlock { IsGap = true,                                     OtherTypeCount = 0, ObjectCount = 1,   Bytes =  6_000_000 }
                }
            },
            new SegmentMap
            {
                Address = "0x00007f2ca0000000", Generation = 3,
                Blocks = new List<SegmentBlock>
                {
                    new SegmentBlock { IsGap = false, TypeName = "System.Byte[]", OtherTypeCount = 2, ObjectCount = 900, Bytes = 60_000_000, HasPinnedObject = false },
                    new SegmentBlock { IsGap = true,                               OtherTypeCount = 0, ObjectCount = 1,   Bytes =  5_011_712 }
                }
            },
            new SegmentMap
            {
                Address = "0x00007f2cb0000000", Generation = 3,
                Blocks = new List<SegmentBlock>
                {
                    new SegmentBlock { IsGap = false, TypeName = "System.Byte[]", OtherTypeCount = 1, ObjectCount = 850, Bytes = 55_481_328, HasPinnedObject = false },
                    new SegmentBlock { IsGap = true,                               OtherTypeCount = 0, ObjectCount = 1,   Bytes =  9_530_384 }
                }
            }
        };

        report.PinnedObjects = new List<PinnedTypeStat>
        {
            new PinnedTypeStat { TypeName = "System.Byte[]",   Generation = 2, Count = 38, TotalBytes = 25_165_824 },
            new PinnedTypeStat { TypeName = "System.Byte[]",   Generation = 1, Count = 10, TotalBytes =    409_600 },
            new PinnedTypeStat { TypeName = "System.Byte[]",   Generation = 0, Count =  2, TotalBytes =      8_192 },
            new PinnedTypeStat { TypeName = "System.String",   Generation = 2, Count =  1, TotalBytes =        512 },
            new PinnedTypeStat { TypeName = "System.Object[]", Generation = 2, Count =  1, TotalBytes =      4_096 }
        };

        report.TopLohTypes = new List<TypeStat>
        {
            new TypeStat { TypeName = "System.Byte[]",                 Count = 892, TotalBytes = 89_128_960 },
            new TypeStat { TypeName = "System.Object[]",               Count =  44, TotalBytes = 14_680_064 },
            new TypeStat { TypeName = "System.Char[]",                 Count =  18, TotalBytes =  4_718_592 },
            new TypeStat { TypeName = "System.Int32[]",                Count =   6, TotalBytes =  2_097_152 },
            new TypeStat { TypeName = "System.Collections.Generic.Dictionary`2+Entry[System.String,System.Object]", Count = 3, TotalBytes = 1_835_008 }
        };

        // Empty, not omitted - matches this sample's POH generation stats
        // above (all zero: this synthetic scenario has no Pinned Object
        // Heap activity at all), and keeps the JSON shape consistent with a
        // real capture where the key is always present even when empty.
        report.TopPohTypes = new List<TypeStat>();

        return report;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapAnalyzer)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
