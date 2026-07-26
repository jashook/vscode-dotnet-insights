////////////////////////////////////////////////////////////////////////////////
// Module: ClrGcPerHeapHistoryTests.cs
//
// Notes:
// ClrGcGeneration.Decode's derived fields (ObjSpaceBefore, Fragmentation,
// Out, SurvRate, ...) are pure arithmetic over the 10 raw GenData values,
// easy to get subtly wrong (e.g. summing the wrong pair, using SizeAfter
// instead of SizeBefore) without any test ever catching it since the real
// capture used elsewhere in this repo never exercises every field
// combination. ClrGcHeap.Decode's HostOffset-based layout is the other risk
// area - it's the one Version >= 3-only layout in this codebase not backed
// by any unit test before this file.
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Gc;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ClrGcPerHeapHistoryTests
{
    [Fact]
    public void ClrGcGeneration_ComputesDerivedFieldsFromRawGenData()
    {
        // Index order matches ClrGcGeneration.Decode's own documented
        // mapping: [0]=SizeBefore [1]=FreeListSpaceBefore [2]=FreeObjSpaceBefore
        // [3]=SizeAfter [4]=FreeListSpaceAfter [5]=FreeObjSpaceAfter [6]=In
        // [7]=PinnedSurv [8]=NonePinnedSurv [9]=NewAllocation(Budget).
        long[] genData = new long[] { 1000, 100, 50, 800, 80, 20, 200, 10, 590, 8 * 1024 * 1024 };

        ClrGcGeneration generation = ClrGcGeneration.Decode(genData);

        Assert.Equal(1000, generation.SizeBefore);
        Assert.Equal(800, generation.SizeAfter);
        Assert.Equal(8 * 1024 * 1024, generation.NewAllocation);
        Assert.Equal(1000 - 100 - 50, generation.ObjSpaceBefore);
        Assert.Equal(80 + 20, generation.Fragmentation);
        Assert.Equal(800 - (80 + 20), generation.ObjSizeAfter);
        Assert.Equal(10 + 590, generation.Out);
        // (10 + 590) * 100.0 / 850 = 70.588... truncated (long cast) to 70.
        Assert.Equal(70, generation.SurvRate);
    }

    [Fact]
    public void ClrGcGeneration_SurvRateIsZeroWhenObjSpaceBeforeIsZero()
    {
        long[] genData = new long[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        ClrGcGeneration generation = ClrGcGeneration.Decode(genData);

        Assert.Equal(0, generation.SurvRate);
    }

    [Fact]
    public void ClrGcHeap_Decode_ReturnsNullBelowVersionThree()
    {
        ClrGcHeap heap = ClrGcHeap.Decode(new PayloadReader(new byte[512], 8), version: 2);

        Assert.Null(heap);
    }

    [Fact]
    public void ClrGcHeap_Decode_ParsesHeapIndexAndFourGenerationsOnA64BitTrace()
    {
        const int pointerSize = 8;
        PayloadBuilder builder = new PayloadBuilder();

        builder.WriteInt16(1);                    // ClrInstanceID @0
        builder.Pad(70 - 2);                       // up to HostOffset(46, 6) == 70
        builder.WriteInt32(3);                     // HeapIndex @70
        builder.Pad(82 - 74);                       // up to HostOffset(54, 7) == 82
        builder.WriteInt32(4);                      // Count @82 - four generations

        for (int genIndex = 0; genIndex < 4; ++genIndex)
        {
            long sizeBefore = 1000 * (genIndex + 1);
            long newAllocation = 500 * (genIndex + 1);

            builder.WriteAddress(sizeBefore, pointerSize);  // SizeBefore
            builder.WriteAddress(0, pointerSize);            // FreeListSpaceBefore
            builder.WriteAddress(0, pointerSize);            // FreeObjSpaceBefore
            builder.WriteAddress(0, pointerSize);            // SizeAfter
            builder.WriteAddress(0, pointerSize);            // FreeListSpaceAfter
            builder.WriteAddress(0, pointerSize);            // FreeObjSpaceAfter
            builder.WriteAddress(0, pointerSize);            // In
            builder.WriteAddress(0, pointerSize);            // PinnedSurv
            builder.WriteAddress(0, pointerSize);            // NonePinnedSurv
            builder.WriteAddress(newAllocation, pointerSize); // NewAllocation
        }

        ClrGcHeap heap = ClrGcHeap.Decode(new PayloadReader(builder.ToArray(), pointerSize), version: 3);

        Assert.NotNull(heap);
        Assert.Equal(3, heap.HeapIndex);
        Assert.Equal(4, heap.Generations.Length);

        for (int genIndex = 0; genIndex < 4; ++genIndex)
        {
            Assert.Equal(1000 * (genIndex + 1), heap.Generations[genIndex].SizeBefore);
            Assert.Equal(500 * (genIndex + 1), heap.Generations[genIndex].NewAllocation);
        }
    }

    [Fact]
    public void ClrGcHeap_Decode_CapsDecodedGenerationsAtFourEvenWhenCountIsFive()
    {
        // Matches the real capture referenced in this file's header comment
        // (Count=5, generation index 4 is the "second pass gen0"/POH slot -
        // decoded by ClrGcHeapStats separately, not exposed per-heap here).
        const int pointerSize = 8;
        PayloadBuilder builder = new PayloadBuilder();

        builder.WriteInt16(1);
        builder.Pad(70 - 2);
        builder.WriteInt32(0);
        builder.Pad(82 - 74);
        builder.WriteInt32(5);  // Count @82 - five generations reported

        for (int genIndex = 0; genIndex < 5; ++genIndex)
        {
            for (int fieldIndex = 0; fieldIndex < 10; ++fieldIndex)
            {
                builder.WriteAddress(0, pointerSize);
            }
        }

        ClrGcHeap heap = ClrGcHeap.Decode(new PayloadReader(builder.ToArray(), pointerSize), version: 3);

        Assert.Equal(4, heap.Generations.Length);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
