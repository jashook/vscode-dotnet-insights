////////////////////////////////////////////////////////////////////////////////
// Module: ClrExceptionTypes.cs
//
// Notes:
// Enum + payload decoder for the CLR runtime provider's ExceptionThrown_V1
// event (Task "Exception", Opcode "Start"), hardcoded from TraceEvent's own
// ClrTraceEventParser.cs ExceptionTraceData class - read as reference, not
// taken as a dependency, same convention as Gc/ClrGcTypes.cs. This event is
// not self-describing in the trace's own metadata (EventMetadata.Fields is
// empty for every CLR provider event - see EventBlock.cs), so there is no
// way to decode it generically.
//
// Every offset/field below was cross-checked against a real capture
// (testApps/ExceptionLoadGenerator/example-exceptions.nettrace) by hand-
// decoding its raw payload bytes and comparing against
// Microsoft.Diagnostics.Tracing.TraceEvent's own decoded property values -
// not assumed from a decompiled manifest alone. Confirmed: Version=1 on
// every event a current .NET runtime actually emits, ExceptionFlags'
// numeric values match TraceEvent's ExceptionThrownFlags exactly, and
// ClrInstanceID is 2 bytes on the wire (TraceEvent's own ClrInstanceID
// property widens it to Int32, same relationship as ClrGcStart.ClrInstanceID
// elsewhere in this codebase).
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Exceptions {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

using DotnetInsights.NetTrace.Gc;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ClrExceptionEventIds
{
    public const int ExceptionThrown = 80;
}

// Manifest bit values (TraceEvent's own ExceptionThrownFlags), not this
// enum's declaration order - ClrExceptionThrown.Decode reads this as a raw
// Int16 rather than depending on a coincidental match.
[Flags]
public enum ClrExceptionFlags
{
    None = 0x0,
    HasInnerException = 0x1,
    Nested = 0x2,
    ReThrown = 0x4,
    CorruptedState = 0x8,
    CLSCompliant = 0x10
}

// A readonly struct, not a class - ExceptionEventProjector.Project constructs
// one per ExceptionThrown_V1 event and immediately copies its fields into an
// ExceptionEvent, discarding it, same short-lived-per-call-value pattern as
// Gc/ClrGcTypes.cs's ClrGcAllocationTick.
public readonly struct ClrExceptionThrown
{
    public readonly string ExceptionType;
    public readonly string ExceptionMessage;
    public readonly long ExceptionEIP;
    public readonly int ExceptionHRESULT;
    public readonly ClrExceptionFlags ExceptionFlags;
    public readonly short ClrInstanceID;

    private ClrExceptionThrown(string exceptionType, string exceptionMessage, long exceptionEIP, int exceptionHRESULT, ClrExceptionFlags exceptionFlags, short clrInstanceID)
    {
        this.ExceptionType = exceptionType;
        this.ExceptionMessage = exceptionMessage;
        this.ExceptionEIP = exceptionEIP;
        this.ExceptionHRESULT = exceptionHRESULT;
        this.ExceptionFlags = exceptionFlags;
        this.ClrInstanceID = clrInstanceID;
    }

    public static ClrExceptionThrown Decode(PayloadReader reader, int version)
    {
        string exceptionType = reader.GetUnicodeStringAt(0);
        int messageOffset = reader.SkipUnicodeString(0);
        string exceptionMessage = reader.GetUnicodeStringAt(messageOffset);
        int fixedFieldsOffset = reader.SkipUnicodeString(messageOffset);

        long exceptionEIP = 0;
        int exceptionHRESULT = 0;
        ClrExceptionFlags exceptionFlags = ClrExceptionFlags.None;
        short clrInstanceID = 0;

        // fixedFieldsOffset + pointerSize (EIP) + 4 (HRESULT) + 2 (Flags) +
        // 2 (ClrInstanceID) is the full payload length for a Version >= 1
        // event - matches the AllocationTick precedent of gating on both
        // version and a real remaining-length check rather than trusting
        // version alone.
        if (version >= 1 && reader.Length >= fixedFieldsOffset + reader.PointerSize + 8)
        {
            exceptionEIP = reader.GetAddressAt(fixedFieldsOffset);
            exceptionHRESULT = reader.GetInt32At(reader.HostOffset(fixedFieldsOffset + 4, 1));
            exceptionFlags = (ClrExceptionFlags)reader.GetInt16At(reader.HostOffset(fixedFieldsOffset + 8, 1));
            clrInstanceID = reader.GetInt16At(reader.HostOffset(fixedFieldsOffset + 10, 1));
        }

        return new ClrExceptionThrown(exceptionType, exceptionMessage, exceptionEIP, exceptionHRESULT, exceptionFlags, clrInstanceID);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Exceptions)
