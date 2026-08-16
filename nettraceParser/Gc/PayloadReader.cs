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
using System.Runtime.InteropServices;
using System.Text;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A readonly struct, not a class: this is a thin, immutable wrapper
// (a byte[] reference + a few ints - over the struct-passing convention's
// 16-byte threshold now that it also carries a base offset/length for the
// shared-buffer slice case, so callers should hold it via `in`/`ref
// readonly` in hot loops rather than copying it repeatedly), and
// AllocationEventProjector.Project constructs one per allocation tick -
// 11.9M times for a real 5-minute capture. No call site relies on
// reference semantics (no null checks, no identity comparisons).
//
// Two constructors:
//  - (payload, pointerSize): the original standalone-array form, still used
//    by every existing unit test that hands this a small dedicated byte[]
//    it built itself (offset 0, full array length).
//  - (payload, offset, length, pointerSize): used by production call sites
//    reading from an EventRecord, whose PayloadBuffer is the whole file's
//    byte array shared across every event (see EventRecord.cs/EventBlock.cs)
//    rather than a copy dedicated to this one event - offset/length mark
//    this event's slice within it. All GetXAt(offset) calls are relative to
//    that slice, not absolute into the shared array.
public readonly struct PayloadReader
{
    private readonly byte[] payload;
    private readonly int baseOffset;
    private readonly int length;
    private readonly int pointerSize;

    public int Length => this.length;
    public int PointerSize => this.pointerSize;

    public PayloadReader(byte[] payload, int pointerSize)
        : this(payload, 0, payload.Length, pointerSize)
    {
    }

    public PayloadReader(byte[] payload, int offset, int length, int pointerSize)
    {
        this.payload = payload;
        this.baseOffset = offset;
        this.length = length;
        this.pointerSize = pointerSize;
    }

    public short GetInt16At(int offset)
    {
        return BitConverter.ToInt16(this.payload, this.baseOffset + offset);
    }

    public int GetInt32At(int offset)
    {
        return BitConverter.ToInt32(this.payload, this.baseOffset + offset);
    }

    public long GetInt64At(int offset)
    {
        return BitConverter.ToInt64(this.payload, this.baseOffset + offset);
    }

    public byte GetByteAt(int offset)
    {
        return this.payload[this.baseOffset + offset];
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
        return Encoding.Unicode.GetString(this.payload, this.baseOffset + offset, endOffset - offset);
    }

    // The same string as GetUnicodeStringAt, but as a view over the payload
    // buffer rather than a newly allocated string. The wire format already
    // stores these as UTF-16, which is exactly what a .NET char is, so no
    // decoding step is needed at all - just a reinterpretation of the same
    // bytes.
    //
    // Callers on a per-event path should use this plus Utf16StringPool rather
    // than GetUnicodeStringAt: a capture with 1.44M exceptions holds only a
    // few dozen distinct type names, and allocating a fresh string per event
    // to represent one of a few dozen values was measured as over half of the
    // exception projection phase on a real 3.23GB capture.
    public ReadOnlySpan<char> GetUnicodeCharsAt(int offset)
    {
        int endOffset = FindUnicodeStringEnd(offset);
        return MemoryMarshal.Cast<byte, char>(
            new ReadOnlySpan<byte>(this.payload, this.baseOffset + offset, endOffset - offset));
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

    // Scans for the UTF-16 null terminator as CHARS, not byte pairs: the
    // byte-at-a-time loop this replaced was 9.1% of the exception projection
    // phase on a real 3.23GB/1.44M-exception capture, where Span<char>.IndexOf
    // is vectorized. Same answer either way, including the "no terminator
    // before the end of the slice" case, which both stop at.
    private int FindUnicodeStringEnd(int offset)
    {
        // Length rounded down to a whole number of chars - an odd trailing
        // byte can't start a char, and the old loop's `endOffset + 1 <
        // this.length` bound ignored it for the same reason.
        int availableChars = (this.length - offset) / 2;
        if (availableChars <= 0)
        {
            return offset;
        }

        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(
            new ReadOnlySpan<byte>(this.payload, this.baseOffset + offset, availableChars * 2));

        int terminatorIndex = chars.IndexOf('\0');
        if (terminatorIndex < 0)
        {
            return offset + (availableChars * 2);
        }

        return offset + (terminatorIndex * 2);
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
