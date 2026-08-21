////////////////////////////////////////////////////////////////////////////////
// Module: SymbolStoreTests.cs
//
// Notes:
// Covers the native symbol pipeline that fills in a `dotnet-trace
// collect-linux` capture's unnamed frames: the ELF symbol reader, the
// build-id-keyed cache, the C++ demangler, and the policy that decides which
// modules are worth fetching.
//
// NOTHING HERE TOUCHES THE NETWORK. Every test either points the store at a
// local directory or disables downloads, so the suite stays hermetic and fast;
// what a real symbol server returns is verified by hand against the reference
// capture, not in CI. The ELF fixtures are synthesized byte by byte for the
// same reason the v6 stream fixtures are - a real libcoreclr.so.dbg is 138MB.
//
// The mangled names below are REAL symbols from that capture's libcoreclr.so.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using DotnetInsights.NetTrace.Symbols;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Builds a minimal but genuine ELF64 image: a header, a null section, a
// symbol table and its string table. Section names are omitted because
// ElfSymbolFile locates sections by TYPE, never by name - which is itself
// worth pinning, since a real .dbg's section names live in a section this
// reader deliberately never reads.
internal sealed class ElfImageBuilder
{
    private const int ElfHeaderBytes = 64;
    private const int SectionHeaderBytes = 64;
    private const int SymbolEntryBytes = 24;

    public const uint SectionTypeSymbolTable = 2;
    public const uint SectionTypeStringTable = 3;
    public const uint SectionTypeDynamicSymbols = 11;

    private readonly List<(string Name, long Address, long Size, int Type)> symbols = new List<(string, long, long, int)>();

    public byte ElfClass { get; set; } = 2;

    public byte ElfData { get; set; } = 1;

    public uint SymbolSectionType { get; set; } = SectionTypeSymbolTable;

    public bool OmitSymbolTable { get; set; }

    // Type 2 is STT_FUNC; anything else must be ignored by the reader.
    public ElfImageBuilder AddFunction(string name, long address, long size)
    {
        this.symbols.Add((name, address, size, 2));
        return this;
    }

    public ElfImageBuilder AddObject(string name, long address, long size)
    {
        this.symbols.Add((name, address, size, 1));
        return this;
    }

