////////////////////////////////////////////////////////////////////////////////
// Module: PayloadReader.cs
//
// Notes:
// Little-endian fixed-offset reads over a raw event payload byte array,
// mirroring the GetInt32At/GetInt64At/GetAddressAt/GetUnicodeStringAt helpers
// TraceEvent's hand-written CLR event classes (ClrTraceEventParser.cs) use to
// decode the manifest-defined (non-self-describing) CLR provider events.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Gc {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Text;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class PayloadReader
{
    private readonly byte[] payload;
    private readonly int pointerSize;

    public int Length => this.payload.Length;
    public int PointerSize => this.pointerSize;

    public PayloadReader(byte[] payload, int pointerSize)
    {
        this.payload = payload;
        this.pointerSize = pointerSize;
    }

    public short GetInt16At(int offset)
    {
        return BitConverter.ToInt16(this.payload, offset);
    }

    public int GetInt32At(int offset)
    {
        return BitConverter.ToInt32(this.payload, offset);
    }

    public long GetInt64At(int offset)
    {
        return BitConverter.ToInt64(this.payload, offset);
    }

    public byte GetByteAt(int offset)
    {
        return this.payload[offset];
    }

    // TraceEvent's "Address" fields are pointer-sized: 4 bytes on a 32-bit
    // trace, 8 bytes on a 64-bit trace.
    public long GetAddressAt(int offset)
    {
        if (this.pointerSize == 4)
        {
            return (uint)GetInt32At(offset);
        }

        return GetInt64At(offset);
    }

    public string GetUnicodeStringAt(int offset)
    {
        int endOffset = FindUnicodeStringEnd(offset);
        return Encoding.Unicode.GetString(this.payload, offset, endOffset - offset);
    }

    // Byte offset immediately after a null-terminated UTF-16 string starting
    // at offset. Scans for the terminator directly rather than decoding the
    // string via GetUnicodeStringAt and measuring its length - callers that
    // only need to skip past a string they've already decoded (or never
    // need the contents of at all, e.g. ClrGcTypes.cs's TypeName) shouldn't
    // pay for an Encoding.Unicode.GetString allocation just to find an
    // offset.
    public int SkipUnicodeString(int offset)
    {
        return FindUnicodeStringEnd(offset) + 2;
    }

    private int FindUnicodeStringEnd(int offset)
    {
        int endOffset = offset;

        while (endOffset + 1 < this.payload.Length && (this.payload[endOffset] != 0 || this.payload[endOffset + 1] != 0))
        {
            endOffset += 2;
        }

        return endOffset;
    }

    public int HostOffset(int offsetAssuming4ByteHost, int numberOfPointersConsumedSoFar)
    {
        return offsetAssuming4ByteHost + (numberOfPointersConsumedSoFar * (this.pointerSize - 4));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
