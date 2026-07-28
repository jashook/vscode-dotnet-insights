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
            TotalObjectBytes    = 220_200_960,   // ~210 MB live objects
            TotalFreeBytes      =  94_371_840,   // ~90 MB fragmentation holes
            FragmentationPct    = 29.99,
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
                FreeChunkCount  = 8
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
                FreeChunkCount  = 24
            },
            new GenerationStats
            {
                Generation      = 2,
                Label           = "Gen2",
                CommittedBytes  = 167_772_160,
                ObjectBytes     = 92_274_688,
                FreeBytes       = 75_497_472,
                FragmentationPct = 45.0,
                SegmentCount    = 3,
                FreeChunkCount  = 1847
            },
            new GenerationStats
            {
                Generation      = 3,
                Label           = "LOH",
                CommittedBytes  = 130_023_424,
                ObjectBytes     = 112_459_776,
                FreeBytes       =  17_563_648,
                FragmentationPct = 13.51,
                SegmentCount    = 2,
                FreeChunkCount  = 38
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
                FreeChunkCount  = 0
            }
        };

        report.FreeChunks = new FreeChunkReport
        {
            TotalCount     = 1917,
            TotalFreeBytes = 94_371_840,
            Histogram = new FreeChunkBucket[]
            {
                new FreeChunkBucket { Label = "< 1 KB",     MinBytes = 0,          MaxBytes = 1_023,      Count = 312,  TotalBytes = 199_680 },
                new FreeChunkBucket { Label = "1–8 KB",     MinBytes = 1_024,      MaxBytes = 8_191,      Count = 1427, TotalBytes = 5_861_376 },
                new FreeChunkBucket { Label = "8–85 KB",    MinBytes = 8_192,      MaxBytes = 84_999,     Count = 140,  TotalBytes = 5_898_240 },
                new FreeChunkBucket { Label = "85 KB–1 MB", MinBytes = 85_000,     MaxBytes = 1_048_575,  Count = 33,   TotalBytes = 15_728_640 },
                new FreeChunkBucket { Label = "> 1 MB",     MinBytes = 1_048_576,  MaxBytes = -1,         Count = 5,    TotalBytes = 66_683_904 }
            },
            LargeChunks = new List<LargeFreeChunk>
            {
                new LargeFreeChunk { Address = "0x00007f2c40000000", SizeBytes = 20_971_520, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c50000000", SizeBytes = 18_874_368, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c60000000", SizeBytes = 15_728_640, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c70000000", SizeBytes = 11_534_336, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c30000000", SizeBytes =  5_242_880, Generation = 3 },
                new LargeFreeChunk { Address = "0x00007f2c20000000", SizeBytes =  3_670_016, Generation = 3 },
                new LargeFreeChunk { Address = "0x00007f2c10000000", SizeBytes =  2_097_152, Generation = 3 },
                new LargeFreeChunk { Address = "0x00007f2c08000000", SizeBytes =  1_572_864, Generation = 3 },
                new LargeFreeChunk { Address = "0x00007f2c05000000", SizeBytes =  1_048_576, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c04000000", SizeBytes =    786_432, Generation = 3 },
                new LargeFreeChunk { Address = "0x00007f2c03800000", SizeBytes =    524_288, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c03400000", SizeBytes =    393_216, Generation = 3 },
                new LargeFreeChunk { Address = "0x00007f2c03000000", SizeBytes =    262_144, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c02c00000", SizeBytes =    196_608, Generation = 3 },
                new LargeFreeChunk { Address = "0x00007f2c02800000", SizeBytes =    131_072, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c02600000", SizeBytes =    114_688, Generation = 3 },
                new LargeFreeChunk { Address = "0x00007f2c02400000", SizeBytes =    106_496, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c02200000", SizeBytes =     98_304, Generation = 3 },
                new LargeFreeChunk { Address = "0x00007f2c02000000", SizeBytes =     90_112, Generation = 2 },
                new LargeFreeChunk { Address = "0x00007f2c01e00000", SizeBytes =     86_016, Generation = 3 }
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