    public string WriteToTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"elftest-{Guid.NewGuid():N}.dbg");
        File.WriteAllBytes(path, this.ToArray());
        return path;
    }

    public byte[] ToArray()
    {
        // String table: index 0 is the empty string, per ELF.
        List<byte> stringTable = new List<byte>();
        stringTable.Add(0);

        List<(uint NameOffset, long Address, long Size, int Type)> entries = new List<(uint, long, long, int)>();

        // Index 0 of a symbol table is always the null symbol.
        entries.Add((0, 0, 0, 0));

        foreach ((string Name, long Address, long Size, int Type) symbol in this.symbols)
        {
            uint nameOffset = (uint)stringTable.Count;
            stringTable.AddRange(Encoding.UTF8.GetBytes(symbol.Name));
            stringTable.Add(0);
            entries.Add((nameOffset, symbol.Address, symbol.Size, symbol.Type));
        }

        int sectionCount = this.OmitSymbolTable ? 1 : 3;
        int sectionHeaderOffset = ElfHeaderBytes;
        int symbolTableOffset = sectionHeaderOffset + (sectionCount * SectionHeaderBytes);
        int symbolTableSize = entries.Count * SymbolEntryBytes;
        int stringTableOffset = symbolTableOffset + symbolTableSize;

        byte[] image = new byte[stringTableOffset + stringTable.Count];

        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = this.ElfClass;
        image[5] = this.ElfData;
        image[6] = 1;

        WriteInt64(image, 0x28, sectionHeaderOffset);
        WriteUInt16(image, 0x3A, SectionHeaderBytes);
        WriteUInt16(image, 0x3C, (ushort)sectionCount);
        WriteUInt16(image, 0x3E, 0);

        if (this.OmitSymbolTable)
        {
            return image;
        }

        // Section 1: the symbol table, linking to section 2 for its names.
        int symbolSection = sectionHeaderOffset + SectionHeaderBytes;
        WriteUInt32(image, symbolSection + 4, this.SymbolSectionType);
        WriteInt64(image, symbolSection + 0x18, symbolTableOffset);
        WriteInt64(image, symbolSection + 0x20, symbolTableSize);
        WriteUInt32(image, symbolSection + 0x28, 2);
        WriteInt64(image, symbolSection + 0x38, SymbolEntryBytes);

        // Section 2: the string table.
        int stringSection = sectionHeaderOffset + (2 * SectionHeaderBytes);
        WriteUInt32(image, stringSection + 4, SectionTypeStringTable);
        WriteInt64(image, stringSection + 0x18, stringTableOffset);
        WriteInt64(image, stringSection + 0x20, stringTable.Count);

        for (int entryIndex = 0; entryIndex < entries.Count; ++entryIndex)
        {
            int entryStart = symbolTableOffset + (entryIndex * SymbolEntryBytes);
            WriteUInt32(image, entryStart, entries[entryIndex].NameOffset);
            image[entryStart + 4] = (byte)entries[entryIndex].Type;
            WriteInt64(image, entryStart + 8, entries[entryIndex].Address);
            WriteInt64(image, entryStart + 16, entries[entryIndex].Size);
        }

        stringTable.CopyTo(image, stringTableOffset);

        return image;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            buffer[offset + (shift / 8)] = (byte)((value >> shift) & 0xFF);
        }
    }

    private static void WriteInt64(byte[] buffer, int offset, long value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            buffer[offset + (shift / 8)] = (byte)((value >> shift) & 0xFF);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ElfSymbolFileTests
{
    private static ElfSymbolFile Load(ElfImageBuilder builder, out string error)
    {
        string path = builder.WriteToTempFile();

        try
        {
            return ElfSymbolFile.TryLoad(path, out error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoad_ReadsFunctionSymbolsAndResolvesAnAddressInsideOne()
    {
        ElfSymbolFile file = Load(new ElfImageBuilder()
            .AddFunction("RhpNewFast", 0x5FC610, 0x40)
            .AddFunction("JIT_ByRefWriteBarrier", 0x5FB580, 0x100), out string error);

        Assert.Null(error);
        Assert.NotNull(file);
        Assert.Equal(2, file.SymbolCount);

        Assert.True(file.TryResolve(0x5FC627, out string name, out long offset));
        Assert.Equal("RhpNewFast", name);
        Assert.Equal(0x17, offset);
    }

    [Fact]
    public void TryResolve_ExcludesTheEndOfAFunction()
    {
        ElfSymbolFile file = Load(new ElfImageBuilder().AddFunction("f", 0x1000, 0x10), out _);

        Assert.True(file.TryResolve(0x100F, out _, out _));
        Assert.False(file.TryResolve(0x1010, out _, out _));
    }

    [Fact]
    public void TryResolve_ReportsFailureBelowTheFirstAndAboveTheLastSymbol()
    {
        ElfSymbolFile file = Load(new ElfImageBuilder()
            .AddFunction("a", 0x2000, 0x10)
            .AddFunction("b", 0x3000, 0x10), out _);

        Assert.False(file.TryResolve(0x1000, out _, out _));
        Assert.False(file.TryResolve(0x9000, out _, out _));
        Assert.False(file.TryResolve(0x2500, out _, out _));
    }

    // Symbols are emitted in whatever order the linker chose, so the reader
    // must sort them - a binary search over an unsorted table silently misses.
    [Fact]
    public void TryLoad_SortsSymbolsRegardlessOfTableOrder()
    {
        ElfSymbolFile file = Load(new ElfImageBuilder()
            .AddFunction("high", 0x9000, 0x10)
            .AddFunction("low", 0x1000, 0x10)
            .AddFunction("middle", 0x5000, 0x10), out _);

        Assert.True(file.TryResolve(0x1005, out string low, out _));
        Assert.Equal("low", low);

        Assert.True(file.TryResolve(0x5005, out string middle, out _));
        Assert.Equal("middle", middle);

        Assert.True(file.TryResolve(0x9005, out string high, out _));
        Assert.Equal("high", high);
    }

    // Only STT_FUNC entries name code. A data object at the same address must
    // not shadow a function.
    [Fact]
    public void TryLoad_IgnoresNonFunctionSymbols()
    {
        ElfSymbolFile file = Load(new ElfImageBuilder()
            .AddObject("g_someGlobal", 0x1000, 0x100)
            .AddFunction("realFunction", 0x2000, 0x10), out _);

        Assert.Equal(1, file.SymbolCount);
        Assert.False(file.TryResolve(0x1050, out _, out _));
    }

    // A zero size covers no address and a zero address is an undefined
    // import; neither can ever be the right answer.
    [Fact]
    public void TryLoad_SkipsZeroSizedAndUndefinedSymbols()
    {
        ElfSymbolFile file = Load(new ElfImageBuilder()
            .AddFunction("sizeless", 0x1000, 0)
            .AddFunction("imported", 0, 0x10)
            .AddFunction("real", 0x2000, 0x10), out _);

        Assert.Equal(1, file.SymbolCount);
    }

    [Fact]
    public void TryLoad_FallsBackToDynamicSymbolsWhenThereIsNoFullSymbolTable()
    {
        ElfImageBuilder builder = new ElfImageBuilder();
        builder.SymbolSectionType = ElfImageBuilder.SectionTypeDynamicSymbols;
        builder.AddFunction("exported", 0x1000, 0x10);

        ElfSymbolFile file = Load(builder, out string error);

        Assert.Null(error);
        Assert.Equal(1, file.SymbolCount);
    }

    // Every one of these must be an error on the stack, never an exception: a
    // symbol file arrives over the network and can be anything at all, and a
    // capture must still open without it.
    [Fact]
    public void TryLoad_ReportsAnErrorForAFileThatIsNotElf()
    {
        string path = Path.Combine(Path.GetTempPath(), $"notelf-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "<html>404 Not Found</html>");

        try
        {
            ElfSymbolFile file = ElfSymbolFile.TryLoad(path, out string error);

            Assert.Null(file);
            Assert.Contains("ELF", error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoad_ReportsAnErrorForAThirtyTwoBitImage()
    {
        ElfImageBuilder builder = new ElfImageBuilder();
        builder.ElfClass = 1;
        builder.AddFunction("f", 0x1000, 0x10);

        ElfSymbolFile file = Load(builder, out string error);

        Assert.Null(file);
        Assert.Equal("not ELF64", error);
    }

    [Fact]
    public void TryLoad_ReportsAnErrorForABigEndianImage()
    {
        ElfImageBuilder builder = new ElfImageBuilder();
        builder.ElfData = 2;
        builder.AddFunction("f", 0x1000, 0x10);

        ElfSymbolFile file = Load(builder, out string error);

        Assert.Null(file);
        Assert.Contains("little-endian", error);
    }

    [Fact]
    public void TryLoad_ReportsAnErrorForAStrippedImageWithNoSymbolTable()
    {
        ElfImageBuilder builder = new ElfImageBuilder();
        builder.OmitSymbolTable = true;

        ElfSymbolFile file = Load(builder, out string error);

        Assert.Null(file);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryLoad_ReportsAnErrorForAMissingFile()
    {
        ElfSymbolFile file = ElfSymbolFile.TryLoad(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")), out string error);

        Assert.Null(file);
        Assert.NotNull(error);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class NativeSymbolDemanglerTests
{
    // Real symbols from the reference capture's libcoreclr.so.
    [Theory]
    [InlineData("_ZN12ObjectNative25Monitor_TryEnter_FastPathEP6Object", "ObjectNative::Monitor_TryEnter_FastPath")]
    [InlineData("_ZN3SVR7gc_heap17find_first_objectEPhS1_", "SVR::gc_heap::find_first_object")]
    [InlineData("_ZN3SVR7gc_heap16background_sweepEv", "SVR::gc_heap::background_sweep")]
    [InlineData("_ZN3SVR6t_join4joinEPNS_7gc_heapEi", "SVR::t_join::join")]
    [InlineData("_ZN7CorUnix20CSynchWaitController21RegisterWaitingThreadENS_8WaitTypeEjbb", "CorUnix::CSynchWaitController::RegisterWaitingThread")]
    public void Demangle_DecodesNestedNames(string mangled, string expected)
    {
        Assert.Equal(expected, NativeSymbolDemangler.Demangle(mangled));
    }

    [Theory]
    [InlineData("_Z26InlinedMemmoveGCRefsHelperPvPKvm", "InlinedMemmoveGCRefsHelper")]
    [InlineData("_Z17ErectWriteBarrierPP6ObjectS0_", "ErectWriteBarrier")]
    public void Demangle_DecodesUnnestedNames(string mangled, string expected)
    {
        Assert.Equal(expected, NativeSymbolDemangler.Demangle(mangled));
    }

    // `_ZL` marks internal linkage and says nothing about the name itself.
    [Fact]
    public void Demangle_SkipsTheInternalLinkagePrefix()
    {
        Assert.Equal("write_event_2", NativeSymbolDemangler.Demangle("_ZL13write_event_2P6ThreadP15_EventPipeEvent"));
    }

    // `_ZNK` is a const member function - the qualifier sits between the N and
    // the first component.
    [Fact]
    public void Demangle_SkipsCvQualifiers()
    {
        Assert.Equal("MethodTable::GetClass", NativeSymbolDemangler.Demangle("_ZNK11MethodTable8GetClassEv"));
    }

    // Anything this subset cannot decode has to come back UNCHANGED. A
    // half-rewritten mangled name is neither the real symbol nor a readable
    // one, and unlike a raw name it cannot be pasted into a demangler.
    [Theory]
    [InlineData("_ZZN3SVR7gc_heap4initEvE6s_once")]          // entity local to a function
    [InlineData("_ZN3std6vectorIiSaIiEE9push_backERKi")]     // template arguments
    [InlineData("_ZN3Foo")]                                   // truncated
    [InlineData("_ZN99999999999999999999Foo")]                // absurd component length
    public void Demangle_ReturnsUnsupportedShapesUnchanged(string mangled)
    {
        Assert.Equal(mangled, NativeSymbolDemangler.Demangle(mangled));
    }

    [Theory]
    [InlineData("finish_task_switch.isra.0")]
    [InlineData("__pthread_mutex_lock")]
    [InlineData("do_syscall_64")]
    [InlineData("CompressionNative_Deflate")]
    public void Demangle_LeavesPlainCSymbolsAlone(string name)
    {
        Assert.False(NativeSymbolDemangler.IsMangled(name));
        Assert.Equal(name, NativeSymbolDemangler.Demangle(name));
    }

    [Fact]
    public void Demangle_ToleratesNullAndShortInput()
    {
        Assert.Null(NativeSymbolDemangler.Demangle(null));
        Assert.Equal("_Z", NativeSymbolDemangler.Demangle("_Z"));
        Assert.Equal("", NativeSymbolDemangler.Demangle(""));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class SymbolStoreTests
{
    private const string BuildId = "e7f47fde94af76a64002a713a81f515af04be076";

    private static string NewCacheDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"symcache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void TryGetSymbols_ReadsAFileAlreadyInTheCacheWithoutAnyNetworkAccess()
    {
        string cache = NewCacheDirectory();

        try
        {
            // allowDownload: false proves the cache alone answered.
            SymbolStore store = new SymbolStore(cache, SymbolServer.Default, false);

            string cachedPath = store.PathForBuildId(BuildId);
            Directory.CreateDirectory(Path.GetDirectoryName(cachedPath));
            File.WriteAllBytes(cachedPath, new ElfImageBuilder().AddFunction("RhpNewFast", 0x5FC610, 0x40).ToArray());

            ElfSymbolFile symbols = store.TryGetSymbols(BuildId, "libcoreclr.so", out string status);

            Assert.NotNull(symbols);
            Assert.Equal(1, store.CacheHitCount);
            Assert.Equal(0, store.DownloadedCount);
            Assert.True(symbols.TryResolve(0x5FC627, out string name, out _));
            Assert.Equal("RhpNewFast", name);
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void TryGetSymbols_DoesNotReachTheNetworkWhenDownloadsAreDisabled()
    {
        string cache = NewCacheDirectory();

        try
        {
            SymbolStore store = new SymbolStore(cache, SymbolServer.Default, false);

            ElfSymbolFile symbols = store.TryGetSymbols(BuildId, "libcoreclr.so", out string status);

            Assert.Null(symbols);
            Assert.Contains("disabled", status);
            Assert.Equal(0, store.DownloadedCount);
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void TryGetSymbols_RefusesAModuleWithNoBuildId()
    {
        string cache = NewCacheDirectory();

        try
        {
            SymbolStore store = new SymbolStore(cache, SymbolServer.Default, true);

            Assert.Null(store.TryGetSymbols(null, "x.so", out string nullStatus));
            Assert.Contains("build id", nullStatus);

            Assert.Null(store.TryGetSymbols("", "x.so", out string emptyStatus));
            Assert.Contains("build id", emptyStatus);
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    // A file that is present but unreadable as ELF must be recorded as a miss,
    // or every open re-parses it forever.
    [Fact]
    public void TryGetSymbols_RecordsAMissForACachedFileThatIsNotUsableElf()
    {
        string cache = NewCacheDirectory();

        try
        {
            SymbolStore store = new SymbolStore(cache, SymbolServer.Default, false);

            string cachedPath = store.PathForBuildId(BuildId);
            Directory.CreateDirectory(Path.GetDirectoryName(cachedPath));
            File.WriteAllText(cachedPath, "<html>404</html>");

            Assert.Null(store.TryGetSymbols(BuildId, "libcoreclr.so", out string status));
            Assert.Contains("unusable", status);

            // A second store over the same directory must take the cached miss
            // rather than re-reading the bad file.
            SymbolStore reopened = new SymbolStore(cache, SymbolServer.Default, false);
            Assert.Null(reopened.TryGetSymbols(BuildId, "libcoreclr.so", out string secondStatus));
            Assert.Equal(1, reopened.MissCount);
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void TryGetSymbols_AnswersRepeatedRequestsForOneModuleFromMemory()
    {
        string cache = NewCacheDirectory();

        try
        {
            SymbolStore store = new SymbolStore(cache, SymbolServer.Default, false);

            string cachedPath = store.PathForBuildId(BuildId);
            Directory.CreateDirectory(Path.GetDirectoryName(cachedPath));
            File.WriteAllBytes(cachedPath, new ElfImageBuilder().AddFunction("f", 0x1000, 0x10).ToArray());

            store.TryGetSymbols(BuildId, "libcoreclr.so", out _);
            store.TryGetSymbols(BuildId, "libcoreclr.so", out string secondStatus);

            Assert.Equal(1, store.CacheHitCount);
            Assert.Equal("already loaded", secondStatus);
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    // The cache is keyed by build id, so two different builds of the same
    // filename can never collide - the failure mode that makes symbol caches
    // produce confidently wrong answers.
    [Fact]
    public void PathForBuildId_KeysTheCacheByBuildIdRatherThanFileName()
    {
        string cache = NewCacheDirectory();

        try
        {
            SymbolStore store = new SymbolStore(cache, SymbolServer.Default, false);

            string first = store.PathForBuildId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            string second = store.PathForBuildId("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

            Assert.NotEqual(first, second);
            Assert.Contains("aaaaaaaa", first);
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class SymbolServerTests
{
    // Microsoft's server and a debuginfod server speak different URL shapes and
    // nothing in a URL itself says which, so the caller declares it.
    [Fact]
    public void BuildUrl_UsesTheMicrosoftElfKeyShape()
    {
        SymbolServer server = new SymbolServer(SymbolServer.MicrosoftSymbolServerUrl, false);

        Assert.Equal(
            "https://msdl.microsoft.com/download/symbols/_.debug/elf-buildid-sym-abcdef/_.debug",
            server.BuildUrl("abcdef"));
    }

    [Fact]
    public void BuildUrl_UsesTheDebuginfodShape()
    {
        SymbolServer server = new SymbolServer(SymbolServer.UbuntuDebuginfodUrl, true);

        Assert.Equal("https://debuginfod.ubuntu.com/buildid/abcdef/debuginfo", server.BuildUrl("abcdef"));
    }

    [Fact]
    public void Parse_RecognizesTheDebuginfodPrefix()
    {
        SymbolServer server = SymbolServer.Parse("debuginfod:https://debuginfod.ubuntu.com");

        Assert.True(server.IsDebuginfod);
        Assert.Equal("https://debuginfod.ubuntu.com", server.BaseUrl);
    }

    [Fact]
    public void Parse_DefaultsToTheMicrosoftShape()
    {
        SymbolServer server = SymbolServer.Parse("https://symbols.example.com/");

        Assert.False(server.IsDebuginfod);
        Assert.Equal("https://symbols.example.com", server.BaseUrl);
    }

    [Fact]
    public void Default_ConsultsMicrosoftsServer()
    {
        Assert.Single(SymbolServer.Default);
        Assert.Equal(SymbolServer.MicrosoftSymbolServerUrl, SymbolServer.Default[0].BaseUrl);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
