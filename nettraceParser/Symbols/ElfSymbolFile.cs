////////////////////////////////////////////////////////////////////////////////
// Module: ElfSymbolFile.cs
//
// Notes:
// Reads the function symbols out of an ELF64 file - either a stripped module's
// separate `.dbg`/`.debug` companion or the module itself - so a
// `dotnet-trace collect-linux` capture's addresses inside a module that
// shipped no symbols can still be named. See Symbols/SymbolStore.cs for where
// those files come from.
//
// Hand-rolled rather than taken from a package, for the same reason the
// nettrace reader is: this needs three structures out of a documented,
// frozen format (the section header table, one symbol table, and its string
// table), and every ELF library available would pull in far more than that.
// ClrMD is not an option here either - these are Linux native modules, not
// a .NET runtime's data structures.
//
// MEMORY MATTERS HERE. A real libcoreclr.so.dbg from Microsoft's symbol
// server is 138MB, and there is no reason to hold any of it: this seeks to
// the section header table, finds the one symbol table and its string table,
// and reads only those two sections. What is retained afterwards is the
// decoded FUNC symbols alone (17,423 of them for that file).
//
// Only ELF64 little-endian is handled. `collect-linux` is Linux-x64/arm64
// only, both of which are ELF64 LE, and a wrong-class file is reported as an
// error on the stack rather than guessed at.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Symbols {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ElfSymbolFile
{
    // Elf64 header field offsets.
    private const int ElfClassOffset = 4;
    private const int ElfDataOffset = 5;
    private const int SectionHeaderOffsetOffset = 0x28;
    private const int SectionHeaderEntrySizeOffset = 0x3A;
    private const int SectionHeaderCountOffset = 0x3C;
    private const int ElfHeaderBytes = 64;

    private const int SectionHeaderBytes = 64;
    private const int SymbolEntryBytes = 24;

    private const uint SectionTypeSymbolTable = 2;
    private const uint SectionTypeDynamicSymbols = 11;

    private const int SymbolTypeFunction = 2;

    public struct FunctionSymbol
    {
        public long StartAddress;
        public long EndAddress;
        public string Name;
    }

    // Sorted by StartAddress. These are the module's OWN ELF virtual
    // addresses - the same space UniversalSymbolTable computes when it turns a
    // runtime address into a module offset.
    private readonly FunctionSymbol[] symbols;

    public int SymbolCount => this.symbols.Length;

    private ElfSymbolFile(FunctionSymbol[] symbols)
    {
        this.symbols = symbols;
    }

    // Returns null and sets `error` rather than throwing: a symbol file is
    // fetched from a network service and may be truncated, an HTML error page,
    // or a format this does not read, and none of those should be able to take
    // down a capture that was parsing fine without symbols at all.
    public static ElfSymbolFile TryLoad(string filePath, out string error)
    {
        error = null;

        try
        {
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return TryLoadCore(stream, out error);
            }
        }
        catch (IOException ioError)
        {
            error = ioError.Message;
            return null;
        }
        catch (UnauthorizedAccessException accessError)
        {
            error = accessError.Message;
            return null;
        }
    }

    private static ElfSymbolFile TryLoadCore(Stream stream, out string error)
    {
        error = null;

        byte[] header = new byte[ElfHeaderBytes];

        if (!ReadExactlyAt(stream, 0, header, header.Length))
        {
            error = "file is too short to be an ELF image";
            return null;
        }

        if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
        {
            error = "not an ELF file (bad magic)";
            return null;
        }

        if (header[ElfClassOffset] != 2)
        {
            error = "not ELF64";
            return null;
        }

        if (header[ElfDataOffset] != 1)
        {
            error = "not little-endian ELF";
            return null;
        }

        long sectionHeaderOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(SectionHeaderOffsetOffset));
        int sectionHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(SectionHeaderEntrySizeOffset));
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(SectionHeaderCountOffset));

        if (sectionHeaderOffset <= 0 || sectionCount == 0 || sectionHeaderEntrySize < SectionHeaderBytes)
        {
            error = "no section header table (a fully stripped file carries no symbols)";
            return null;
        }

        byte[] sectionHeaders = new byte[(long)sectionCount * sectionHeaderEntrySize <= int.MaxValue
            ? sectionCount * sectionHeaderEntrySize
            : 0];

        if (sectionHeaders.Length == 0 || !ReadExactlyAt(stream, sectionHeaderOffset, sectionHeaders, sectionHeaders.Length))
        {
            error = "section header table is truncated";
            return null;
        }

        // Prefer .symtab (SHT_SYMTAB) over .dynsym: a separate debug file's
        // full symbol table names every static function, while .dynsym only
        // covers exported ones. On a real libcoreclr.so.dbg that is the
        // difference between 17,423 names and a few hundred.
        int symbolSectionIndex = FindSection(sectionHeaders, sectionCount, sectionHeaderEntrySize, SectionTypeSymbolTable);

        if (symbolSectionIndex < 0)
        {
            symbolSectionIndex = FindSection(sectionHeaders, sectionCount, sectionHeaderEntrySize, SectionTypeDynamicSymbols);
        }

        if (symbolSectionIndex < 0)
        {
            error = "no symbol table";
            return null;
        }

        int symbolSectionStart = symbolSectionIndex * sectionHeaderEntrySize;
        long symbolTableOffset = BinaryPrimitives.ReadInt64LittleEndian(sectionHeaders.AsSpan(symbolSectionStart + 0x18));
        long symbolTableSize = BinaryPrimitives.ReadInt64LittleEndian(sectionHeaders.AsSpan(symbolSectionStart + 0x20));
        int stringSectionIndex = BinaryPrimitives.ReadInt32LittleEndian(sectionHeaders.AsSpan(symbolSectionStart + 0x28));

        if (stringSectionIndex < 0 || stringSectionIndex >= sectionCount)
        {
            error = "symbol table names a string table that does not exist";
            return null;
        }

        int stringSectionStart = stringSectionIndex * sectionHeaderEntrySize;
        long stringTableOffset = BinaryPrimitives.ReadInt64LittleEndian(sectionHeaders.AsSpan(stringSectionStart + 0x18));
        long stringTableSize = BinaryPrimitives.ReadInt64LittleEndian(sectionHeaders.AsSpan(stringSectionStart + 0x20));

        if (symbolTableSize <= 0 || symbolTableSize > int.MaxValue || stringTableSize <= 0 || stringTableSize > int.MaxValue)
        {
            error = "symbol or string table has an unusable size";
            return null;
        }

        byte[] symbolTable = new byte[(int)symbolTableSize];

        if (!ReadExactlyAt(stream, symbolTableOffset, symbolTable, symbolTable.Length))
        {
            error = "symbol table is truncated";
            return null;
        }

        byte[] stringTable = new byte[(int)stringTableSize];

        if (!ReadExactlyAt(stream, stringTableOffset, stringTable, stringTable.Length))
        {
            error = "string table is truncated";
            return null;
        }

        int symbolCount = (int)(symbolTableSize / SymbolEntryBytes);
        List<FunctionSymbol> functions = new List<FunctionSymbol>(symbolCount / 2);

        for (int symbolIndex = 0; symbolIndex < symbolCount; ++symbolIndex)
        {
            int entryStart = symbolIndex * SymbolEntryBytes;

            byte info = symbolTable[entryStart + 4];

            if ((info & 0xF) != SymbolTypeFunction)
            {
                continue;
            }

            long value = BinaryPrimitives.ReadInt64LittleEndian(symbolTable.AsSpan(entryStart + 8));
            long size = BinaryPrimitives.ReadInt64LittleEndian(symbolTable.AsSpan(entryStart + 16));

            // A zero address is an undefined/imported symbol; a zero size
            // covers no address, so neither can ever match a lookup.
            if (value == 0 || size <= 0)
            {
                continue;
            }

            uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(symbolTable.AsSpan(entryStart));

            string name = ReadNullTerminated(stringTable, nameOffset);

            if (name == null || name.Length == 0)
            {
                continue;
            }

            FunctionSymbol function = new FunctionSymbol();
            function.StartAddress = value;
            function.EndAddress = value + size;
            function.Name = name;

            functions.Add(function);
        }

        if (functions.Count == 0)
        {
            error = "symbol table contains no function symbols";
            return null;
        }

        FunctionSymbol[] sorted = functions.ToArray();
        Array.Sort(sorted, CompareByStartAddress);

        return new ElfSymbolFile(sorted);
    }

    private static int FindSection(byte[] sectionHeaders, int sectionCount, int entrySize, uint sectionType)
    {
        for (int sectionIndex = 0; sectionIndex < sectionCount; ++sectionIndex)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(sectionHeaders.AsSpan((sectionIndex * entrySize) + 4));

            if (type == sectionType)
            {
                return sectionIndex;
            }
        }

        return -1;
    }

    public bool TryResolve(long elfVirtualAddress, out string name, out long offsetIntoFunction)
    {
        name = null;
        offsetIntoFunction = 0;

        int low = 0;
        int high = this.symbols.Length - 1;
        int candidate = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);

            if (this.symbols[middle].StartAddress <= elfVirtualAddress)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0 || elfVirtualAddress >= this.symbols[candidate].EndAddress)
        {
            return false;
        }

        name = this.symbols[candidate].Name;
        offsetIntoFunction = elfVirtualAddress - this.symbols[candidate].StartAddress;
        return true;
    }

    private static int CompareByStartAddress(FunctionSymbol left, FunctionSymbol right)
    {
        return left.StartAddress.CompareTo(right.StartAddress);
    }

    private static string ReadNullTerminated(byte[] buffer, uint offset)
    {
        if (offset >= buffer.Length)
        {
            return null;
        }

        int end = (int)offset;

        while (end < buffer.Length && buffer[end] != 0)
        {
            ++end;
        }

        return Encoding.UTF8.GetString(buffer, (int)offset, end - (int)offset);
    }

    private static bool ReadExactlyAt(Stream stream, long offset, byte[] buffer, int count)
    {
        if (offset < 0 || count < 0 || count > buffer.Length)
        {
            return false;
        }

        stream.Seek(offset, SeekOrigin.Begin);

        int totalRead = 0;

        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);

            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
