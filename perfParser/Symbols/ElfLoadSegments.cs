////////////////////////////////////////////////////////////////////////////////
// Module: ElfLoadSegments.cs
//
// Notes:
// Reads ONLY the PT_LOAD program headers out of an ELF file. That is a
// deliberately tiny slice of the format, and it exists because it is the one
// piece of information needed to turn a sampled instruction pointer into an
// address a symbol table can be asked about - and the one piece a perf.data
// capture does not contain.
//
// The translation, whose derivation and verification are recorded in
// nettraceParser/Symbols/NativeSymbolResolution.cs, is:
//
//     elfVirtualAddress = (ip - mapping.Start) + mapping.FileOffset
//                         - p_offset + p_vaddr
//
// The naive form, `ip - mapping.Start`, is wrong and does not fail visibly: a
// mapping begins at some offset INTO the module file (libcoreclr's text maps
// at file offset 0x1C8000) and the file has its own p_vaddr/p_offset bias, so
// the naive answer lands inside a DIFFERENT function and names it
// confidently. Scored against ground truth on the .NET side it was 0 of 6;
// this form was 6 of 6.
//
// On the nettrace path p_vaddr/p_offset arrive in the capture, as
// ProcessMappingMetadata. A perf.data MMAP2 record carries the mapping's own
// start, length and file offset but nothing about the file's internal layout,
// so the bias has to come from the module file itself - which is available,
// because resolving a symbol requires having that file anyway.
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

public readonly struct ElfLoadSegment
{
    public readonly long FileOffset;
    public readonly long VirtualAddress;
    public readonly long FileSize;
    public readonly bool IsExecutable;

    public ElfLoadSegment(long fileOffset, long virtualAddress, long fileSize, bool isExecutable)
    {
        this.FileOffset = fileOffset;
        this.VirtualAddress = virtualAddress;
        this.FileSize = fileSize;
        this.IsExecutable = isExecutable;
    }
}

public sealed class ElfLoadSegments
{
    private const int ElfIdentSize = 16;
    private const uint ProgramHeaderTypeLoad = 1;
    private const uint ProgramHeaderFlagExecute = 1;

    private readonly List<ElfLoadSegment> segments = new List<ElfLoadSegment>();

    public int Count => this.segments.Count;

    // Only the ELF header and the program header table are read - a few hundred
    // bytes - so this is cheap enough to do for every module in a capture even
    // when its symbols are never needed.
    public static ElfLoadSegments TryLoad(string filePath, out string error)
    {
        error = null;

        FileStream stream = null;

        try
        {
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

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

            bool is64Bit = header[4] == 2;
            bool isLittleEndian = header[5] == 1;

            if (!is64Bit)
            {
                // 32-bit ELF is a different header layout throughout. Declining
                // is better than decoding one as the other, which produces
                // segment addresses that look ordinary.
                error = "32-bit ELF is not supported";
                return null;
            }

            if (!isLittleEndian)
            {
                error = "big-endian ELF is not supported";
                return null;
            }

            long programHeaderOffset = ReadInt64(header, ElfIdentSize + 16);
            int programHeaderEntrySize = ReadUInt16(header, ElfIdentSize + 38);
            int programHeaderCount = ReadUInt16(header, ElfIdentSize + 40);

            if (programHeaderOffset <= 0 || programHeaderEntrySize < 56 || programHeaderCount <= 0)
            {
                error = "ELF declares no usable program header table";
                return null;
            }

            long tableBytes = (long)programHeaderEntrySize * programHeaderCount;
            if (tableBytes > 16 * 1024 * 1024)
            {
                error = "ELF declares an implausible program header table";
                return null;
            }

            byte[] table = new byte[tableBytes];
            stream.Seek(programHeaderOffset, SeekOrigin.Begin);

            int readTotal = 0;
            while (readTotal < table.Length)
            {
                int readNow = stream.Read(table, readTotal, table.Length - readTotal);
                if (readNow <= 0)
                {
                    error = "ELF is truncated before the end of its program header table";
                    return null;
                }

                readTotal += readNow;
            }

            ElfLoadSegments result = new ElfLoadSegments();

            for (int entryIndex = 0; entryIndex < programHeaderCount; ++entryIndex)
            {
                int entryOffset = entryIndex * programHeaderEntrySize;

                uint segmentType = ReadUInt32(table, entryOffset);
                if (segmentType != ProgramHeaderTypeLoad)
                {
                    continue;
                }

                uint flags = ReadUInt32(table, entryOffset + 4);
                long fileOffset = ReadInt64(table, entryOffset + 8);
                long virtualAddress = ReadInt64(table, entryOffset + 16);
                long fileSize = ReadInt64(table, entryOffset + 32);

                result.segments.Add(new ElfLoadSegment(fileOffset, virtualAddress, fileSize, (flags & ProgramHeaderFlagExecute) != 0));
            }

            if (result.segments.Count == 0)
            {
                error = "ELF has no PT_LOAD segments";
                return null;
            }

            return result;
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
        finally
        {
            if (stream != null)
            {
                stream.Dispose();
            }
        }
    }

    // The (p_vaddr - p_offset) bias for the segment a mapping's file offset
    // falls in. Selected by FILE OFFSET, not by address: the mapping's address
    // is where the loader happened to put it in this process, which says
    // nothing about which segment of the file it is.
    public bool TryGetBiasForFileOffset(long mappingFileOffset, out long bias)
    {
        bias = 0;

        for (int segmentIndex = 0; segmentIndex < this.segments.Count; ++segmentIndex)
        {
            ElfLoadSegment segment = this.segments[segmentIndex];

            if (mappingFileOffset >= segment.FileOffset && mappingFileOffset < segment.FileOffset + segment.FileSize)
            {
                bias = segment.VirtualAddress - segment.FileOffset;
                return true;
            }
        }

        // A mapping whose file offset sits in no PT_LOAD segment. The
        // executable segment is the only one a sampled instruction can be in,
        // so it is the right guess - but it IS a guess, and the caller is told
        // so rather than being handed a confident wrong answer.
        for (int segmentIndex = 0; segmentIndex < this.segments.Count; ++segmentIndex)
        {
            if (this.segments[segmentIndex].IsExecutable)
            {
                bias = this.segments[segmentIndex].VirtualAddress - this.segments[segmentIndex].FileOffset;
                return false;
            }
        }

        return false;
    }

    // The executable PT_LOAD segment's own (p_offset, p_vaddr). This is the
    // pair a consumer needs when it computes the bias itself rather than
    // asking for one - notably UniversalSymbolTable, which takes them as
    // ProcessMappingMetadata fields.
    public bool TryGetExecutableSegment(out long fileOffset, out long virtualAddress)
    {
        fileOffset = 0;
        virtualAddress = 0;

        for (int segmentIndex = 0; segmentIndex < this.segments.Count; ++segmentIndex)
        {
            if (this.segments[segmentIndex].IsExecutable)
            {
                fileOffset = this.segments[segmentIndex].FileOffset;
                virtualAddress = this.segments[segmentIndex].VirtualAddress;
                return true;
            }
        }

        return false;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(buffer, offset, 2));
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(buffer, offset, 4));
    }

    private static long ReadInt64(byte[] buffer, int offset)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(buffer, offset, 8));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
