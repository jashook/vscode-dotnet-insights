////////////////////////////////////////////////////////////////////////////////
// Module: V6SpanReader.cs
//
// Notes:
// Cursor over one already-buffered v6 block. Every v6 block is read whole
// before it is decoded (see V6Reader), so unlike the v5 path - which reads
// through FastSerialization's IStreamReader and its 4MB refill buffer - there
// is no partial-read case to handle here and no reason to go through an
// interface call per primitive. A ref struct over ReadOnlySpan<byte> keeps the
// whole thing on the stack.
//
// The primitives differ from v5's in two ways that are easy to get wrong:
//
//   - varuint/varint replace most fixed-width integers. varint is ZIGZAG
//     encoded (NetTraceFormat.md: "result = (value >> 1) ^ (-(value & 1))"),
//     NOT sign-extended, so decoding it as a plain varuint and casting gives
//     a plausible-looking but wrong number for every negative value.
//
//   - Strings are varuint32-length-prefixed UTF-8, where v5 used
//     length-prefixed UTF-16. Utf16StringPool (which exists because the v5
//     wire format is already UTF-16 and can therefore be pooled without
//     decoding) does not apply; v6 names go through V6Utf8StringPool instead.
//
// Bounds are checked once per read against the block's own length rather than
// trusted from the stream: a block's declared BlockSize comes from the file,
// so a truncated or corrupt capture must fail as a caught error rather than
// read into the next block's bytes.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.V6 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public ref struct V6SpanReader
{
    private readonly ReadOnlySpan<byte> buffer;
    private int position;

    public V6SpanReader(ReadOnlySpan<byte> buffer)
    {
        this.buffer = buffer;
        this.position = 0;
    }

    public V6SpanReader(ReadOnlySpan<byte> buffer, int position)
    {
        this.buffer = buffer;
        this.position = position;
    }

    public int Position
    {
        get { return this.position; }
        set { this.position = value; }
    }

    public int Length => this.buffer.Length;

    public bool AtEnd => this.position >= this.buffer.Length;

    public int Remaining => this.buffer.Length - this.position;

    public void Skip(int byteCount)
    {
        this.EnsureAvailable(byteCount);
        this.position += byteCount;
    }

    public byte ReadUInt8()
    {
        this.EnsureAvailable(1);
        byte value = this.buffer[this.position];
        ++this.position;
        return value;
    }

    public short ReadInt16()
    {
        this.EnsureAvailable(2);
        short value = BinaryPrimitives.ReadInt16LittleEndian(this.buffer.Slice(this.position));
        this.position += 2;
        return value;
    }

    public ushort ReadUInt16()
    {
        this.EnsureAvailable(2);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(this.buffer.Slice(this.position));
        this.position += 2;
        return value;
    }

    public int ReadInt32()
    {
        this.EnsureAvailable(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(this.buffer.Slice(this.position));
        this.position += 4;
        return value;
    }

    public uint ReadUInt32()
    {
        this.EnsureAvailable(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(this.buffer.Slice(this.position));
        this.position += 4;
        return value;
    }

    public long ReadInt64()
    {
        this.EnsureAvailable(8);
        long value = BinaryPrimitives.ReadInt64LittleEndian(this.buffer.Slice(this.position));
        this.position += 8;
        return value;
    }

    public ulong ReadUInt64()
    {
        this.EnsureAvailable(8);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(this.buffer.Slice(this.position));
        this.position += 8;
        return value;
    }

    // ULEB128: low 7 bits carry the value, the high bit means "another byte
    // follows". The shift is capped at 63 so a corrupt run of continuation
    // bytes cannot spin past the end of a ulong (or, before the cap, silently
    // shift the accumulated value back off the bottom).
    public ulong ReadVarUInt64()
    {
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            byte current = this.ReadUInt8();
            result |= ((ulong)(current & 0x7F)) << shift;

            if ((current & 0x80) == 0)
            {
                return result;
            }

            shift += 7;

            if (shift > 63)
            {
                throw new InvalidOperationException("Malformed varuint: more than 10 continuation bytes.");
            }
        }
    }

    public uint ReadVarUInt32()
    {
        return (uint)this.ReadVarUInt64();
    }

    public int ReadVarInt32()
    {
        return (int)this.ReadVarInt64();
    }

    // Zigzag, per NetTraceFormat.md. Decoding this as a plain varuint is the
    // easy mistake and is wrong for every negative value.
    public long ReadVarInt64()
    {
        ulong encoded = this.ReadVarUInt64();
        return (long)(encoded >> 1) ^ -((long)(encoded & 1));
    }

    // The raw UTF-8 bytes, not a string. Callers that want a string go
    // through V6Utf8StringPool so repeated names (every provider name, every
    // method name) collapse to one instance - see that file.
    public ReadOnlySpan<byte> ReadStringBytes()
    {
        int byteCount = (int)this.ReadVarUInt32();
        this.EnsureAvailable(byteCount);
        ReadOnlySpan<byte> value = this.buffer.Slice(this.position, byteCount);
        this.position += byteCount;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int byteCount)
    {
        this.EnsureAvailable(byteCount);
        ReadOnlySpan<byte> value = this.buffer.Slice(this.position, byteCount);
        this.position += byteCount;
        return value;
    }

    public Guid ReadGuid()
    {
        this.EnsureAvailable(16);
        Guid value = new Guid(this.buffer.Slice(this.position, 16));
        this.position += 16;
        return value;
    }

    private void EnsureAvailable(int byteCount)
    {
        if (byteCount < 0 || this.position + byteCount > this.buffer.Length)
        {
            throw new InvalidOperationException(
                $"v6 block read past its end (position {this.position}, wanted {byteCount} bytes, block is {this.buffer.Length} bytes).");
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.V6)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
