////////////////////////////////////////////////////////////////////////////////
// Module: ClrGcPerHeapHistory.cs
//
// Notes:
// Decoder for the CLR runtime provider's GCPerHeapHistory event: one heap's
// per-generation breakdown for a single GC (a GC with N heaps fires N of
// these). Offsets hardcoded from TraceEvent's ClrTraceEventParser.cs
// (GCPerHeapHistoryTraceData / GCPerHeapHistoryGenData - read as reference,
// not a dependency), Version >= 3 only - verified against a real capture
// (EventId=204, Version=3, payload length exactly 486 bytes, which matches
// this layout's computed size with Count=5 generations).
//
// Only Version >= 3 is implemented: Version 0/2 (older, pre-.NET Core 3.x-ish
// runtimes) use entirely different offset formulas that can't be verified
// against any capture available here. Decode() returns null for those, which
// GcEventProjector treats the same as any other unrecognized/skipped event.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Gc {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Field names/semantics match gcEventListener/HelperClasses.cs's Generation
// class exactly - both ultimately derive from the same CLR ETW manifest.
//
// A readonly struct, not a class: ClrGcHeap.Generations is an array of
// these (up to 4 per heap, 8 heaps, one call per GC - ~274,000 instances
// for a real 5-minute capture), so this array is now one contiguous
// allocation instead of N separate small-object allocations. At 120 bytes
// (15 longs) this is well over the struct-passing convention's 16-byte
// threshold - callers reading many fields in a loop (GcJsonExporter.cs,
// Program.cs's debug dump) should hold it via a `ref readonly` local
// rather than copying it out of the array.
public readonly struct ClrGcGeneration
{
    public readonly long SizeBefore;
    public readonly long SizeAfter;
    public readonly long ObjSpaceBefore;
    public readonly long Fragmentation;
    public readonly long FreeListSpaceBefore;
    public readonly long FreeListSpaceAfter;
    public readonly long FreeObjSpaceBefore;
    public readonly long FreeObjSpaceAfter;
    public readonly long ObjSizeAfter;
    public readonly long In;
    public readonly long Out;
    public readonly long NewAllocation;
    public readonly long SurvRate;
    public readonly long PinnedSurv;
    public readonly long NonePinnedSurv;

    // genData holds the 10 pointer-sized fields for one generation, in the
    // Version>=3 GCPerHeapHistoryGenData order.
    public ClrGcGeneration(ReadOnlySpan<long> genData)
    {
        this.SizeBefore = genData[0];
        this.FreeListSpaceBefore = genData[1];
        this.FreeObjSpaceBefore = genData[2];
        this.SizeAfter = genData[3];
        this.FreeListSpaceAfter = genData[4];
        this.FreeObjSpaceAfter = genData[5];
        this.In = genData[6];
        this.PinnedSurv = genData[7];
        this.NonePinnedSurv = genData[8];
        this.NewAllocation = genData[9]; // "Budget" in TraceEvent - historically renamed NewAllocation for the XML/JSON output.

        this.ObjSpaceBefore = this.SizeBefore - this.FreeListSpaceBefore - this.FreeObjSpaceBefore;
        this.Fragmentation = this.FreeListSpaceAfter + this.FreeObjSpaceAfter;
        this.ObjSizeAfter = this.SizeAfter - this.Fragmentation;
        this.Out = this.PinnedSurv + this.NonePinnedSurv;
        this.SurvRate = this.ObjSpaceBefore == 0 ? 0 : (long)((double)this.Out * 100.0 / (double)this.ObjSpaceBefore);
    }

    public static ClrGcGeneration Decode(ReadOnlySpan<long> genData)
    {
        return new ClrGcGeneration(genData);
    }
}

public class ClrGcHeap
{
    public int HeapIndex;
    public int ClrInstanceID;

    // Index 0-3 = Gen0, Gen1, Gen2, LOH (matching the existing .gcinfo rendering's
    // Generations:{0,1,2,3} object). Generation index 4 (the "second pass gen0"
    // slot the manifest also reports) is decoded but not exposed here - unused
    // by the existing rendering.
    public ClrGcGeneration[] Generations;

    public static ClrGcHeap Decode(PayloadReader reader, int version)
    {
        if (version < 3)
        {
            return null;
        }

        ClrGcHeap heap = new ClrGcHeap();
        heap.ClrInstanceID = reader.GetInt16At(0);
        heap.HeapIndex = reader.GetInt32At(reader.HostOffset(46, 6));

        int count = reader.GetInt32At(reader.HostOffset(54, 7));
        int genDataStart = reader.HostOffset(54, 7) + 4;
        int sizeOfGenData = reader.PointerSize * 10;

        int generationsToDecode = count < 4 ? count : 4;
        heap.Generations = new ClrGcGeneration[generationsToDecode];

        // Declared once, outside the loop (never stackalloc inside a loop -
        // see CLAUDE.md's stackalloc convention), and fully overwritten
        // then consumed each iteration before the next one starts, so
        // reusing the same 80-byte stack buffer across all
        // (at most 4) generations is safe.
        Span<long> genDataArray = stackalloc long[10];

        for (int genIndex = 0; genIndex < generationsToDecode; ++genIndex)
        {
            int genOffset = genDataStart + (sizeOfGenData * genIndex);

            for (int entryIndex = 0; entryIndex < 10; ++entryIndex)
            {
                genDataArray[entryIndex] = reader.GetAddressAt(genOffset + (entryIndex * reader.PointerSize));
            }

            heap.Generations[genIndex] = ClrGcGeneration.Decode(genDataArray);
        }

        return heap;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
