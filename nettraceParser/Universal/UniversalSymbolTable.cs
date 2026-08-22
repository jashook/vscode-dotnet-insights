////////////////////////////////////////////////////////////////////////////////
// Module: UniversalSymbolTable.cs
//
// Notes:
// Resolves a raw instruction pointer to a name using the Universal.System
// provider's own description of the machine - the module mappings and symbol
// ranges a `dotnet-trace collect-linux` capture carries inline
// (UniversalProviders.md). This is what makes a v6 capture's stacks readable:
// its samples come from perf_events rather than from the CLR sampler, so their
// frames are plain addresses spanning kernel, native and JIT'd managed code,
// and NONE of them resolve through Rundown/MethodSymbolTable - that table is
// built from MethodLoadVerbose/MethodDCStartVerbose (event ids 143/144), which
// a collect-linux capture does not contain.
//
// Three facts, all verified against the reference capture:
//
//   - ProcessSymbol addresses are ABSOLUTE virtual addresses, not offsets
//     within their mapping, despite the spec wording ("Starting virtual
//     address of the symbol within the mapping"). Confirmed by resolving the
//     capture's own hottest frames: 0xFFFFFFFF8C29DFAB lands exactly inside
//     the [0xffffffff8c29df90, 0xffffffff8c29e0??) range named
//     finish_task_switch.isra.0. Treating them as relative would put every
//     kernel symbol at a nonsense address.
//
//   - ProcessSymbol covers MANAGED methods too, not just native ones. The
//     runtime publishes its JIT'd code through the same mechanism, with the
//     CLR's own perf-map naming - e.g. "instance void [Some.Assembly]
//     Some.Namespace.Type::Method(object)[OptimizedTier1]". That is why this
//     table alone is enough for a collect-linux capture, and why it formats
//     those names into the shape the rest of this project's UI expects (see
//     FormatSymbolName).
//
//   - Only a minority of mappings carry symbols at all - 42 of 561 on the
//     reference capture. The rest are stripped shared objects
//     (libcoreclr.so, libc.so.6) and managed assemblies. An address inside
//     one of those still resolves to something useful - "libcoreclr.so
//     +0x433627" - which is a real, groupable identity rather than a bare
//     address that differs on every sample. Only an address in no mapping at
//     all stays unresolved.
//
// MULTI-PROCESS: collect-linux traces every process on the machine by
// default, and two processes can legitimately map different modules at the
// same address. Mappings are therefore checked for cross-process address
// overlap while building, and OverlappingProcessRangeCount reports the
// result rather than the question being assumed away - a merged, pid-less
// table is exactly correct when that count is 0, which it is on the
// reference capture (a single containerized process plus the shared kernel
// mapping). A non-zero count means some frames may be attributed to the
// wrong process's module, and the caller says so rather than presenting it
// as fact.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Universal {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Progress;
using DotnetInsights.NetTrace.V6;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class UniversalSymbolTable
{
    private struct AddressRange
    {
        public long StartAddress;
        public long EndAddress;
        public string Name;
        public int ProcessId;

        // True when this symbol names JIT'd MANAGED code. The runtime
        // publishes its methods through the same ProcessSymbol events as
        // everything else, distinguishable by the CLR perf-map form's "::"
        // between type and method - native and kernel symbols are plain C
        // identifiers and never contain it. Used to derive a sample's
        // Managed/External kind, which a perf-sampled capture has no other
        // source for; see TryClassifyManaged.
        public bool IsManagedCode;

        // Mapping rows only. The mapping's own offset into the module FILE,
        // plus the module's (p_vaddr - p_offset) bias from its
        // ProcessMappingMetadata. Together these turn a runtime address into
        // the module's own ELF virtual address, which is the only form worth
        // reporting - see FormatModuleOffset.
        public long FileOffset;
        public long VirtualAddressBias;
        public long MetadataId;
    }

    // The ELF details a module's ProcessMappingMetadata carries. build_id is
    // not used to resolve anything today - a capture never contains the
    // symbols for a stripped module - but it is exactly the key a symbol
    // server is looked up by, so it is decoded and surfaced rather than
    // discarded.
    public struct ModuleDebugInfo
    {
        public string FileName;
        public string BuildId;
        public long ProgramHeaderVirtualAddress;
        public long ProgramHeaderFileOffset;
        public bool HasSymbolsInCapture;
    }

    private const string ProcessMappingEventName = "ProcessMapping";
    private const string ProcessSymbolEventName = "ProcessSymbol";
    private const string ProcessMappingMetadataEventName = "ProcessMappingMetadata";

    // Sorted by StartAddress; searched by binary search plus a containment
    // check, so overlapping entries resolve to the last one that starts at or
    // before the address.
    private readonly List<AddressRange> symbolRanges = new List<AddressRange>();
    private readonly List<AddressRange> mappingRanges = new List<AddressRange>();

    // Memoizes whole resolutions, not just lookups. A real capture resolves
    // the same few thousand distinct addresses across ~1M samples and every
    // frame of every stack, so without this the string formatting below would
    // run millions of times.
    private readonly Dictionary<long, string> resolvedNameByAddress = new Dictionary<long, string>();

    // MetadataId -> the module's ELF details, from ProcessMappingMetadata.
    private readonly Dictionary<long, ModuleDebugInfo> debugInfoByMetadataId = new Dictionary<long, ModuleDebugInfo>();

    // MetadataId -> symbols fetched from a symbol server for that module (see
    // Symbols/SymbolStore.cs). Empty unless symbol resolution ran; consulted
    // between "no in-capture symbol covers this address" and the module+offset
    // fallback.
    private readonly Dictionary<long, Symbols.ElfSymbolFile> downloadedSymbolsByMetadataId = new Dictionary<long, Symbols.ElfSymbolFile>();

    public int DownloadedModuleSymbolCount => this.downloadedSymbolsByMetadataId.Count;

    // Lower-cased `os` values seen in any module's VersionMetadata ("ubuntu",
    // "debian", ...). Usually exactly one; a container image built on one
    // distro running on another can legitimately produce more.
    private readonly HashSet<string> detectedDistributions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> DetectedDistributions => this.detectedDistributions;

    // Names that resolved out of the kernel image. Kernel symbols are plain
    // lowercase C identifiers with nothing in the NAME that marks them as
    // kernel - `finish_task_switch` and a libc helper are indistinguishable as
    // text - so the only reliable signal is the module the address came from,
    // which only this table knows. Populated as addresses resolve and read by
    // Cpu/CpuCategoryBuilder to attribute kernel time.
    private readonly HashSet<string> kernelSymbolNames = new HashSet<string>(StringComparer.Ordinal);

    public bool IsKernelSymbol(string name)
    {
        return name != null && this.kernelSymbolNames.Contains(name);
    }

    // File names of mappings that hold JIT-compiled managed code. Identified
    // from the DATA rather than by matching a name: a mapping counts if any
    // managed symbol falls inside it, and then every mapping sharing that file
    // name counts too. On the reference capture that identifies
    // /memfd:doublemapper - the runtime's W^X double-mapped code heap, which
    // holds 5,114 managed symbols, all of them managed.
    //
    // Why the "every mapping sharing the name" part matters: the code heap is
    // carved into many separate mappings and some hold no named method at all.
    // The hottest single one on the reference capture is a 4KB page with zero
    // symbols and 46,351 samples in it, which is a stub page - matching only
    // mappings that directly contain a symbol would have missed exactly the
    // region that mattered most.
    private readonly HashSet<string> jitCodeModuleNames = new HashSet<string>(StringComparer.Ordinal);

    public bool IsJitCodeModule(string moduleName)
    {
        return moduleName != null && this.jitCodeModuleNames.Contains(moduleName);
    }

    // How many distinct stacks CountFramesByModule aims to examine. See its
    // own comment for why sampling is legitimate there and what the exact
    // version cost.
    private const int TargetStacksToSampleForRanking = 100_000;

    public int SymbolCount => this.symbolRanges.Count;

    public int MappingCount => this.mappingRanges.Count;

    public int ProcessCount { get; private set; }

    // Number of mapping pairs from DIFFERENT processes whose address ranges
    // overlap. 0 means this pid-less table is exactly correct for the capture.
    public int OverlappingProcessRangeCount { get; private set; }

    public bool IsEmpty => this.symbolRanges.Count == 0 && this.mappingRanges.Count == 0;

    public static UniversalSymbolTable Build(List<EventRecord> events, V6ThreadTable threadTable, Action<double> onProgress = null)
    {
        UniversalSymbolTable table = new UniversalSymbolTable();

        HashSet<int> processIds = new HashSet<int>();

        Span<EventRecord> eventsSpan = CollectionsMarshal.AsSpan(events);

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            if (onProgress != null && (eventIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress((double)eventIndex / eventsSpan.Length);
            }

            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            if (record.ProviderName != V6Format.UniversalSystemProviderName)
            {
                continue;
            }

            bool isMapping = record.EventName == ProcessMappingEventName;
            bool isSymbol = !isMapping && record.EventName == ProcessSymbolEventName;
            bool isMappingMetadata = !isMapping && !isSymbol && record.EventName == ProcessMappingMetadataEventName;

            if (isMappingMetadata)
            {
                table.ReadMappingMetadata(record);
                continue;
            }

            if (!isMapping && !isSymbol)
            {
                continue;
            }

            long startAddress;
            long endAddress;

            if (!TryGetInt64(record.Fields, "StartAddress", out startAddress) ||
                !TryGetInt64(record.Fields, "EndAddress", out endAddress))
            {
                continue;
            }

            int processId = 0;

            if (threadTable != null)
            {
                threadTable.TryGetProcessId(record.ThreadId, out processId);
            }

            processIds.Add(processId);

            AddressRange range = new AddressRange();
            range.StartAddress = startAddress;
            range.EndAddress = endAddress;
            range.ProcessId = processId;

            if (isMapping)
            {
                string fileName;

                if (!TryGetString(record.Fields, "FileName", out fileName) || fileName.Length == 0)
                {
                    continue;
                }

                range.Name = fileName;

                long fileOffset;
                TryGetInt64(record.Fields, "FileOffset", out fileOffset);
                range.FileOffset = fileOffset;

                long mappingMetadataId;
                TryGetInt64(record.Fields, "MetadataId", out mappingMetadataId);
                range.MetadataId = mappingMetadataId;

                table.mappingRanges.Add(range);
            }
            else
            {
                string symbolName;

                if (!TryGetString(record.Fields, "Name", out symbolName) || symbolName.Length == 0)
                {
                    continue;
                }

                range.Name = symbolName;
                range.IsManagedCode = IsClrPerfMapName(symbolName);
                table.symbolRanges.Add(range);
            }
        }

        table.ProcessCount = processIds.Count;

        // Applied after the pass, not during it: a ProcessMappingMetadata
        // event is free to arrive after the ProcessMapping rows that reference
        // it, and on the reference capture it does.
        table.ApplyModuleBiases();

        table.symbolRanges.Sort(CompareByStartAddress);
        table.mappingRanges.Sort(CompareByStartAddress);

        table.OverlappingProcessRangeCount = CountCrossProcessOverlaps(table.mappingRanges);
        table.IdentifyJitCodeModules();

        return table;
    }

    // Symbol first, then the containing module plus offset, then nothing.
    // Returns false only for an address in no known mapping at all, which
    // leaves the caller free to render it however it already renders an
    // unresolved frame.
    public bool TryResolve(long instructionPointer, out string name)
    {
        if (this.resolvedNameByAddress.TryGetValue(instructionPointer, out name))
        {
            return name != null;
        }

        name = this.ResolveUncached(instructionPointer);
        this.resolvedNameByAddress[instructionPointer] = name;

        return name != null;
    }

    // Answers "was this address executing managed code" for an address, which
    // is the question ThreadSampleType answers on a v5 capture and which a
    // perf-sampled v6 capture carries no field for. Returns false for a
    // native or kernel symbol AND for an address that only resolves to a
    // module (libcoreclr.so, libc.so.6) - being inside a real native module
    // is exactly what "not executing managed code" means. Returns false from
    // the method itself only when the address is in no known mapping at all,
    // where no claim can be made either way.
    public bool TryClassifyManaged(long instructionPointer, out bool isManagedCode)
    {
        isManagedCode = false;

        int symbolIndex = FindContainingRange(this.symbolRanges, instructionPointer);

        if (symbolIndex >= 0)
        {
            isManagedCode = this.symbolRanges[symbolIndex].IsManagedCode;
            return true;
        }

        return FindContainingRange(this.mappingRanges, instructionPointer) >= 0;
    }

    private string ResolveUncached(long instructionPointer)
    {
        int symbolIndex = FindContainingRange(this.symbolRanges, instructionPointer);

        if (symbolIndex >= 0)
        {
            string resolvedName = FormatSymbolName(this.symbolRanges[symbolIndex].Name);

            int containingMapping = FindContainingRange(this.mappingRanges, instructionPointer);

            if (containingMapping >= 0 && IsKernelModule(this.mappingRanges[containingMapping].Name))
            {
                this.kernelSymbolNames.Add(resolvedName);
            }

            return resolvedName;
        }

        int mappingIndex = FindContainingRange(this.mappingRanges, instructionPointer);

        if (mappingIndex >= 0)
        {
            AddressRange mapping = this.mappingRanges[mappingIndex];
            long elfVirtualAddress = ToElfVirtualAddress(mapping, instructionPointer);

            Symbols.ElfSymbolFile moduleSymbols;

            if (this.downloadedSymbolsByMetadataId.TryGetValue(mapping.MetadataId, out moduleSymbols) && moduleSymbols != null)
            {
                string symbolName;
                long offsetIntoFunction;

                if (moduleSymbols.TryResolve(elfVirtualAddress, out symbolName, out offsetIntoFunction))
                {
                    symbolName = FormatSymbolName(symbolName);

                    if (IsKernelModule(mapping.Name))
                    {
                        this.kernelSymbolNames.Add(symbolName);
                    }

                    // The bare function name, not name+offset - matching how
                    // an in-capture ProcessSymbol resolves, so the two sources
                    // aggregate into the same row rather than splitting one
                    // function across every instruction sampled inside it.
                    return symbolName;
                }
            }

            return FormatModuleOffset(mapping, instructionPointer);
        }

        return null;
    }

    // Binary search for the last range starting at or before the address,
    // then a containment check. Ranges may overlap (a symbol inside a
    // mapping, or two processes' mappings), so this walks backwards over
    // equal/covering starts rather than trusting a single hit.
    private static int FindContainingRange(List<AddressRange> ranges, long address)
    {
        int low = 0;
        int high = ranges.Count - 1;
        int candidate = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);

            // Unsigned comparison: kernel addresses have the top bit set and
            // read as negative as int64, which would sort them below every
            // user-space address and make a signed search miss them entirely.
            if (CompareUnsigned(ranges[middle].StartAddress, address) <= 0)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        while (candidate >= 0)
        {
            AddressRange range = ranges[candidate];

            if (CompareUnsigned(address, range.EndAddress) < 0)
            {
                return candidate;
            }

            // A shorter range can hide a longer one that also covers this
            // address, but only a bounded walk back is worth doing - ranges
            // are overwhelmingly disjoint.
            --candidate;

            if (candidate >= 0 && CompareUnsigned(ranges[candidate].StartAddress, address) > 0)
            {
                return -1;
            }
        }

        return -1;
    }


    // The label for an address inside a module that shipped no symbols.
    //
    // The offset reported is the module's own ELF VIRTUAL ADDRESS, not the
    // distance from the start of the mapping, and the difference is the whole
    // point: only the former is a real address in the binary. A mapping starts
    // at some offset INTO the file (the reference capture maps libcoreclr.so's
    // text at file offset 0x1C8000), and the file is laid out with its own
    // (p_vaddr - p_offset) bias, so:
    //
    //     elfVirtualAddress = (ip - mapping.Start) + mapping.FileOffset
    //                         - p_offset + p_vaddr
    //
    // Verified against ground truth rather than derived and hoped for. One
    // module in the reference capture - libSystem.IO.Compression.Native.so -
    // carries BOTH in-capture ProcessSymbol entries and the metadata, so its
    // six known symbol addresses can be fed through both candidate formulas
    // and checked against the real binary's symbol table (fetched by build_id).
    // The naive "ip - mapping.Start" scored 0 of 6 - it lands in no symbol at
    // all - and this one scored 6 of 6.
    //
    // That makes the difference user-visible, not academic: the number printed
    // here is what somebody pastes into `addr2line -e libcoreclr.so <addr>` or
    // looks up on a symbol server, and the naive form silently resolves to the
    // WRONG function rather than to nothing.
    //
    // With no metadata for the module the bias is unknown and treated as zero,
    // which makes this the module-file offset. That is correct whenever
    // p_vaddr == p_offset (common) and still a stable, groupable identity when
    // it is not.
    private string FormatModuleOffset(AddressRange mapping, long instructionPointer)
    {
        string moduleOffset = $"{GetFileNameOnly(mapping.Name)}+0x{ToElfVirtualAddress(mapping, instructionPointer):X}";

        // An address in the JIT code heap that no perf-map entry covers is a
        // runtime-generated stub - precode, a call-counting stub, a jump stub,
        // a delegate thunk. It is unnamed for a permanent reason, not for a
        // fixable one: this code is generated at runtime and no symbol file
        // anywhere describes it. Marking it keeps it out of the "download
        // symbols and this goes away" bucket, which on the reference capture
        // is the difference between telling somebody 12% of their profile is
        // missing symbols and telling them 7% is.
        if (this.IsJitCodeModule(mapping.Name))
        {
            return JitStubPrefix + moduleOffset;
        }

        return moduleOffset;
    }

    // Deliberately a prefix rather than a suffix: the "module+0xADDR" shape is
    // what marks an unresolved frame everywhere else, and appending to it
    // would break that test rather than override it.
    public const string JitStubPrefix = "[jit] ";

    private static long ToElfVirtualAddress(AddressRange mapping, long instructionPointer)
    {
        long moduleFileOffset = (instructionPointer - mapping.StartAddress) + mapping.FileOffset;
        return moduleFileOffset + mapping.VirtualAddressBias;
    }

    // Attaches symbols fetched for one module. Must happen before anything
    // resolves an address through this table - the resolution cache is dropped
    // here to make that safe rather than assumed.
    public void AddModuleSymbols(long metadataId, Symbols.ElfSymbolFile symbols)
    {
        this.downloadedSymbolsByMetadataId[metadataId] = symbols;
        this.resolvedNameByAddress.Clear();
    }

    // Roughly how many stack frames land in each module, keyed by the
    // module's MetadataId. This is what makes symbol downloading DEMAND
    // driven: a real capture describes hundreds of mappings, the large
    // majority of which are managed assemblies and never-executed pages that
    // no stack ever touches, and fetching symbols for all of them would mean
    // hundreds of megabytes to name functions nobody sampled.
    //
    // APPROXIMATE, ON PURPOSE, AND ONLY EVER USED AS A RANKING. The exact
    // version of this - every frame of every distinct stack, two binary
    // searches each - measured 4,828ms on the reference capture (936,389
    // stacks, ~17.4M frames), which is longer than the entire rest of the
    // parse. Nothing here is reported to the user or written to the JSON: the
    // counts choose which modules clear a 0.1% share floor, and a strided
    // sample of ~100,000 stacks settles that question with enormous margin.
    // The stride is deterministic, so the same capture always selects the same
    // modules. Note that this is only safe because the CALLER has no
    // minimum-share threshold - a sampled count and a floor together make
    // selection depend on which stacks the stride happened to hit, which
    // measurably lost a module's symbols before that floor was removed (see
    // Symbols/NativeSymbolResolution.MaximumModulesToFetch).
    //
    // The lookup order also matters and is the reverse of the obvious one: the
    // MAPPING is searched first (561 ranges) and the in-capture symbol table
    // (10,187 ranges) only for modules that actually have in-capture symbols.
    // The module that dominates a .NET capture - libcoreclr.so - has none, so
    // the symbol search could never have matched for the overwhelming majority
    // of frames.
    //
    // Together: 4,828ms -> ~390ms on the reference capture.
    public Dictionary<long, long> CountFramesByModule(StackTable stacks)
    {
        Dictionary<long, long> framesByMetadataId = new Dictionary<long, long>();

        if (stacks == null || stacks.Count == 0)
        {
            return framesByMetadataId;
        }

        int stride = stacks.Count / TargetStacksToSampleForRanking;

        if (stride < 1)
        {
            stride = 1;
        }

        for (int stackIndex = 0; stackIndex < stacks.Count; stackIndex += stride)
        {
            long[] frames = stacks.FramesAt(stackIndex);

            for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
            {
                long frameAddress = frames[frameIndex];

                int mappingIndex = FindContainingRange(this.mappingRanges, frameAddress);

                if (mappingIndex < 0)
                {
                    continue;
                }

                long metadataId = this.mappingRanges[mappingIndex].MetadataId;

                // A frame already named by an in-capture symbol needs nothing
                // downloaded, so it does not count toward any module's case
                // for being fetched. Only worth checking for modules that
                // shipped symbols at all.
                ModuleDebugInfo info;

                if (this.debugInfoByMetadataId.TryGetValue(metadataId, out info) && info.HasSymbolsInCapture)
                {
                    if (FindContainingRange(this.symbolRanges, frameAddress) >= 0)
                    {
                        continue;
                    }
                }

                framesByMetadataId.TryGetValue(metadataId, out long existing);
                framesByMetadataId[metadataId] = existing + stride;
            }
        }

        return framesByMetadataId;
    }

    public bool TryGetModuleDebugInfo(long metadataId, out ModuleDebugInfo info)
    {
        return this.debugInfoByMetadataId.TryGetValue(metadataId, out info);
    }

    // Decodes one ProcessMappingMetadata event's SymbolMetadata JSON. Shape
    // (from the reference capture):
    //
    //   {"type": "ELF","debug_link": "libcoreclr.so.dbg",
    //    "build_id": "e7f47fde...","p_vaddr": "0x1c9650","p_offset": "0x1c8650"}
    //
    // The numbers are hex STRINGS, not JSON numbers.
    private void ReadMappingMetadata(in EventRecord record)
    {
        long metadataId;

        if (!TryGetInt64(record.Fields, "Id", out metadataId))
        {
            return;
        }

        string symbolMetadata;

        if (!TryGetString(record.Fields, "SymbolMetadata", out symbolMetadata) || symbolMetadata.Length == 0)
        {
            return;
        }

        ModuleDebugInfo info = new ModuleDebugInfo();
        info.BuildId = ExtractJsonString(symbolMetadata, "build_id");
        info.ProgramHeaderVirtualAddress = ParseHex(ExtractJsonString(symbolMetadata, "p_vaddr"));
        info.ProgramHeaderFileOffset = ParseHex(ExtractJsonString(symbolMetadata, "p_offset"));

        // VersionMetadata names the DISTRIBUTION a module came from, e.g.
        // {"type":"deb","os":"ubuntu","name":"openssl","version":"3.5.5-1ubuntu3.2",...}.
        // That is the one thing that says where a distro library's symbols can
        // be found - Microsoft's symbol server has .NET's own modules and
        // nothing else, so libc, openssl and friends need that distro's own
        // debuginfod server. Recording it here means the capture answers the
        // question itself instead of the user having to know which distro the
        // traced machine ran.
        string versionMetadata;

        if (TryGetString(record.Fields, "VersionMetadata", out versionMetadata) && versionMetadata != null && versionMetadata.Length > 0)
        {
            string operatingSystem = ExtractJsonString(versionMetadata, "os");

            if (operatingSystem != null && operatingSystem.Length > 0)
            {
                this.detectedDistributions.Add(operatingSystem.ToLowerInvariant());
            }
        }

        this.debugInfoByMetadataId[metadataId] = info;
    }

    private void ApplyModuleBiases()
    {
        HashSet<long> metadataIdsWithSymbols = new HashSet<long>();

        for (int rangeIndex = 0; rangeIndex < this.mappingRanges.Count; ++rangeIndex)
        {
            AddressRange mapping = this.mappingRanges[rangeIndex];

            ModuleDebugInfo info;

            if (!this.debugInfoByMetadataId.TryGetValue(mapping.MetadataId, out info))
            {
                continue;
            }

            mapping.VirtualAddressBias = info.ProgramHeaderVirtualAddress - info.ProgramHeaderFileOffset;
            this.mappingRanges[rangeIndex] = mapping;

            if (info.FileName == null)
            {
                info.FileName = mapping.Name;
                this.debugInfoByMetadataId[mapping.MetadataId] = info;
            }
        }

        // A module counts as symbolicated if ANY symbol range falls inside any
        // of its mappings. Used only for reporting - see ModulesMissingSymbols.
        for (int symbolIndex = 0; symbolIndex < this.symbolRanges.Count; ++symbolIndex)
        {
            long symbolStart = this.symbolRanges[symbolIndex].StartAddress;

            for (int rangeIndex = 0; rangeIndex < this.mappingRanges.Count; ++rangeIndex)
            {
                AddressRange mapping = this.mappingRanges[rangeIndex];

                if (CompareUnsigned(mapping.StartAddress, symbolStart) <= 0 && CompareUnsigned(symbolStart, mapping.EndAddress) < 0)
                {
                    metadataIdsWithSymbols.Add(mapping.MetadataId);
                    break;
                }
            }
        }

        foreach (long metadataId in metadataIdsWithSymbols)
        {
            ModuleDebugInfo info;

            if (this.debugInfoByMetadataId.TryGetValue(metadataId, out info))
            {
                info.HasSymbolsInCapture = true;
                this.debugInfoByMetadataId[metadataId] = info;
            }
        }
    }

    // Every module the capture describes but ships no symbols for, with the
    // build_id a symbol server would be asked for. This is the answer to "why
    // is this frame still an offset" - the capture never contained the
    // symbols, it only contained the recipe for finding them.
    public List<ModuleDebugInfo> ModulesMissingSymbols()
    {
        List<ModuleDebugInfo> missing = new List<ModuleDebugInfo>();

        foreach (KeyValuePair<long, ModuleDebugInfo> entry in this.debugInfoByMetadataId)
        {
            if (!entry.Value.HasSymbolsInCapture && entry.Value.FileName != null)
            {
                missing.Add(entry.Value);
            }
        }

        return missing;
    }

    private static string ExtractJsonString(string json, string key)
    {
        // These blobs are a handful of flat string properties each, and there
        // are ~176 of them in a whole capture, so a scan beats standing up a
        // JSON document per module.
        string pattern = "\"" + key + "\"";
        int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);

        if (keyIndex < 0)
        {
            return null;
        }

        int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);

        if (colonIndex < 0)
        {
            return null;
        }

        int openQuote = json.IndexOf('"', colonIndex + 1);

        if (openQuote < 0)
        {
            return null;
        }

        int closeQuote = json.IndexOf('"', openQuote + 1);

        if (closeQuote < 0)
        {
            return null;
        }

        return json.Substring(openQuote + 1, closeQuote - openQuote - 1);
    }

    private static long ParseHex(string value)
    {
        if (value == null)
        {
            return 0;
        }

        ReadOnlySpan<char> digits = value.AsSpan();

        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits.Slice(2);
        }

        long parsed;

        if (!long.TryParse(digits, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out parsed))
        {
            return 0;
        }

        return parsed;
    }

    private static int CompareByStartAddress(AddressRange left, AddressRange right)
    {
        return CompareUnsigned(left.StartAddress, right.StartAddress);
    }

    private static int CompareUnsigned(long left, long right)
    {
        return ((ulong)left).CompareTo((ulong)right);
    }

    private static int CountCrossProcessOverlaps(List<AddressRange> sortedMappings)
    {
        int overlapCount = 0;

        for (int rangeIndex = 1; rangeIndex < sortedMappings.Count; ++rangeIndex)
        {
            AddressRange previous = sortedMappings[rangeIndex - 1];
            AddressRange current = sortedMappings[rangeIndex];

            if (previous.ProcessId == current.ProcessId)
            {
                continue;
            }

            if (CompareUnsigned(current.StartAddress, previous.EndAddress) < 0)
            {
                ++overlapCount;
            }
        }

        return overlapCount;
    }

    // Whether a symbol name is the CLR's perf-map form for a JIT'd managed
    // method - "instance void [Some.Assembly] Ns.Type::Method(...)".
    //
    // The test is the ASSEMBLY BRACKET, not the presence of "::". This file
    // already learned that once, in FormatSymbolName, and the naive version
    // was reintroduced here and caused a second, quieter failure: ICU's C++
    // symbols (icu_78::CollationKeys::writeSortKeyUpToQuaternary) matched, so
    // libicui18n was flagged as a module full of managed code. That made it a
    // "JIT code heap" and re-labelled its unresolved frames as runtime stubs,
    // and - worse, because it is silent - it made every ICU sample count as
    // Managed in the derived ThreadSampleType, inflating the managed fraction
    // the Threading view classifies threads on.
    private static bool IsClrPerfMapName(string symbolName)
    {
        int scopeIndex = symbolName.IndexOf("::", StringComparison.Ordinal);

        if (scopeIndex < 0)
        {
            return false;
        }

        return symbolName.LastIndexOf("] ", scopeIndex, StringComparison.Ordinal) >= 0;
    }

    // The kernel image, as `dotnet-trace collect-linux` names it. Its mapping
    // covers the whole upper half of the address space, so every kernel frame
    // in the capture falls inside it.
    private static bool IsKernelModule(string moduleName)
    {
        return moduleName != null &&
            (moduleName.EndsWith("vmlinux", StringComparison.Ordinal) ||
             moduleName.StartsWith("[kernel", StringComparison.Ordinal));
    }

    private void IdentifyJitCodeModules()
    {
        for (int symbolIndex = 0; symbolIndex < this.symbolRanges.Count; ++symbolIndex)
        {
            if (!this.symbolRanges[symbolIndex].IsManagedCode)
            {
                continue;
            }

            int mappingIndex = FindContainingRange(this.mappingRanges, this.symbolRanges[symbolIndex].StartAddress);

            if (mappingIndex >= 0)
            {
                this.jitCodeModuleNames.Add(this.mappingRanges[mappingIndex].Name);
            }
        }
    }

    private static string GetFileNameOnly(string path)
    {
        int lastSeparator = path.LastIndexOf('/');

        if (lastSeparator < 0 || lastSeparator == path.Length - 1)
        {
            return path;
        }

        return path.Substring(lastSeparator + 1);
    }

    // Managed symbols arrive in the CLR's perf-map form:
    //
    //   instance void [Some.Assembly] Some.Namespace.Type::Method(object)[OptimizedTier1]
    //
    // Every other method name in this project is rendered as
    // Namespace.Type.Method, and the ranked tables split on the last '.' to
    // dim the type prefix (see media/rankedTable.js's splitQualifiedName), so
    // passing this through unchanged would leave the CPU view's busiest column
    // full of signatures its own formatting cannot read. The JIT tier is kept -
    // it is genuinely useful next to a hot method.
    //
    // The "[Assembly] " prefix is REQUIRED for a name to be rewritten, and that
    // is the whole safety of this function rather than an incidental detail:
    // "contains ::" is NOT enough to identify a managed name, because C++
    // symbols are full of it. A collect-linux capture resolves plenty of them
    // out of the runtime's own native dependencies - icu_78::CollationKeys::
    // writeSortKeyUpToQuaternary, std::__atomic_futex_unsigned_base::
    // _M_futex_wait_until - and an earlier version of this function, which
    // keyed only on "::", rewrote those into a mangled hybrid
    // ("icu_78.CollationKeys::writeSortKeyUpToQuaternary") that is neither the
    // real symbol nor a valid managed name. Native symbols have no assembly
    // bracket, so requiring one leaves every one of them untouched.
    public static string FormatSymbolName(string rawName)
    {
        // A native symbol out of a symbol server arrives Itanium-mangled.
        // Demangled here rather than at load time because a module's symbol
        // table has thousands of entries and only the handful a stack actually
        // lands on are ever displayed - see Symbols/NativeSymbolDemangler.cs.
        if (Symbols.NativeSymbolDemangler.IsMangled(rawName))
        {
            return Symbols.NativeSymbolDemangler.Demangle(rawName);
        }

        int scopeIndex = rawName.IndexOf("::", StringComparison.Ordinal);

        if (scopeIndex < 0)
        {
            return rawName;
        }

        string tier = ExtractTier(rawName);
        int bodyEnd = rawName.Length;

        if (tier != null)
        {
            bodyEnd = rawName.LastIndexOf('[');

            while (bodyEnd > 0 && rawName[bodyEnd - 1] == ' ')
            {
                --bodyEnd;
            }
        }

        // The signature's argument list is dropped - the rest of this project
        // renders methods without one. Searched from the "::" rather than from
        // the start, because a return type can carry parentheses of its own:
        // "instance void modreq([System.Runtime]...IsExternalInit) [Asm]
        // Type::set_Foo(...)" is a real name from the reference capture, and
        // taking its first '(' would cut the body off before the type even
        // began.
        int signatureStart = rawName.IndexOf('(', scopeIndex);

        if (signatureStart >= 0 && signatureStart < bodyEnd)
        {
            bodyEnd = signatureStart;
        }

        if (scopeIndex >= bodyEnd)
        {
            return rawName;
        }

        // The last "] " before the first "::" is the assembly bracket. Last
        // rather than first so a return type that is itself an array
        // ("int32[] [Asm] T::M()") does not win.
        int assemblyEnd = rawName.LastIndexOf("] ", scopeIndex, StringComparison.Ordinal);

        if (assemblyEnd < 0)
        {
            return rawName;
        }

        int typeStart = assemblyEnd + 2;

        while (typeStart < bodyEnd && rawName[typeStart] == ' ')
        {
            ++typeStart;
        }

        if (typeStart >= bodyEnd)
        {
            return rawName;
        }

        // Replace every "::" - an explicit interface implementation carries
        // more than one.
        string qualifiedName = rawName.Substring(typeStart, bodyEnd - typeStart).Replace("::", ".");

        if (tier == null)
        {
            return qualifiedName;
        }

        return $"{qualifiedName} [{tier}]";
    }

    // The trailing "[OptimizedTier1]"/"[QuickJitted]"/... marker, if present.
    // Rejects anything containing a space or a comma so a generic argument
    // list ("[[System.Int32, System.Private.CoreLib]]") is never mistaken for
    // one.
    private static string ExtractTier(string rawName)
    {
        if (rawName.Length == 0 || rawName[rawName.Length - 1] != ']')
        {
            return null;
        }

        int tierStart = rawName.LastIndexOf('[');

        if (tierStart < 0 || tierStart == rawName.Length - 2)
        {
            return null;
        }

        string candidate = rawName.Substring(tierStart + 1, rawName.Length - tierStart - 2);

        if (candidate.IndexOf(' ') >= 0 || candidate.IndexOf(',') >= 0 || candidate.IndexOf('[') >= 0)
        {
            return null;
        }

        return candidate;
    }

    private static bool TryGetInt64(Dictionary<string, object> fields, string fieldName, out long value)
    {
        value = 0;

        object raw;

        if (fields == null || !fields.TryGetValue(fieldName, out raw) || raw == null)
        {
            return false;
        }

        if (raw is long longValue)
        {
            value = longValue;
            return true;
        }

        if (raw is int intValue)
        {
            value = intValue;
            return true;
        }

        return false;
    }

    private static bool TryGetString(Dictionary<string, object> fields, string fieldName, out string value)
    {
        value = null;

        object raw;

        if (fields == null || !fields.TryGetValue(fieldName, out raw))
        {
            return false;
        }

        value = raw as string;
        return value != null;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Universal)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
