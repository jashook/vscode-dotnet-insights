////////////////////////////////////////////////////////////////////////////////
// Module: NativeSymbolResolutionTests.cs
//
// Notes:
// Address -> name resolution for a `dotnet-trace collect-linux` capture
// (Universal/UniversalSymbolTable.cs): the symbol/mapping range lookup, the
// unsigned address comparison kernel symbols depend on, and the module-offset
// formula.
//
// The module-offset numbers below are GROUND TRUTH, not constructed examples.
// libSystem.IO.Compression.Native.so is the one module in the reference
// capture that carries BOTH in-capture ProcessSymbol entries and the
// ProcessMappingMetadata describing its ELF layout, so its six known symbol
// addresses can be run through a candidate formula and checked against the
// real binary's symbol table (fetched from Microsoft's symbol server by the
// build_id the capture records). That check is what settled the formula:
//
//   naive   (ip - mapping.Start)                            0 of 6 correct
//   correct (ip - mapping.Start) + FileOffset - p_off + p_va 6 of 6 correct
//
// The naive form does not merely look worse - it lands inside a DIFFERENT
// function, so it produces a confidently wrong answer rather than no answer.
// These tests pin the real addresses so that can never silently come back.
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Universal;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class NativeSymbolResolutionTests
{
    private const string UniversalSystem = "Universal.System";

    // Real values from the reference capture's libSystem.IO.Compression.Native.so.
    private const long CompressionMapStart = 0x7d4e867bb000L;
    private const long CompressionMapEnd = 0x7d4e8683c000L;
    private const long CompressionFileOffset = 0x79000L;
    private const long CompressionProgramVirtualAddress = 0x7ae70L;
    private const long CompressionProgramFileOffset = 0x79e70L;
    private const long CompressionMetadataId = 2L;

    private static EventRecord MakeUniversalEvent(string eventName, Dictionary<string, object> fields, long threadId = 100)
    {
        return new EventRecord(UniversalSystem, eventName, 0, 0, 0, threadId, StackTable.EmptyStackIndex, fields, null, 0, 0);
    }

    private static EventRecord Mapping(long id, long start, long end, string fileName, long fileOffset = 0, long metadataId = 0)
    {
        Dictionary<string, object> fields = new Dictionary<string, object>();
        fields["Id"] = id;
        fields["StartAddress"] = start;
        fields["EndAddress"] = end;
        fields["FileOffset"] = fileOffset;
        fields["FileName"] = fileName;
        fields["MetadataId"] = metadataId;
        return MakeUniversalEvent("ProcessMapping", fields);
    }

    private static EventRecord Symbol(long id, long start, long end, string name)
    {
        Dictionary<string, object> fields = new Dictionary<string, object>();
        fields["Id"] = id;
        fields["MappingId"] = 0L;
        fields["StartAddress"] = start;
        fields["EndAddress"] = end;
        fields["Name"] = name;
        return MakeUniversalEvent("ProcessSymbol", fields);
    }

    private static EventRecord MappingMetadata(long id, string buildId, long programVirtualAddress, long programFileOffset)
    {
        Dictionary<string, object> fields = new Dictionary<string, object>();
        fields["Id"] = id;
        fields["SymbolMetadata"] =
            $"{{\"type\": \"ELF\",\"debug_link\": \"x.dbg\",\"build_id\": \"{buildId}\"," +
            $"\"p_vaddr\": \"0x{programVirtualAddress:x}\",\"p_offset\": \"0x{programFileOffset:x}\"}}";
        fields["VersionMetadata"] = "";
        return MakeUniversalEvent("ProcessMappingMetadata", fields);
    }

    private static UniversalSymbolTable BuildTable(params EventRecord[] events)
    {
        return UniversalSymbolTable.Build(new List<EventRecord>(events), null);
    }

    [Fact]
    public void Resolve_PrefersASymbolOverTheContainingModule()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/lib/libthing.so"),
            Symbol(0, 0x2000, 0x2100, "do_the_thing"));

        Assert.True(table.TryResolve(0x2050, out string name));
        Assert.Equal("do_the_thing", name);
    }

    [Fact]
    public void Resolve_FallsBackToTheModuleWhenNoSymbolCoversTheAddress()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/lib/libthing.so"),
            Symbol(0, 0x2000, 0x2100, "do_the_thing"));

        Assert.True(table.TryResolve(0x5000, out string name));
        Assert.Equal("libthing.so+0x4000", name);
    }

    [Fact]
    public void Resolve_ReportsFailureForAnAddressInNoMappingAtAll()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/lib/libthing.so"));

        Assert.False(table.TryResolve(0xF0000, out string name));
        Assert.Null(name);
    }

    [Fact]
    public void Resolve_ExcludesTheEndAddressOfARange()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/lib/libthing.so"),
            Symbol(0, 0x2000, 0x2100, "do_the_thing"));

        Assert.True(table.TryResolve(0x20FF, out string inside));
        Assert.Equal("do_the_thing", inside);

        Assert.True(table.TryResolve(0x2100, out string past));
        Assert.Equal("libthing.so+0x1100", past);
    }

    // Kernel addresses have the top bit set and read as NEGATIVE int64. A
    // signed binary search sorts them below every user-space address and
    // misses every kernel symbol in the capture - which is most of the
    // interesting ones on a perf-sampled trace.
    [Fact]
    public void Resolve_FindsKernelSymbolsWhoseAddressesHaveTheTopBitSet()
    {
        const long KernelStart = unchecked((long)0xFFFFFFFF8C29DF90UL);
        const long KernelEnd = unchecked((long)0xFFFFFFFF8C29E020UL);
        const long KernelHit = unchecked((long)0xFFFFFFFF8C29DFABUL);

        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/lib/libthing.so"),
            Symbol(0, 0x2000, 0x2100, "do_the_thing"),
            Mapping(1, unchecked((long)0xFFFF800000000000UL), unchecked((long)0xFFFFFFFFFFFFFFFFUL), "vmlinux"),
            Symbol(1, KernelStart, KernelEnd, "finish_task_switch.isra.0"));

        Assert.True(table.TryResolve(KernelHit, out string name));
        Assert.Equal("finish_task_switch.isra.0", name);
    }

    [Fact]
    public void Resolve_StillFindsUserSpaceSymbolsWhenKernelRangesArePresent()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(1, unchecked((long)0xFFFF800000000000UL), unchecked((long)0xFFFFFFFFFFFFFFFFUL), "vmlinux"),
            Symbol(1, unchecked((long)0xFFFFFFFF8C29DF90UL), unchecked((long)0xFFFFFFFF8C29E020UL), "finish_task_switch.isra.0"),
            Mapping(0, 0x1000, 0x9000, "/usr/lib/libthing.so"),
            Symbol(0, 0x2000, 0x2100, "do_the_thing"));

        Assert.True(table.TryResolve(0x2050, out string name));
        Assert.Equal("do_the_thing", name);
    }

    // GROUND TRUTH. The reported offset must be the module's own ELF virtual
    // address, which is what `addr2line -e <module>` and every symbol server
    // are keyed by - NOT the distance from the start of the mapping. See this
    // file's header for how these numbers were confirmed.
    [Theory]
    [InlineData(0x7d4e867bc070L, 0x7B070L)]   // CompressionNative_DeflateEnd
    [InlineData(0x7d4e867bc0c0L, 0x7B0C0L)]   // CompressionNative_InflateInit2_
    [InlineData(0x7d4e867bc010L, 0x7B010L)]   // CompressionNative_Deflate
    [InlineData(0x7d4e867bc150L, 0x7B150L)]   // CompressionNative_Inflate
    [InlineData(0x7d4e867bc1b0L, 0x7B1B0L)]   // CompressionNative_InflateEnd
    [InlineData(0x7d4e867bbf50L, 0x7AF50L)]   // CompressionNative_DeflateInit2_
    public void ModuleOffset_IsTheElfVirtualAddressNotTheMappingOffset(long instructionPointer, long expectedElfVirtualAddress)
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, CompressionMapStart, CompressionMapEnd, "/usr/share/dotnet/libSystem.IO.Compression.Native.so", CompressionFileOffset, CompressionMetadataId),
            MappingMetadata(CompressionMetadataId, "ba2edad540f790f6afd1809590710c37311b7b2c", CompressionProgramVirtualAddress, CompressionProgramFileOffset));

        Assert.True(table.TryResolve(instructionPointer, out string name));
        Assert.Equal($"libSystem.IO.Compression.Native.so+0x{expectedElfVirtualAddress:X}", name);

        // The naive form would report this instead, and it is not merely
        // different - it points into an unrelated part of the binary.
        Assert.NotEqual($"libSystem.IO.Compression.Native.so+0x{instructionPointer - CompressionMapStart:X}", name);
    }

    // With no ProcessMappingMetadata the bias is unknown. Treating it as zero
    // makes the reported value the module-FILE offset, which is correct
    // whenever p_vaddr == p_offset and is at least a stable identity when it
    // is not - as opposed to the mapping-relative value, which is neither.
    [Fact]
    public void ModuleOffset_FallsBackToTheFileOffsetWhenTheModuleHasNoMetadata()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x7f0000000000L, 0x7f0000100000L, "/usr/lib/libnometa.so", 0x9000));

        Assert.True(table.TryResolve(0x7f0000000100L, out string name));
        Assert.Equal("libnometa.so+0x9100", name);
    }

    [Fact]
    public void ModuleOffset_UsesTheFileNameNotTheWholePath()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.10/libcoreclr.so"));

        Assert.True(table.TryResolve(0x2000, out string name));
        Assert.StartsWith("libcoreclr.so+", name);
    }

    // A managed method published through ProcessSymbol must classify as
    // managed, and everything else - native symbol, kernel symbol, or an
    // address that only resolves to a module - as external. This is what
    // stands in for ThreadSampleType on a perf-sampled capture.
    [Fact]
    public void TryClassifyManaged_SeparatesJittedManagedCodeFromEverythingElse()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/memfd:doublemapper"),
            Symbol(0, 0x2000, 0x2100, "instance void [Some.Asm] Ns.Type::Method(object)[OptimizedTier1]"),
            Symbol(1, 0x3000, 0x3100, "__pthread_mutex_lock"));

        Assert.True(table.TryClassifyManaged(0x2050, out bool managedIsManaged));
        Assert.True(managedIsManaged);

        Assert.True(table.TryClassifyManaged(0x3050, out bool nativeIsManaged));
        Assert.False(nativeIsManaged);

        // Inside the mapping but in no symbol - still a real module, so still
        // "not executing managed code".
        Assert.True(table.TryClassifyManaged(0x8000, out bool moduleOnlyIsManaged));
        Assert.False(moduleOnlyIsManaged);

        // Outside every mapping - no claim either way.
        Assert.False(table.TryClassifyManaged(0xF0000, out bool unknownIsManaged));
        Assert.False(unknownIsManaged);
    }

    [Fact]
    public void Build_ReportsModulesThatShippedNoSymbolsAlongWithTheirBuildIds()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/lib/withsyms.so", 0, 10),
            MappingMetadata(10, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0x1000, 0x1000),
            Symbol(0, 0x2000, 0x2100, "present"),
            Mapping(1, 0x20000, 0x29000, "/usr/lib/stripped.so", 0, 11),
            MappingMetadata(11, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 0x1000, 0x1000));

        List<UniversalSymbolTable.ModuleDebugInfo> missing = table.ModulesMissingSymbols();

        Assert.Single(missing);
        Assert.Equal("/usr/lib/stripped.so", missing[0].FileName);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", missing[0].BuildId);
    }

    // The runtime's JIT code heap is identified from the DATA - a mapping that
    // holds managed symbols - and every mapping sharing that file name counts,
    // because the heap is carved into many mappings and the hottest one on the
    // reference capture is a stub page with no symbols in it at all.
    [Fact]
    public void Build_MarksTheJitCodeHeapAndLabelsItsUnnamedFramesAsStubs()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x2000, "/memfd:doublemapper"),
            Symbol(0, 0x1100, 0x1200, "instance void [Some.Asm] Ns.Type::Method(object)[OptimizedTier1]"),
            // A second mapping of the same memfd holding no symbols at all.
            Mapping(1, 0x5000, 0x6000, "/memfd:doublemapper"));

        Assert.True(table.IsJitCodeModule("/memfd:doublemapper"));

        Assert.True(table.TryResolve(0x5040, out string stubName));
        Assert.StartsWith(UniversalSymbolTable.JitStubPrefix, stubName);
        Assert.Contains("doublemapper+0x", stubName);
    }

    // REGRESSION. "contains ::" does not mean managed - C++ symbols are full
    // of it. Using that test to decide which modules hold managed code flagged
    // ICU as a JIT code heap (its symbols look like
    // icu_78::CollationKeys::writeSortKey...), which re-labelled its
    // unresolved frames as runtime stubs and, more quietly, counted every ICU
    // sample as Managed in the derived ThreadSampleType. The assembly bracket
    // is what identifies the CLR's perf-map form.
    [Fact]
    public void Build_DoesNotMistakeNativeCppModulesForJitCodeHeaps()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/lib/libicui18n.so.78.2"),
            Symbol(0, 0x2000, 0x2100, "icu_78::CollationKeys::writeSortKeyUpToQuaternary(icu_78::CollationIterator&)"));

        Assert.False(table.IsJitCodeModule("/usr/lib/libicui18n.so.78.2"));

        // And an address with no symbol stays a plain module offset, so it is
        // still reported as something whose symbols could be fetched.
        Assert.True(table.TryResolve(0x5000, out string name));
        Assert.DoesNotContain(UniversalSymbolTable.JitStubPrefix, name);
    }

    [Fact]
    public void TryClassifyManaged_DoesNotCountNativeCppAsManaged()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/lib/libicui18n.so.78.2"),
            Symbol(0, 0x2000, 0x2100, "icu_78::CollationKeys::writeSortKeyUpToQuaternary(icu_78::CollationIterator&)"));

        Assert.True(table.TryClassifyManaged(0x2050, out bool isManaged));
        Assert.False(isManaged);
    }

    [Fact]
    public void Build_CountsNoCrossProcessOverlapForASingleProcessCapture()
    {
        UniversalSymbolTable table = BuildTable(
            Mapping(0, 0x1000, 0x9000, "/usr/lib/a.so"),
            Mapping(1, 0x9000, 0x11000, "/usr/lib/b.so"));

        Assert.Equal(0, table.OverlappingProcessRangeCount);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
