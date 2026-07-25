////////////////////////////////////////////////////////////////////////////////
// Module: ClrGcTypes.cs
//
// Notes:
// Enums and per-event-type decoders for the CLR runtime provider's GC events,
// hardcoded from the official CLR ETW manifest (as embedded in TraceEvent's
// ClrTraceEventParser.cs GCStartTraceData/GCEndTraceData/GCHeapStatsTraceData/
// GCGlobalHeapHistoryTraceData/GCAllocationTickTraceData classes - read as
// reference, not taken as a dependency). These events are not self-describing
// in the trace's own metadata, so there is no way to decode them generically -
// each type's field offsets are fixed by the manifest and versioned by the
// event's own Version number, exactly like TraceEvent's GetXAt(offset) pattern.
//
// GCPerHeapHistory (per-heap, per-generation breakdown) lives in its own file,
// Gc/ClrGcPerHeapHistory.cs - its manifest layout is substantially larger and
// more version-dependent than the events here.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Gc {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ClrGcEventIds
{
    public const int GCStart = 1;
    public const int GCEnd = 2;
    public const int GCHeapStats = 4;
    public const int GCAllocationTick = 10;
    public const int GCPerHeapHistory = 204;
    public const int GCGlobalHeapHistory = 205;
}

public enum GCReason
{
    AllocSmall = 0x0,
    Induced = 0x1,
    LowMemory = 0x2,
    Empty = 0x3,
    AllocLarge = 0x4,
    OutOfSpaceSOH = 0x5,
    OutOfSpaceLOH = 0x6,
    InducedNotForced = 0x7,
    Internal = 0x8,
    InducedLowMemory = 0x9,
    InducedCompacting = 0xa,
    LowMemoryHost = 0xb,
    PMFullGC = 0xc,
    LowMemoryHostBlocking = 0xd
}

public enum GCType
{
    NonConcurrentGC = 0x0,
    BackgroundGC = 0x1,
    ForegroundGC = 0x2,
}

public enum GCAllocationKind
{
    Small = 0x0,
    Large = 0x1,
    Pinned = 0x2
}

[Flags]
public enum GCGlobalMechanisms
{
    None = 0,
    Concurrent = 0x1,
    Compaction = 0x2,
    Promotion = 0x4,
    Demotion = 0x8,
    CardBundles = 0x10,
}

public class ClrGcStart
{
    public int Count;
    public GCReason Reason;
    public int Depth;
    public GCType Type;
    public int ClrInstanceID;

    public static ClrGcStart Decode(PayloadReader reader, int version)
    {
        ClrGcStart data = new ClrGcStart();
        data.Count = reader.GetInt32At(0);

        if (reader.Length >= 16)
        {
            data.Reason = (GCReason)reader.GetInt32At(8);
            data.Depth = reader.GetInt32At(4);
            data.Type = (GCType)reader.GetInt32At(12);
        }

        if (version >= 1 && reader.Length >= 18)
        {
            data.ClrInstanceID = reader.GetInt16At(16);
        }

        return data;
    }
}

public class ClrGcEnd
{
    public int Count;
    public int Depth;
    public int ClrInstanceID;

    public static ClrGcEnd Decode(PayloadReader reader, int version)
    {
        ClrGcEnd data = new ClrGcEnd();
        data.Count = reader.GetInt32At(0);
        data.Depth = version >= 1 ? reader.GetInt32At(4) : reader.GetInt16At(4);

        if (version >= 1 && reader.Length >= 10)
        {
            data.ClrInstanceID = reader.GetInt16At(8);
        }

        return data;
    }
}

public class ClrGcHeapStats
{
    public long GenerationSize0;
    public long TotalPromotedSize0;
    public long GenerationSize1;
    public long TotalPromotedSize1;
    public long GenerationSize2;
    public long TotalPromotedSize2;
    public long GenerationSize3;
    public long TotalPromotedSize3;
    public long GenerationSize4;
    public long TotalPromotedSize4;
    public long FinalizationPromotedSize;
    public long FinalizationPromotedCount;
    public int PinnedObjectCount;
    public int SinkBlockCount;
    public int GCHandleCount;
    public int ClrInstanceID;

