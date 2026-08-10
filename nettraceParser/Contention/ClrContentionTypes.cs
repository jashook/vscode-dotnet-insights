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
// Payload layouts (verified against TraceEvent's own source):
//   ContentionStart: ContentionFlags (byte at 0), ClrInstanceID (short at 1)
//   ContentionStop:  ContentionFlags (byte at 0), ClrInstanceID (short at 1),
//                    DurationNs (double at 3)
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

    private ClrContentionStart(ClrContentionFlags contentionFlags, short clrInstanceID)
    {
        this.ContentionFlags = contentionFlags;
        this.ClrInstanceID = clrInstanceID;
    }

    public static ClrContentionStart Decode(PayloadReader reader)
    {
        ClrContentionFlags contentionFlags = (ClrContentionFlags)reader.GetByteAt(0);
        short clrInstanceID = reader.GetInt16At(1);
        return new ClrContentionStart(contentionFlags, clrInstanceID);
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
