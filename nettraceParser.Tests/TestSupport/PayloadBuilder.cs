////////////////////////////////////////////////////////////////////////////////
// Module: PayloadBuilder.cs
//
// Notes:
// Small fluent builder for constructing synthetic CLR-provider event
// payloads in tests - the real decoders (Gc/ClrGcTypes.cs,
// Gc/ClrGcPerHeapHistory.cs) read fixed byte offsets out of a raw byte[],
// and hand-computing those offsets inline in every test would be tedious
// and error-prone. This mirrors PayloadReader's own field-width semantics
// (WriteAddress matches GetAddressAt's 4-byte-vs-8-byte-by-pointerSize
// behavior, WriteUnicodeString null-terminates like GetUnicodeStringAt
// expects) so a built payload round-trips correctly through the real
// decoder under test.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class PayloadBuilder
{
    private readonly List<byte> bytes = new List<byte>();

    public PayloadBuilder WriteByte(byte value)
    {
        this.bytes.Add(value);
        return this;
    }

    public PayloadBuilder WriteInt16(short value)
    {
        this.bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    public PayloadBuilder WriteInt32(int value)
    {
        this.bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    public PayloadBuilder WriteInt64(long value)
    {
        this.bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    // Pointer-sized field - 4 bytes on a 32-bit trace, 8 bytes on a 64-bit
    // trace, matching PayloadReader.GetAddressAt.
    public PayloadBuilder WriteAddress(long value, int pointerSize)
    {
        if (pointerSize == 4)
        {
            this.bytes.AddRange(BitConverter.GetBytes((int)value));
        }
        else
        {
            this.bytes.AddRange(BitConverter.GetBytes(value));
        }

        return this;
    }

    public PayloadBuilder WriteUnicodeString(string value)
    {
        this.bytes.AddRange(Encoding.Unicode.GetBytes(value));
        this.bytes.AddRange(BitConverter.GetBytes((short)0));
        return this;
    }

    public PayloadBuilder Pad(int byteCount)
    {
        for (int index = 0; index < byteCount; ++index)
        {
            this.bytes.Add(0);
        }

        return this;
    }

    public byte[] ToArray()
    {
        return this.bytes.ToArray();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
