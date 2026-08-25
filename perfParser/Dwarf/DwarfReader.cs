////////////////////////////////////////////////////////////////////////////////
// Module: DwarfReader.cs
//
// Notes:
// Cursor over DWARF-encoded bytes: LEB128 integers and the `.eh_frame` pointer
// encodings. Same ref-struct-over-span shape as PerfSpanReader, for the same
// reason - the data is already buffered and there is no reason to pay an
// interface call per primitive.
//
// The pointer encodings are the part worth reading carefully. A single byte
// says both how a pointer is STORED (its width and signedness, the low nibble)
// and what it is stored RELATIVE TO (the high nibble). The overwhelmingly
// common case in a real `.eh_frame` is DW_EH_PE_pcrel|DW_EH_PE_sdata4: a
// signed 32-bit displacement from the address of the field itself. That means
// decoding one requires knowing the VIRTUAL ADDRESS the field would have when
// loaded - not its offset in the file, and not its address in the process that
// was sampled. Getting that wrong yields addresses that are plausibly-sized
// and consistently wrong by a fixed bias, which then match the wrong function
// entirely rather than failing to match one.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Dwarf {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class DwarfPointerEncoding
{
    public const byte Omit = 0xFF;

    public const byte FormatMask = 0x0F;
    public const byte FormatAbsolutePointer = 0x00;
    public const byte FormatUleb128 = 0x01;
    public const byte FormatUData2 = 0x02;
    public const byte FormatUData4 = 0x03;
    public const byte FormatUData8 = 0x04;
    public const byte FormatSleb128 = 0x09;
    public const byte FormatSData2 = 0x0A;
    public const byte FormatSData4 = 0x0B;
    public const byte FormatSData8 = 0x0C;

    public const byte ApplicationMask = 0x70;
    public const byte ApplicationAbsolute = 0x00;
    public const byte ApplicationPcRelative = 0x10;
    public const byte ApplicationTextRelative = 0x20;
    public const byte ApplicationDataRelative = 0x30;
    public const byte ApplicationFunctionRelative = 0x40;
    public const byte ApplicationAligned = 0x50;

    public const byte Indirect = 0x80;
}

