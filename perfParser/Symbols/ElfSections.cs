////////////////////////////////////////////////////////////////////////////////
// Module: ElfSections.cs
//
// Notes:
// Reads an ELF section header table and hands back named sections. Separate
// from ElfLoadSegments (which reads PROGRAM headers, the runtime view) because
// this is the LINKING view: `.eh_frame`, `.eh_frame_hdr` and later
// `.debug_info` are found by section name, and a section header table is a
// different table in a different place with a different entry layout.
//
// Both views are needed and neither substitutes for the other: the program
// headers say where a mapping's bytes live at run time, the section headers say
// which of those bytes are the unwind tables.
//
// A section's `Address` is its ELF virtual address, which is the address space
// every address in .eh_frame is expressed in - so a runtime instruction pointer
// must be translated into it first (see ElfLoadSegments for that formula).
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Symbols {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ElfSection
{
    public string Name;
    public long Address;
    public long FileOffset;
    public long Size;

    // The section's bytes, read on demand. A stripped release binary's
    // .eh_frame is routinely megabytes, and a capture touches only the modules
    // its samples actually landed in.
    public byte[] Data;
}

public sealed class ElfSections
{
    private const int ElfIdentSize = 16;
    private const uint SectionTypeNoBits = 8;

    private readonly Dictionary<string, ElfSection> sectionsByName = new Dictionary<string, ElfSection>(StringComparer.Ordinal);
    private readonly string filePath;

    private ElfSections(string filePath)
    {
        this.filePath = filePath;
    }

    public int Count => this.sectionsByName.Count;

    public static ElfSections TryLoad(string filePath, out string error)
    {
        error = null;

        try
        {
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] header = new byte[64];
                if (stream.Read(header, 0, header.Length) != header.Length)
                {
                    error = "file is shorter than an ELF header";
                    return null;
                }

                if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
                {
                    error = "not an ELF file";
                    return null;
                }

                if (header[4] != 2 || header[5] != 1)
                {
                    error = "only 64-bit little-endian ELF is supported";
                    return null;
                }

                long sectionHeaderOffset = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(header, ElfIdentSize + 24, 8));
                int sectionHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(header, ElfIdentSize + 42, 2));
                int sectionHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(header, ElfIdentSize + 44, 2));
                int sectionNameTableIndex = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(header, ElfIdentSize + 46, 2));

                if (sectionHeaderOffset <= 0 || sectionHeaderEntrySize < 64 || sectionHeaderCount <= 0)
                {
                    error = "ELF has no usable section header table";
                    return null;
                }

                byte[] table = ReadAt(stream, sectionHeaderOffset, (long)sectionHeaderEntrySize * sectionHeaderCount);
                if (table == null)
                {
                    error = "ELF is truncated before the end of its section header table";
                    return null;
                }

                if (sectionNameTableIndex >= sectionHeaderCount)
                {
                    error = "ELF section name table index is out of range";
                    return null;
                }

                int nameTableEntry = sectionNameTableIndex * sectionHeaderEntrySize;
                long nameTableOffset = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(table, nameTableEntry + 24, 8));
                long nameTableSize = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(table, nameTableEntry + 32, 8));

                byte[] nameTable = ReadAt(stream, nameTableOffset, nameTableSize);
                if (nameTable == null)
                {
                    error = "ELF section name table is unreadable";
                    return null;
                }

                ElfSections sections = new ElfSections(filePath);

                for (int sectionIndex = 0; sectionIndex < sectionHeaderCount; ++sectionIndex)
                {
                    int entryOffset = sectionIndex * sectionHeaderEntrySize;

                    uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(table, entryOffset, 4));
                    uint sectionType = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(table, entryOffset + 4, 4));

                    // SHT_NOBITS (.bss) occupies no file bytes; its declared
                    // offset points at whatever follows it.
                    if (sectionType == SectionTypeNoBits)
                    {
                        continue;
                    }

                    ElfSection section = new ElfSection();
                    section.Name = ReadNulTerminated(nameTable, (int)nameOffset);
                    section.Address = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(table, entryOffset + 16, 8));
                    section.FileOffset = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(table, entryOffset + 24, 8));
                    section.Size = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(table, entryOffset + 32, 8));

                    if (section.Name.Length > 0 && !sections.sectionsByName.ContainsKey(section.Name))
                    {
                        sections.sectionsByName[section.Name] = section;
                    }
                }

                return sections;
            }
        }
        catch (IOException ioException)
        {
            error = ioException.Message;
            return null;
        }
        catch (UnauthorizedAccessException accessException)
        {
            error = accessException.Message;
            return null;
        }
    }

    public ElfSection TryGet(string sectionName)
    {
        ElfSection section;
        if (!this.sectionsByName.TryGetValue(sectionName, out section))
        {
            return null;
        }

        if (section.Data == null)
        {
            try
            {
                using (FileStream stream = new FileStream(this.filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    section.Data = ReadAt(stream, section.FileOffset, section.Size);
                }
            }
            catch (IOException)
            {
                return null;
            }
        }

        return section.Data != null ? section : null;
    }

    private static byte[] ReadAt(FileStream stream, long offset, long size)
    {
        if (offset < 0 || size < 0 || size > int.MaxValue)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        stream.Seek(offset, SeekOrigin.Begin);

        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int readNow = stream.Read(buffer, readTotal, buffer.Length - readTotal);
            if (readNow <= 0)
            {
                return null;
            }

            readTotal += readNow;
        }

        return buffer;
    }

    private static string ReadNulTerminated(byte[] buffer, int offset)
    {
        if (offset < 0 || offset >= buffer.Length)
        {
            return string.Empty;
        }

        int end = offset;
        while (end < buffer.Length && buffer[end] != 0)
        {
            ++end;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, offset, end - offset);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
