////////////////////////////////////////////////////////////////////////////////
// Module: ClrContentionTypes.cs
//
// Notes:
// EventIds and payload structs for CLR contention events: Contention/Start
// (EventId 81) and Contention/Stop (EventId 91). Hardcoded from TraceEvent's
// own ClrTraceEventParser.cs ContentionStartTraceData / ContentionStopTraceData
// - read as reference, not taken as a dependency, same convention as
// Exceptions/ClrExceptionTypes.cs and Gc/ClrGcTypes.cs. These events are not
// self-describing in the trace's own metadata, so offsets are hardcoded.
//
// ContentionStop carries DurationNs (double at offset 3), the CLR's own
// measurement of the lock wait in nanoseconds. This is more accurate than
// computing a QPC delta between Start and Stop because it uses the CLR's
// internal high-resolution timer, not the external sampling clock.
//
// Payload layouts (verified against TraceEvent's own source, then confirmed
// byte-for-byte against a real capture - see ClrContentionStart.Decode):
//   ContentionStart V1: ContentionFlags (byte at 0), ClrInstanceID (short at 1)
//   ContentionStart V2: the V1 fields plus LockID (pointer at 3),
//                       AssociatedObjectID (pointer at 11),
//                       LockOwnerThreadID (UInt64 at 19) - 27 bytes total on
//                       a 64-bit process.
//   ContentionStop:  ContentionFlags (byte at 0), ClrInstanceID (short at 1),
//                    DurationNs (double at 3)
//
// Note ContentionStop stays V1 (11 bytes) even when Start is V2 - verified on
// a real .NET 9 capture - so a wait's lock identity is only ever known from
// its Start event, never its Stop. ContentionEventProjector already pairs the
// two by thread id, so the Start's fields are what get carried forward.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Contention {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

using DotnetInsights.NetTrace.Gc;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ClrContentionEventIds
{
    public const int ContentionStart = 81;
    public const int ContentionStop = 91;
}

// Managed = 0: contention on a managed Monitor lock (the common case).
// Native = 1: contention on a native OS primitive. Most .NET profiling
// sessions only see Managed; Native requires additional CLR keywords.
[Flags]
public enum ClrContentionFlags : byte
{
    Managed = 0,
    Native = 1
}

public readonly struct ClrContentionStart
{
    public readonly ClrContentionFlags ContentionFlags;
    public readonly short ClrInstanceID;
    // V2-only (0 on a V1 payload). LockID identifies the lock itself and is
    // what the Lock Timeline view groups by; AssociatedObjectID is the
    // managed object the lock is attached to (kept distinct because the two
    // don't map 1:1 - a real capture showed 1447 LockIDs against 1462
    // AssociatedObjectIDs).
    public readonly long LockID;
    public readonly long AssociatedObjectID;
    // The thread that HELD the lock while this event's own thread waited for
    // it. 0 when the runtime couldn't attribute an owner (~12% of waits on a
    // real capture) - callers must treat 0 as "unknown", not as a thread id.
    public readonly long LockOwnerThreadID;

    private ClrContentionStart(ClrContentionFlags contentionFlags, short clrInstanceID, long lockID, long associatedObjectID, long lockOwnerThreadID)
    {
        this.ContentionFlags = contentionFlags;
        this.ClrInstanceID = clrInstanceID;
        this.LockID = lockID;
        this.AssociatedObjectID = associatedObjectID;
        this.LockOwnerThreadID = lockOwnerThreadID;
    }

    // Version/length gating mirrors ClrExceptionThrown.Decode's own precedent
    // (check both the version AND a real remaining-length check rather than
    // trusting version alone). The V2 layout was confirmed against a real
    // .NET 9 capture by hand-decoding raw payload bytes: every
    // ContentionStart there was Version=2 with PayloadLength=27, which is
    // exactly 1 (flags) + 2 (ClrInstanceID) + 8 + 8 + 8 on a 64-bit process,
    // and the decoded LockOwnerThreadID values matched real thread ids
    // present elsewhere in the same trace.
    public static ClrContentionStart Decode(PayloadReader reader, int version)
    {
        ClrContentionFlags contentionFlags = (ClrContentionFlags)reader.GetByteAt(0);
        short clrInstanceID = reader.GetInt16At(1);

        long lockID = 0;
        long associatedObjectID = 0;
        long lockOwnerThreadID = 0;

        int v2FieldsLength = 3 + reader.PointerSize + reader.PointerSize + 8;

        if (version >= 2 && reader.Length >= v2FieldsLength)
        {
            lockID = reader.GetAddressAt(3);
            associatedObjectID = reader.GetAddressAt(reader.HostOffset(3 + 4, 1));
            lockOwnerThreadID = reader.GetInt64At(reader.HostOffset(3 + 8, 2));
        }

        return new ClrContentionStart(contentionFlags, clrInstanceID, lockID, associatedObjectID, lockOwnerThreadID);
    }
}

public readonly struct ClrContentionStop
{
    public readonly ClrContentionFlags ContentionFlags;
    public readonly short ClrInstanceID;
    // Lock wait duration in nanoseconds - the CLR's own measurement, not a
    // QPC-derived delta. Divide by 1e6 to convert to milliseconds.
    public readonly double DurationNs;

    private ClrContentionStop(ClrContentionFlags contentionFlags, short clrInstanceID, double durationNs)
    {
        this.ContentionFlags = contentionFlags;
        this.ClrInstanceID = clrInstanceID;
        this.DurationNs = durationNs;
    }

    public static ClrContentionStop Decode(PayloadReader reader)
    {
        ClrContentionFlags contentionFlags = (ClrContentionFlags)reader.GetByteAt(0);
        short clrInstanceID = reader.GetInt16At(1);
        double durationNs = 0;

        // byte (1) + short (2) + double (8) = 11 bytes minimum for DurationNs
        if (reader.Length >= 11)
        {
            durationNs = BitConverter.Int64BitsToDouble(reader.GetInt64At(3));
        }

        return new ClrContentionStop(contentionFlags, clrInstanceID, durationNs);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Contention)
