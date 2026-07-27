////////////////////////////////////////////////////////////////////////////////
// Module: ClrGcTypesTests.cs
//
// Notes:
// Byte-offset regression tests for the CLR GC event decoders in
// Gc/ClrGcTypes.cs. These offsets are hardcoded from the CLR ETW manifest
// (see that file's own header comment) and verified only against real
// captures elsewhere in this repo - a silent offset slip here would corrupt
// every GC/allocation number the extension renders, so each decoder gets a
// synthetic payload built to its documented layout and checked field by
// field, including the version-gated optional tails.
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Gc;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ClrGcTypesTests
{
    [Fact]
    public void ClrGcStart_DecodesCountDepthReasonType()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt32(42)   // Count @0
            .WriteInt32(0)    // Depth @4
            .WriteInt32(4)    // Reason @8 (AllocLarge)
            .WriteInt32(1)    // Type @12 (BackgroundGC)
            .WriteInt16(1)    // ClrInstanceID @16
            .ToArray();

        ClrGcStart start = ClrGcStart.Decode(new PayloadReader(payload, 8), version: 1);

        Assert.Equal(42, start.Count);
        Assert.Equal(0, start.Depth);
        Assert.Equal(GCReason.AllocLarge, start.Reason);
        Assert.Equal(GCType.BackgroundGC, start.Type);
        Assert.Equal(1, start.ClrInstanceID);
    }

    [Fact]
    public void ClrGcStart_OmitsClrInstanceIdWhenVersionZero()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt32(1)
            .WriteInt32(0)
            .WriteInt32(0)
            .WriteInt32(0)
            .WriteInt16(99)
            .ToArray();

        ClrGcStart start = ClrGcStart.Decode(new PayloadReader(payload, 8), version: 0);

        Assert.Equal(0, start.ClrInstanceID);
    }

    [Fact]
    public void ClrGcEnd_DecodesCountAndDepthAsInt32WhenVersionAtLeastOne()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt32(42)  // Count @0
            .WriteInt32(2)   // Depth @4 (Int32 form, version >= 1)
            .WriteInt16(1)   // ClrInstanceID @8
            .ToArray();

        ClrGcEnd end = ClrGcEnd.Decode(new PayloadReader(payload, 8), version: 1);

        Assert.Equal(42, end.Count);
        Assert.Equal(2, end.Depth);
        Assert.Equal(1, end.ClrInstanceID);
    }

    [Fact]
    public void ClrGcEnd_DecodesDepthAsInt16WhenVersionZero()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt32(7)   // Count @0
            .WriteInt16(2)   // Depth @4 (Int16 form, version 0)
            .ToArray();

        ClrGcEnd end = ClrGcEnd.Decode(new PayloadReader(payload, 8), version: 0);

        Assert.Equal(7, end.Count);
        Assert.Equal(2, end.Depth);
    }

    [Fact]
    public void ClrGcHeapStats_DecodesGenerationSizesAndComputesTotals()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt64(1024)  // GenerationSize0 @0
            .WriteInt64(512)   // TotalPromotedSize0 @8
            .WriteInt64(2048)  // GenerationSize1 @16
            .WriteInt64(1024)  // TotalPromotedSize1 @24
            .WriteInt64(4096)  // GenerationSize2 @32
            .WriteInt64(2048)  // TotalPromotedSize2 @40
            .WriteInt64(8192)  // GenerationSize3 (LOH) @48
            .WriteInt64(4096)  // TotalPromotedSize3 @56
            .WriteInt64(0)     // FinalizationPromotedSize @64
            .WriteInt64(0)     // FinalizationPromotedCount @72
            .WriteInt32(5)     // PinnedObjectCount @80
            .WriteInt32(3)     // SinkBlockCount @84
            .WriteInt32(100)   // GCHandleCount @88
            .WriteInt16(1)     // ClrInstanceID @92 (version >= 1)
            .WriteInt64(256)   // GenerationSize4 (POH) @94 (version >= 2)
            .WriteInt64(128)   // TotalPromotedSize4 @102
            .ToArray();

        ClrGcHeapStats stats = ClrGcHeapStats.Decode(new PayloadReader(payload, 8), version: 2);

        Assert.Equal(1024, stats.GenerationSize0);
        Assert.Equal(8192, stats.GenerationSize3);
        Assert.Equal(256, stats.GenerationSize4);
        Assert.Equal(100, stats.GCHandleCount);
        Assert.Equal(1, stats.ClrInstanceID);
        Assert.Equal(1024 + 2048 + 4096 + 8192 + 256, stats.TotalHeapSize);
        Assert.Equal(512 + 1024 + 2048 + 4096 + 128, stats.TotalPromoted);
    }

    [Fact]
    public void ClrGcHeapStats_OmitsPohFieldsWhenVersionOne()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt64(1024).WriteInt64(0)
            .WriteInt64(0).WriteInt64(0)
            .WriteInt64(0).WriteInt64(0)
            .WriteInt64(0).WriteInt64(0)
            .WriteInt64(0).WriteInt64(0)
            .WriteInt32(0).WriteInt32(0).WriteInt32(0)
            .WriteInt16(1)
            .ToArray();

        ClrGcHeapStats stats = ClrGcHeapStats.Decode(new PayloadReader(payload, 8), version: 1);

        Assert.Equal(0, stats.GenerationSize4);
        Assert.Equal(0, stats.TotalPromotedSize4);
    }

    [Fact]
    public void ClrGcGlobalHeapHistory_DecodesReasonAndMechanismsFlags()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt64(8 * 1024 * 1024)  // FinalYoungestDesired @0
            .WriteInt32(4)                // NumHeaps @8
            .WriteInt32(2)                // CondemnedGeneration @12
            .WriteInt32(0)                // Gen0ReductionCount @16
            .WriteInt32(4)                // Reason @20 (AllocLarge)
            .WriteInt32(3)                // GlobalMechanisms @24 (Concurrent | Compaction)
            .WriteInt16(1)                // ClrInstanceID @28
            .ToArray();

        ClrGcGlobalHeapHistory history = ClrGcGlobalHeapHistory.Decode(new PayloadReader(payload, 8), version: 1);

        Assert.Equal(8 * 1024 * 1024, history.FinalYoungestDesired);
        Assert.Equal(4, history.NumHeaps);
        Assert.Equal(2, history.CondemnedGeneration);
        Assert.Equal(GCReason.AllocLarge, history.Reason);
        Assert.Equal(GCGlobalMechanisms.Concurrent | GCGlobalMechanisms.Compaction, history.GlobalMechanisms);
        Assert.Equal(1, history.ClrInstanceID);
    }

    [Fact]
    public void ClrGcAllocationTick_DecodesSmallAllocationWithoutTypeNameWhenVersionOne()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt32(100000)  // AllocationAmount @0
            .WriteInt32(0)       // AllocationKind @4 (Small)
            .WriteInt16(1)       // ClrInstanceID @8
            .ToArray();

        ClrGcAllocationTick tick = ClrGcAllocationTick.Decode(new PayloadReader(payload, 8), version: 1);

        Assert.Equal(100000, tick.AllocationAmount);
        Assert.Equal(GCAllocationKind.Small, tick.AllocationKind);
        Assert.Equal(1, tick.ClrInstanceID);
        Assert.Null(tick.TypeName);
        Assert.Equal(0, tick.AllocationAmount64);
    }

    [Fact]
    public void ClrGcAllocationTick_DecodesTypeNameAndHeapIndexWhenVersionTwo()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt32(106928)          // AllocationAmount @0
            .WriteInt32(1)                // AllocationKind @4 (Large)
            .WriteInt16(1)                 // ClrInstanceID @8
            .WriteInt64(106928)             // AllocationAmount64 @10
            .WriteAddress(0xDEADBEEF, 8)     // TypeID (unused pointer) @18
            .WriteUnicodeString("System.Byte[]")  // TypeName @26
            .WriteInt32(2)                        // HeapIndex, immediately after the string
            .ToArray();

        ClrGcAllocationTick tick = ClrGcAllocationTick.Decode(new PayloadReader(payload, 8), version: 2);

        Assert.Equal(106928, tick.AllocationAmount64);
        Assert.Equal(GCAllocationKind.Large, tick.AllocationKind);
        Assert.Equal("System.Byte[]", tick.TypeName);
        Assert.Equal(2, tick.HeapIndex);
    }

    [Fact]
    public void ClrGcAllocationTick_HandlesPinnedKind()
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt32(4096)
            .WriteInt32(2)  // AllocationKind @4 (Pinned)
            .ToArray();

        ClrGcAllocationTick tick = ClrGcAllocationTick.Decode(new PayloadReader(payload, 8), version: 0);

        Assert.Equal(GCAllocationKind.Pinned, tick.AllocationKind);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
