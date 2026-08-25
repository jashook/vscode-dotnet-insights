////////////////////////////////////////////////////////////////////////////////
// Module: PerfSpanReader.cs
//
// Notes:
// Cursor over an already-buffered slice of a perf.data file. Same shape and
// same reasoning as nettraceParser's V6/V6SpanReader.cs: the whole capture is
// resident, so there is no partial-read case and no reason to pay an interface
// call per primitive. A ref struct over ReadOnlySpan<byte> keeps it on the
// stack.
//
// Everything in perf.data is native-endian FIXED width - there is no varint
// layer - so this is deliberately much smaller than its v6 counterpart. Bounds
// are checked against the slice rather than trusted, because every length that
// drives them (a record's `size`, a raw tracepoint payload's `size`) is read
// from the file: a truncated capture must fail as a reported error rather than
// read into the next record's bytes.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.PerfData {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public ref struct PerfSpanReader
{
    private readonly ReadOnlySpan<byte> buffer;
    private int position;
    private bool overran;

    public PerfSpanReader(ReadOnlySpan<byte> buffer)
    {
        this.buffer = buffer;
        this.position = 0;
        this.overran = false;
    }

    public int Position
    {
        get { return this.position; }
        set { this.position = value; }
    }

    public int Length => this.buffer.Length;

    public int Remaining => this.buffer.Length - this.position;

    public bool AtEnd => this.position >= this.buffer.Length;

    // Sticky, and checked once at the end of a record rather than after every
    // field. A malformed record should be reported and skipped, not thrown
    // from - see this project's "errors on the stack" convention.
    public bool Overran => this.overran;

    private bool Take(int byteCount)
    {
        if (byteCount < 0 || this.position + byteCount > this.buffer.Length)
        {
            this.overran = true;
            return false;
        }

        return true;
    }

    public void Skip(int byteCount)
    {
        if (!this.Take(byteCount))
        {
            this.position = this.buffer.Length;
            return;
        }

        this.position += byteCount;
    }

    public byte ReadUInt8()
    {
        if (!this.Take(1))
        {
            return 0;
        }

        byte value = this.buffer[this.position];
        ++this.position;
        return value;
    }

    public ushort ReadUInt16()
    {
        if (!this.Take(2))
        {
            return 0;
        }

        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(this.buffer.Slice(this.position, 2));
        this.position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        if (!this.Take(4))
        {
            return 0;
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(this.buffer.Slice(this.position, 4));
        this.position += 4;
        return value;
    }

    public int ReadInt32()
    {
        return unchecked((int)this.ReadUInt32());
    }

    public ulong ReadUInt64()
    {
        if (!this.Take(8))
        {
            return 0;
        }

        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(this.buffer.Slice(this.position, 8));
        this.position += 8;
        return value;
    }

    public long ReadInt64()
    {
        return unchecked((long)this.ReadUInt64());
    }

    public ReadOnlySpan<byte> ReadBytes(int byteCount)
    {
        if (!this.Take(byteCount))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        ReadOnlySpan<byte> value = this.buffer.Slice(this.position, byteCount);
        this.position += byteCount;
        return value;
    }

    // A u64 array viewed in place rather than copied. Safe to reinterpret
    // because every perf.data record starts 8-byte aligned within the file
    // buffer and every field preceding a callchain is a u64 or a pair of u32s,
    // so the entries are genuinely 8-byte aligned - and both x86-64 and
    // AArch64, the only targets that produce these files, permit the loads
    // regardless.
    public ReadOnlySpan<ulong> ReadUInt64Array(int elementCount)
    {
        if (elementCount < 0 || !this.Take(elementCount * 8))
        {
            return ReadOnlySpan<ulong>.Empty;
        }

        ReadOnlySpan<ulong> value = MemoryMarshal.Cast<byte, ulong>(this.buffer.Slice(this.position, elementCount * 8));
        this.position += elementCount * 8;
        return value;
    }

    // perf writes strings NUL-terminated and then pads to an 8-byte boundary,
    // so the declared length of the field is not the length of the string.
    // Reading the padded length as the name is how a module path ends up with
    // trailing NULs embedded in it, which then fail every later comparison
    // against a path from any other source.
    public string ReadFixedString(int fieldByteCount)
    {
        ReadOnlySpan<byte> raw = this.ReadBytes(fieldByteCount);
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        int terminatorIndex = raw.IndexOf((byte)0);
        if (terminatorIndex >= 0)
        {
            raw = raw.Slice(0, terminatorIndex);
        }

        return System.Text.Encoding.UTF8.GetString(raw);
    }

    // The rest of this slice, as a NUL-terminated string. Used for the
    // trailing `char filename[]` / `char comm[]` members that run to the end
    // of their record.
    public string ReadRemainingString()
    {
        return this.ReadFixedString(this.Remaining);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.PerfData)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