public ref struct DwarfReader
{
    private readonly ReadOnlySpan<byte> buffer;
    private int position;
    private bool overran;

    // The virtual address the first byte of `buffer` would have once loaded.
    // Needed for DW_EH_PE_pcrel, which is relative to the field's own address.
    private readonly long baseVirtualAddress;

    public DwarfReader(ReadOnlySpan<byte> buffer, long baseVirtualAddress)
    {
        this.buffer = buffer;
        this.position = 0;
        this.overran = false;
        this.baseVirtualAddress = baseVirtualAddress;
    }

    public int Position
    {
        get { return this.position; }
        set { this.position = value; }
    }

    public int Remaining => this.buffer.Length - this.position;

    public bool AtEnd => this.position >= this.buffer.Length;

    public bool Overran => this.overran;

    public long CurrentVirtualAddress => this.baseVirtualAddress + this.position;

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

    public string ReadNulTerminatedString()
    {
        int start = this.position;
        while (this.position < this.buffer.Length && this.buffer[this.position] != 0)
        {
            ++this.position;
        }

        int length = this.position - start;

        // Step past the terminator itself.
        if (this.position < this.buffer.Length)
        {
            ++this.position;
        }

        return System.Text.Encoding.ASCII.GetString(this.buffer.Slice(start, length));
    }

    public ulong ReadUleb128()
    {
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            if (!this.Take(1))
            {
                return result;
            }

            byte current = this.buffer[this.position];
            ++this.position;

            if (shift < 64)
            {
                result |= (ulong)(current & 0x7F) << shift;
            }

            if ((current & 0x80) == 0)
            {
                break;
            }

            shift += 7;

            // A LEB128 longer than ten bytes cannot represent a 64-bit value
            // and means the cursor is not where the caller thinks it is.
            if (shift > 70)
            {
                this.overran = true;
                break;
            }
        }

        return result;
    }

    public long ReadSleb128()
    {
        long result = 0;
        int shift = 0;
        byte current = 0;

        while (true)
        {
            if (!this.Take(1))
            {
                return result;
            }

            current = this.buffer[this.position];
            ++this.position;

            if (shift < 64)
            {
                result |= (long)(current & 0x7F) << shift;
            }

            shift += 7;

            if ((current & 0x80) == 0)
            {
                break;
            }

            if (shift > 70)
            {
                this.overran = true;
                break;
            }
        }

        // Sign-extend from the last byte's sign bit.
        if (shift < 64 && (current & 0x40) != 0)
        {
            result |= -1L << shift;
        }

        return result;
    }

    // Decodes one pointer in the given encoding.
    //
    // `dataRelativeBase` is the address DW_EH_PE_datarel is measured from -
    // conventionally the start of .eh_frame_hdr, which is the only place that
    // encoding appears in practice. `functionRelativeBase` likewise for
    // DW_EH_PE_funcrel. Both are passed in rather than assumed because neither
    // is derivable from the bytes being read.
    public bool TryReadEncodedPointer(byte encoding, long dataRelativeBase, long functionRelativeBase, out long value)
    {
        value = 0;

        if (encoding == DwarfPointerEncoding.Omit)
        {
            return false;
        }

        // The address of the field ITSELF, captured before reading it - pcrel
        // is relative to where the pointer is stored, not to where it ends.
        long fieldVirtualAddress = this.CurrentVirtualAddress;

        long raw;

        switch (encoding & DwarfPointerEncoding.FormatMask)
        {
            case DwarfPointerEncoding.FormatAbsolutePointer:
                raw = unchecked((long)this.ReadUInt64());
                break;

            case DwarfPointerEncoding.FormatUleb128:
                raw = unchecked((long)this.ReadUleb128());
                break;

            case DwarfPointerEncoding.FormatUData2:
                raw = this.ReadUInt16();
                break;

            case DwarfPointerEncoding.FormatUData4:
                raw = this.ReadUInt32();
                break;

            case DwarfPointerEncoding.FormatUData8:
                raw = unchecked((long)this.ReadUInt64());
                break;

            case DwarfPointerEncoding.FormatSleb128:
                raw = this.ReadSleb128();
                break;

            case DwarfPointerEncoding.FormatSData2:
                raw = unchecked((short)this.ReadUInt16());
                break;

            case DwarfPointerEncoding.FormatSData4:
                raw = unchecked((int)this.ReadUInt32());
                break;

            case DwarfPointerEncoding.FormatSData8:
                raw = unchecked((long)this.ReadUInt64());
                break;

            default:
                this.overran = true;
                return false;
        }

        if (this.overran)
        {
            return false;
        }

        switch (encoding & DwarfPointerEncoding.ApplicationMask)
        {
            case DwarfPointerEncoding.ApplicationAbsolute:
                value = raw;
                break;

            case DwarfPointerEncoding.ApplicationPcRelative:
                value = fieldVirtualAddress + raw;
                break;

            case DwarfPointerEncoding.ApplicationDataRelative:
                value = dataRelativeBase + raw;
                break;

            case DwarfPointerEncoding.ApplicationFunctionRelative:
                value = functionRelativeBase + raw;
                break;

            case DwarfPointerEncoding.ApplicationTextRelative:
                // Requires the address of .text, which nothing here tracks.
                // Declining is correct: a wrong base produces a confident
                // wrong address.
                return false;

            case DwarfPointerEncoding.ApplicationAligned:
                return false;

            default:
                return false;
        }

        // DW_EH_PE_indirect means the decoded value is the address of the real
        // pointer, which lives in the loaded image's data. That needs a memory
        // read this reader cannot perform from a file alone.
        if ((encoding & DwarfPointerEncoding.Indirect) != 0)
        {
            return false;
        }

        return true;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Dwarf)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
