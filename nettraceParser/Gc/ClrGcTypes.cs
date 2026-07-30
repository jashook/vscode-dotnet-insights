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
using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ClrGcEventIds
{
    public const int GCStart = 1;
    public const int GCEnd = 2;
    public const int GCRestartEEEnd = 3;
    public const int GCHeapStats = 4;
    public const int GCSuspendEEEnd = 8;
    public const int GCSuspendEEBegin = 9;
    public const int GCAllocationTick = 10;
    public const int GCPerHeapHistory = 204;
    public const int GCGlobalHeapHistory = 205;
}

// Manifest value, not the C# GCSuspendEEReason enum's member order (which
// happens to match numerically here, but ClrGcSuspendEEBegin.Decode reads
// this as a raw int rather than depending on that coincidence).
public static class GCSuspendEEReason
{
    public const int SuspendOther = 0x0;
    public const int SuspendForGC = 0x1;
    public const int SuspendForAppDomainShutdown = 0x2;
    public const int SuspendForCodePitching = 0x3;
    public const int SuspendForShutdown = 0x4;
    public const int SuspendForDebugger = 0x5;
    public const int SuspendForGCPrep = 0x6;
    public const int SuspendForDebuggerSweep = 0x7;
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

// GCSuspendEEBegin (manifest value 9) - the moment the runtime *requests*
// thread suspension for a GC, which precedes that GC's own GCStart. Field
// layout differs by version (ClrEtwAll.man's "GCSuspendEE"/"GCSuspendEE_V1"
// templates): v0 is a bare UInt16 Reason with no Count; v1 adds Count
// (UInt32) and ClrInstanceID (UInt16) after a widened UInt32 Reason. Only
// Reason is decoded here - see GcEventProjector.Project's PauseStartRelativeMSec/
// PauseDurationMSec comment for why Count isn't needed for correlation.
public class ClrGcSuspendEEBegin
{
    public int Reason;

    public static ClrGcSuspendEEBegin Decode(PayloadReader reader, int version)
    {
        ClrGcSuspendEEBegin data = new ClrGcSuspendEEBegin();
        data.Reason = version >= 1 ? reader.GetInt32At(0) : reader.GetInt16At(0);
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

// A readonly struct, not a class: AllocationEventProjector.Project decodes
// one of these per candidate GCAllocationTick event - 11.9M times for a
// real 5-minute capture, immediately copying its fields into an
// AllocationEvent and discarding it. At ~40 bytes (over the struct-passing
// convention's 16-byte threshold) it's returned by value once per Decode
// call rather than held/passed around, so the one-time copy cost is
// negligible next to the 11.9M heap allocations this replaces.
public readonly struct ClrGcAllocationTick
{
    public readonly int AllocationAmount;
    public readonly GCAllocationKind AllocationKind;
    public readonly int ClrInstanceID;
    public readonly long AllocationAmount64;
    public readonly long TypeID;
    public readonly string TypeName;
    public readonly int HeapIndex;

    private ClrGcAllocationTick(int allocationAmount, GCAllocationKind allocationKind, int clrInstanceID, long allocationAmount64, long typeID, string typeName, int heapIndex)
    {
        this.AllocationAmount = allocationAmount;
        this.AllocationKind = allocationKind;
        this.ClrInstanceID = clrInstanceID;
        this.AllocationAmount64 = allocationAmount64;
        this.TypeID = typeID;
        this.TypeName = typeName;
        this.HeapIndex = heapIndex;
    }

    // typeNameCache is optional (null skips caching entirely, matching the
    // old unconditional-decode behavior) - callers processing a whole
    // capture's worth of ticks should pass one shared Dictionary<long,
    // string> across every Decode call. TypeID (a MethodTable pointer,
    // stable for a type's lifetime in the process) is the same for every
    // tick of a given type, but a real capture with millions of ticks
    // typically has only a handful of distinct allocated types - profiling
    // a real 5-minute/11.9M-tick capture showed Encoding.Unicode.GetString
    // (called unconditionally here before caching existed) as one of the
    // single largest contributors to nettraceParser's wall time, decoding
    // and allocating the same handful of strings millions of times over.
    // SkipUnicodeString still runs unconditionally on a cache hit (to find
    // HeapIndex's offset past the variable-length string) but that's a
    // cheap byte scan for a null terminator, not a decode+allocation - see
    // its own doc comment on PayloadReader.
    public static ClrGcAllocationTick Decode(PayloadReader reader, int version, Dictionary<long, string> typeNameCache = null)
    {
        int allocationAmount = reader.GetInt32At(0);
        GCAllocationKind allocationKind = (GCAllocationKind)reader.GetInt32At(4);
        int clrInstanceID = 0;
        long allocationAmount64 = 0;
        long typeID = 0;
        string typeName = null;
        int heapIndex = 0;

        if (version >= 1 && reader.Length >= 10)
        {
            clrInstanceID = reader.GetInt16At(8);
        }

        if (version >= 2 && reader.Length > 18)
        {
            allocationAmount64 = reader.GetInt64At(10);

            typeID = reader.GetAddressAt(18);

            int typeNameOffset = 18 + reader.PointerSize;

            string cachedTypeName;
            if (typeNameCache != null && typeNameCache.TryGetValue(typeID, out cachedTypeName))
            {
                typeName = cachedTypeName;
            }
            else
            {
                typeName = reader.GetUnicodeStringAt(typeNameOffset);

                if (typeNameCache != null)
                {
                    typeNameCache[typeID] = typeName;
                }
            }

            heapIndex = reader.GetInt32At(reader.SkipUnicodeString(typeNameOffset));
        }

        return new ClrGcAllocationTick(allocationAmount, allocationKind, clrInstanceID, allocationAmount64, typeID, typeName, heapIndex);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