    public long TotalHeapSize => this.GenerationSize0 + this.GenerationSize1 + this.GenerationSize2 + this.GenerationSize3 + this.GenerationSize4;
    public long TotalPromoted => this.TotalPromotedSize0 + this.TotalPromotedSize1 + this.TotalPromotedSize2 + this.TotalPromotedSize3 + this.TotalPromotedSize4;

    public static ClrGcHeapStats Decode(PayloadReader reader, int version)
    {
        ClrGcHeapStats data = new ClrGcHeapStats();
        data.GenerationSize0 = reader.GetInt64At(0);
        data.TotalPromotedSize0 = reader.GetInt64At(8);
        data.GenerationSize1 = reader.GetInt64At(16);
        data.TotalPromotedSize1 = reader.GetInt64At(24);
        data.GenerationSize2 = reader.GetInt64At(32);
        data.TotalPromotedSize2 = reader.GetInt64At(40);
        data.GenerationSize3 = reader.GetInt64At(48);
        data.TotalPromotedSize3 = reader.GetInt64At(56);
        data.FinalizationPromotedSize = reader.GetInt64At(64);
        data.FinalizationPromotedCount = reader.GetInt64At(72);
        data.PinnedObjectCount = reader.GetInt32At(80);
        data.SinkBlockCount = reader.GetInt32At(84);
        data.GCHandleCount = reader.GetInt32At(88);

        if (version >= 1 && reader.Length > 92)
        {
            data.ClrInstanceID = reader.GetInt16At(92);
        }

        if (version >= 2 && reader.Length >= 110)
        {
            data.GenerationSize4 = reader.GetInt64At(94);
            data.TotalPromotedSize4 = reader.GetInt64At(102);
        }

        return data;
    }
}

public class ClrGcGlobalHeapHistory
{
    public long FinalYoungestDesired;
    public int NumHeaps;
    public int CondemnedGeneration;
    public int Gen0ReductionCount;
    public GCReason Reason;
    public GCGlobalMechanisms GlobalMechanisms;
    public int ClrInstanceID;

    public static ClrGcGlobalHeapHistory Decode(PayloadReader reader, int version)
    {
        ClrGcGlobalHeapHistory data = new ClrGcGlobalHeapHistory();
        data.FinalYoungestDesired = reader.GetInt64At(0);
        data.NumHeaps = reader.GetInt32At(8);
        data.CondemnedGeneration = reader.GetInt32At(12);
        data.Gen0ReductionCount = reader.GetInt32At(16);
        data.Reason = (GCReason)reader.GetInt32At(20);
        data.GlobalMechanisms = (GCGlobalMechanisms)reader.GetInt32At(24);

        if (version >= 1 && reader.Length >= 30)
        {
            data.ClrInstanceID = reader.GetInt16At(28);
        }

        return data;
    }
}

public class ClrGcAllocationTick
{
    public int AllocationAmount;
    public GCAllocationKind AllocationKind;
    public int ClrInstanceID;
    public long AllocationAmount64;
    public string TypeName;
    public int HeapIndex;

    public static ClrGcAllocationTick Decode(PayloadReader reader, int version)
    {
        ClrGcAllocationTick data = new ClrGcAllocationTick();
        data.AllocationAmount = reader.GetInt32At(0);
        data.AllocationKind = (GCAllocationKind)reader.GetInt32At(4);

        if (version >= 1 && reader.Length >= 10)
        {
            data.ClrInstanceID = reader.GetInt16At(8);
        }

        if (version >= 2 && reader.Length > 18)
        {
            data.AllocationAmount64 = reader.GetInt64At(10);

            // TypeID (pointer-sized, unused here) at offset 18; TypeName follows immediately.
            int typeNameOffset = 18 + reader.PointerSize;
            data.TypeName = reader.GetUnicodeStringAt(typeNameOffset);
            data.HeapIndex = reader.GetInt32At(reader.SkipUnicodeString(typeNameOffset));
        }

        return data;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
