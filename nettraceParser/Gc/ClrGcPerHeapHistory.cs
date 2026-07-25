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

// Field names/semantics match gcEventListener/HelperClasses.cs's Generation
// class exactly - both ultimately derive from the same CLR ETW manifest.
public class ClrGcGeneration
{
    public long SizeBefore;
    public long SizeAfter;
    public long ObjSpaceBefore;
    public long Fragmentation;
    public long FreeListSpaceBefore;
    public long FreeListSpaceAfter;
    public long FreeObjSpaceBefore;
    public long FreeObjSpaceAfter;
    public long ObjSizeAfter;
    public long In;
    public long Out;
    public long NewAllocation;
    public long SurvRate;
    public long PinnedSurv;
    public long NonePinnedSurv;

    // genDataArray holds the 10 pointer-sized fields for one generation, in
    // the Version>=3 GCPerHeapHistoryGenData order.
    public static ClrGcGeneration Decode(long[] genDataArray)
    {
        ClrGcGeneration gen = new ClrGcGeneration();
        gen.SizeBefore = genDataArray[0];
        gen.FreeListSpaceBefore = genDataArray[1];
        gen.FreeObjSpaceBefore = genDataArray[2];
        gen.SizeAfter = genDataArray[3];
        gen.FreeListSpaceAfter = genDataArray[4];
        gen.FreeObjSpaceAfter = genDataArray[5];
        gen.In = genDataArray[6];
        gen.PinnedSurv = genDataArray[7];
        gen.NonePinnedSurv = genDataArray[8];
        gen.NewAllocation = genDataArray[9]; // "Budget" in TraceEvent - historically renamed NewAllocation for the XML/JSON output.

        gen.ObjSpaceBefore = gen.SizeBefore - gen.FreeListSpaceBefore - gen.FreeObjSpaceBefore;
        gen.Fragmentation = gen.FreeListSpaceAfter + gen.FreeObjSpaceAfter;
        gen.ObjSizeAfter = gen.SizeAfter - gen.Fragmentation;
        gen.Out = gen.PinnedSurv + gen.NonePinnedSurv;
        gen.SurvRate = gen.ObjSpaceBefore == 0 ? 0 : (long)((double)gen.Out * 100.0 / (double)gen.ObjSpaceBefore);

        return gen;
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

        for (int genIndex = 0; genIndex < generationsToDecode; ++genIndex)
        {
            int genOffset = genDataStart + (sizeOfGenData * genIndex);
            long[] genDataArray = new long[10];

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
