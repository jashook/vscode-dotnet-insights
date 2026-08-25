////////////////////////////////////////////////////////////////////////////////
// Module: PerfCaptureAnalysis.cs
//
// Notes:
// The bridge between the perf.data reader and nettraceParser's analysis half.
// Everything downstream of this file - the CPU view, its caller trees, the
// flame graph, the category breakdown, the ranked tables and the webview that
// renders them - is shared with the .NET tool and knows nothing about perf.
//
// That is possible because the analysis pipeline's real input is small and
// already source-agnostic: a StackTable of raw instruction pointers, a
// List<SampleEvent>, and a symbol table that can answer "what function is at
// this address". It was made that way for `dotnet-trace collect-linux`, which
// is itself perf_events data arriving in a different container.
//
// The one genuinely non-obvious part is how a perf capture becomes a
// UniversalSymbolTable. That type is built from a v6 capture's own
// ProcessMapping / ProcessMappingMetadata events, and this SYNTHESIZES those
// events from PERF_RECORD_MMAP2 records rather than reimplementing the type.
// The two models line up almost exactly - a mapping is an address range, a
// file, and an offset into that file, in both - and the metadata event's
// SymbolMetadata carries precisely the build id and p_vaddr/p_offset bias that
// a perf capture lacks and an ELF file supplies. Synthesizing is deliberate:
// it needed no change at all to the shared code, which is what makes this
// project's reuse claim testable rather than aspirational.
//
// A perf capture's symbols never come from the capture itself. Every name is
// read from a module file located by BUILD ID, and handed over through
// AddModuleSymbols - the same path the .NET tool uses for symbols fetched from
// a symbol server.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Analysis {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Rundown;
using DotnetInsights.NetTrace.Symbols;
using DotnetInsights.NetTrace.Universal;
using DotnetInsights.NetTrace.V6;
using DotnetInsights.Perf.Dwarf;
using DotnetInsights.Perf.PerfData;
using DotnetInsights.Perf.PerfScript;
using DotnetInsights.Perf.Symbols;
using DotnetInsights.Perf.Tracepoints;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class PerfCaptureAnalysis : IPerfRecordSink
{
    // perf timestamps are CLOCK_MONOTONIC nanoseconds since boot, so they are
    // enormous and only meaningful as differences.
    private const double NanosecondsPerMillisecond = 1000000.0;

    // A capture's callchains are bounded by attr.sample_max_stack, 127 by
    // default. The scratch buffer is grown rather than assumed so a capture
    // recorded with a raised limit still works.
    private long[] frameScratch = new long[256];

    private readonly StackTable stacks = new StackTable();
    private readonly List<SampleEvent> samples = new List<SampleEvent>();
    private readonly List<PerfMapping> mappings = new List<PerfMapping>();

    private readonly Dictionary<int, string> commByThreadId = new Dictionary<int, string>();
    private readonly Dictionary<int, int> processIdByThreadId = new Dictionary<int, int>();

    private ulong minimumSampleTime = ulong.MaxValue;
    private bool anySampleHadTime;

    // Set only for a `--call-graph dwarf` capture, where the callchain the
    // kernel recorded is just the sampled instruction pointer and the real
    // stack has to be reconstructed from the copied stack bytes. Null for a
    // frame-pointer capture, which already carries its chain.
    private DwarfUnwinder unwinder;
    private readonly List<long> unwoundFrames = new List<long>();

    public long DwarfUnwoundSampleCount { get; private set; }

    public void AttachUnwinder(DwarfUnwinder attachedUnwinder)
    {
        this.unwinder = attachedUnwinder;
    }

    // The scheduling/futex projection, built only when the capture actually
    // recorded those tracepoints. Null otherwise - a CPU-only capture has
    // nothing to say about why a thread was not running, and saying nothing is
    // the correct output rather than an empty view that implies "no blocking".
    public TracepointProjector Tracepoints { get; private set; }

    ////////////////////////////////////////////////////////////////////////////
    // Inline expansion.
    //
    // A frame that was inlined has no stack frame and therefore no address of
    // its own - it shares its caller's. To put it into a stack that the shared
    // pipeline can key by address like any other frame, each distinct inlined
    // NAME is given a SYNTHETIC address and registered as a symbol covering it.
    //
    // The synthetic range starts at 2^48, above the 47-bit user address space
    // on both x86-64 and AArch64 and below the kernel half, so it cannot
    // collide with a real mapping. Registering it as an ordinary
    // mapping+symbol pair means nothing downstream - not the caller trees, not
    // the flame graph, not the category classifier - needs to know these frames
    // are different from any other.
    ////////////////////////////////////////////////////////////////////////////

    private const long SyntheticInlineAddressBase = 1L << 48;
    private const long SyntheticInlineAddressStride = 16;

    private PerfSymbolTable inlineSymbolLookup;
    private readonly Dictionary<string, DebugInfoIndex> inlineIndexByModule = new Dictionary<string, DebugInfoIndex>(StringComparer.Ordinal);
    private readonly Dictionary<long, long[]> expandedFramesByAddress = new Dictionary<long, long[]>();
    private readonly Dictionary<string, long> syntheticAddressByInlineName = new Dictionary<string, long>(StringComparer.Ordinal);
    private readonly List<string> inlineChainScratch = new List<string>();
    private long[] expansionScratch = new long[512];

    // Counts DISTINCT ADDRESSES that expanded, not samples - expansion is
    // memoized per address, so a per-sample figure would need separate
    // bookkeeping on a path that runs millions of times for a number nobody
    // reads. Named for what it actually measures.
    public long AddressesWithInlinedFrames { get; private set; }

    public int DistinctInlinedFunctionCount => this.syntheticAddressByInlineName.Count;

    // Enables inline expansion. Off unless a symbol table is supplied, because
    // it needs the module files to read `.debug_info` from.
    public void AttachInlineExpander(PerfSymbolTable symbols)
    {
        this.inlineSymbolLookup = symbols;
    }

    private PerfDataFile attachedFile;
    private bool tracepointsInitialized;

    // Held rather than read: this is called BEFORE PerfDataFile.Read, so the
    // capture's feature sections - which is where the tracepoint formats live -
    // have not been parsed yet and TraceFormats is still null. The projector is
    // therefore built on first sight of a tracepoint sample, by which point the
    // features have been read (Read parses them before walking records).
    public void AttachFile(PerfDataFile capture)
    {
        this.attachedFile = capture;
    }

    public StackTable Stacks => this.stacks;

    public List<SampleEvent> Samples => this.samples;

    public IReadOnlyDictionary<int, string> CommByThreadId => this.commByThreadId;

    public IReadOnlyDictionary<int, int> ProcessIdByThreadId => this.processIdByThreadId;

    public MethodSymbolTable SymbolTable { get; private set; }

    public UniversalSymbolTable NativeSymbols { get; private set; }

    public int ModulesWithSymbols { get; private set; }

    public int ModulesWithoutSymbols { get; private set; }

    public readonly List<string> UnresolvedModules = new List<string>();

    ////////////////////////////////////////////////////////////////////////////
    // Record sink
    ////////////////////////////////////////////////////////////////////////////

    public void OnSample(PerfEventAttr attr, ref PerfSample sample)
    {
        // A tracepoint sample rides the same record type but is an OCCURRENCE
        // of an event, not a periodic observation of where a thread was.
        // Counting one as CPU time would attribute the profile to whatever ran
        // when a lock happened to be taken - so these never become
        // SampleEvents, and go to the scheduling/futex projection instead.
        if (attr.IsTracepoint)
        {
            this.ObserveTracepoint(attr, ref sample);
            return;
        }

        // A DWARF capture's kernel-recorded callchain contains the sampled
        // instruction pointer and its context markers and nothing else, so the
        // real stack comes from unwinding the copied stack bytes. Preferred
        // whenever an unwinder is attached AND it produced more than the one
        // frame the callchain already had - a failed unwind falls back rather
        // than losing the leaf.
        if (this.unwinder != null && sample.HasUserRegisters && sample.HasUserStack)
        {
            if (this.unwinder.TryUnwind((int)sample.ProcessId, attr, ref sample, this.unwoundFrames) && this.unwoundFrames.Count > 0)
            {
                this.AddSample(ref sample, CollectionsMarshal.AsSpan(this.unwoundFrames));
                ++this.DwarfUnwoundSampleCount;
                return;
            }
        }

        if (!sample.HasCallchain || sample.Callchain.Length == 0)
        {
            return;
        }

        int frameCount = this.FilterCallchain(ref sample);
        if (frameCount == 0)
        {
            return;
        }

        // Leaf-first, which is the order perf writes and the order every stack
        // in the shared code carries.
        this.AddSample(ref sample, new ReadOnlySpan<long>(this.frameScratch, 0, frameCount));
    }

    // Copies a sample's callchain into the scratch buffer, dropping the
    // PERF_CONTEXT_* markers. Those delimit the kernel and user portions of a
    // chain and are not addresses; left in, they rank 0xFFFFFFFFFFFFFF80 as a
    // hot method and appear as a frame in every caller tree.
    private int FilterCallchain(ref PerfSample sample)
    {
        if (this.frameScratch.Length < sample.Callchain.Length)
        {
            this.frameScratch = new long[sample.Callchain.Length * 2];
        }

        int frameCount = 0;
        for (int frameIndex = 0; frameIndex < sample.Callchain.Length; ++frameIndex)
        {
            ulong frame = sample.Callchain[frameIndex];
            if (PerfFormat.IsContextMarker(frame))
            {
                continue;
            }

            this.frameScratch[frameCount] = unchecked((long)frame);
            ++frameCount;
        }

        return frameCount;
    }

    private void NoteSampleTime(ref PerfSample sample)
    {
        if (!sample.HasTime)
        {
            return;
        }

        this.anySampleHadTime = true;
        if (sample.Time < this.minimumSampleTime)
        {
            this.minimumSampleTime = sample.Time;
        }
    }

    // Replaces each address with [inlined frames..., the address itself],
    // leaf-first. Memoized per address: a real capture has millions of samples
    // over a few thousand distinct addresses, and reading DWARF per sample
    // would dominate everything else.
    private ReadOnlySpan<long> ExpandInlineFrames(int processId, ReadOnlySpan<long> frames)
    {
        if (this.inlineSymbolLookup == null)
        {
            return frames;
        }

        int written = 0;

        for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
        {
            long[] expanded = this.ExpansionFor(processId, frames[frameIndex]);

            if (written + expanded.Length > this.expansionScratch.Length)
            {
                this.expansionScratch = new long[(written + expanded.Length) * 2];
            }

            for (int expandedIndex = 0; expandedIndex < expanded.Length; ++expandedIndex)
            {
                this.expansionScratch[written] = expanded[expandedIndex];
                ++written;
            }
        }

        return new ReadOnlySpan<long>(this.expansionScratch, 0, written);
    }

    private long[] ExpansionFor(int processId, long address)
    {
        long[] cached;
        if (this.expandedFramesByAddress.TryGetValue(address, out cached))
        {
            return cached;
        }

        long[] result = this.ComputeExpansion(processId, address);
        this.expandedFramesByAddress[address] = result;
        return result;
    }

    private long[] ComputeExpansion(int processId, long address)
    {
        long[] justTheAddress = new long[] { address };

        string modulePath;
        string moduleKey;
        long bias;
        if (!this.inlineSymbolLookup.TryGetUnwindContext(processId, address, out modulePath, out moduleKey, out bias))
        {
            return justTheAddress;
        }

        DebugInfoIndex index;
        if (!this.inlineIndexByModule.TryGetValue(moduleKey, out index))
        {
            string sectionError;
            ElfSections sections = ElfSections.TryLoad(modulePath, out sectionError);

            string indexError;
            index = sections != null ? DebugInfoIndex.TryLoad(sections, out indexError) : null;
            this.inlineIndexByModule[moduleKey] = index;
        }

        if (index == null)
        {
            return justTheAddress;
        }

        int recovered = index.GetInlineChain(address + bias, this.inlineChainScratch);
        if (recovered == 0)
        {
            return justTheAddress;
        }

        long[] expanded = new long[recovered + 1];
        for (int chainIndex = 0; chainIndex < recovered; ++chainIndex)
        {
            expanded[chainIndex] = this.SyntheticAddressFor(this.inlineChainScratch[chainIndex]);
        }

        expanded[recovered] = address;
        ++this.AddressesWithInlinedFrames;
        return expanded;
    }

    private long SyntheticAddressFor(string inlineName)
    {
        long existing;
        if (this.syntheticAddressByInlineName.TryGetValue(inlineName, out existing))
        {
            return existing;
        }

        long assigned = SyntheticInlineAddressBase + (this.syntheticAddressByInlineName.Count * SyntheticInlineAddressStride);
        this.syntheticAddressByInlineName[inlineName] = assigned;
        return assigned;
    }

    private void AddSample(ref PerfSample sample, ReadOnlySpan<long> frames)
    {
        int stackIndex = this.stacks.GetOrAdd(this.ExpandInlineFrames((int)sample.ProcessId, frames));

        if (sample.HasTime)
        {
            this.anySampleHadTime = true;
            if (sample.Time < this.minimumSampleTime)
            {
                this.minimumSampleTime = sample.Time;
            }
        }

        // Absolute for now; rebased once the whole file has been read and the
        // earliest timestamp is known. Records are near-ordered but nothing in
        // the format guarantees it, so the minimum cannot be taken from the
        // first sample seen.
        double absoluteMSec = sample.Time / NanosecondsPerMillisecond;

        // ThreadSampleType.Unknown, deliberately. That enum is the CLR's own
        // answer to "was this thread running managed code", and a perf capture
        // has no equivalent: cpu-clock samples only threads that are ON CPU, so
        // every sample present is by definition running. Claiming Managed here
        // would feed the Threading view's parked/blocked classification a
        // signal calibrated against a completely different measurement.
        this.samples.Add(new SampleEvent(absoluteMSec, sample.ThreadId, stackIndex, ThreadSampleType.Unknown));
    }

    private void ObserveTracepoint(PerfEventAttr attr, ref PerfSample sample)
    {
        if (!this.tracepointsInitialized)
        {
            this.tracepointsInitialized = true;

            if (this.attachedFile != null && this.attachedFile.TraceFormats != null)
            {
                this.Tracepoints = new TracepointProjector(this.attachedFile.TraceFormats);
            }
        }

        if (this.Tracepoints == null || !sample.HasRaw)
        {
            return;
        }

        this.NoteSampleTime(ref sample);

        // The stack still goes into the shared StackTable, because an off-CPU
        // interval and a lock wait are only useful attributed to the code that
        // caused them - and keying them the same way CPU samples are keyed is
        // what lets one caller-tree renderer show all three.
        int stackIndex = StackTable.EmptyStackIndex;
        if (sample.HasCallchain && sample.Callchain.Length > 0)
        {
            int frameCount = this.FilterCallchain(ref sample);
            if (frameCount > 0)
            {
                stackIndex = this.stacks.GetOrAdd(new ReadOnlySpan<long>(this.frameScratch, 0, frameCount));
            }
        }

        // For a tracepoint event, attr.Config IS the ftrace event id the
        // format table is keyed by.
        this.Tracepoints.Observe((int)attr.Config, sample.Time / NanosecondsPerMillisecond, sample.ThreadId, stackIndex, sample.Raw);
    }

    public void OnMapping(in PerfMapping mapping)
    {
        this.mappings.Add(mapping);
    }

    public void OnComm(int processId, int threadId, string comm)
    {
        this.commByThreadId[threadId] = comm;
        this.processIdByThreadId[threadId] = processId;
    }

    public void OnFork(int processId, int parentProcessId, int threadId, int parentThreadId, ulong time)
    {
        this.processIdByThreadId[threadId] = processId;

        // A forked thread inherits its parent's name until it sets its own.
        // Without this, every thread that never calls prctl renders as "?"
        // even though the capture knows what it is.
        string parentName;
        if (!this.commByThreadId.ContainsKey(threadId) && this.commByThreadId.TryGetValue(parentThreadId, out parentName))
        {
            this.commByThreadId[threadId] = parentName;
        }
    }

    public void OnExit(int processId, int threadId, ulong time)
    {
    }

    ////////////////////////////////////////////////////////////////////////////
    // Post-read
    ////////////////////////////////////////////////////////////////////////////

    // Rebases every sample onto capture-relative time. Done by overwriting the
    // list in place rather than by buffering raw timestamps in a parallel list
    // during the walk: at 10M+ samples that parallel list is hundreds of MB,
    // for a value that is only ever needed as a difference.
    public void RebaseTimestamps()
    {
        if (!this.anySampleHadTime || this.samples.Count == 0)
        {
            return;
        }

        double baseMSec = this.minimumSampleTime / NanosecondsPerMillisecond;

        Span<SampleEvent> span = CollectionsMarshal.AsSpan(this.samples);
        for (int sampleIndex = 0; sampleIndex < span.Length; ++sampleIndex)
        {
            ref SampleEvent existing = ref span[sampleIndex];
            span[sampleIndex] = new SampleEvent(existing.RelativeMSec - baseMSec, existing.ThreadId, existing.StackIndex, existing.SampleType);
        }

        // The tracepoint projection carries the same absolute clock and has to
        // move with it, or a contention event's timestamp lands nowhere near
        // the CPU samples it should line up against on a shared timeline.
        if (this.Tracepoints != null)
        {
            List<ContentionEvent> contention = this.Tracepoints.Projection.ContentionEvents;
            for (int eventIndex = 0; eventIndex < contention.Count; ++eventIndex)
            {
                ContentionEvent existing = contention[eventIndex];
                contention[eventIndex] = new ContentionEvent(
                    existing.RelativeMSec - baseMSec,
                    existing.DurationMSec,
                    existing.ContentionFlags,
                    existing.ThreadId,
                    existing.StackIndex,
                    existing.LockId,
                    existing.AssociatedObjectId,
                    existing.OwnerThreadId);
            }

            Span<OffCpuInterval> intervals = CollectionsMarshal.AsSpan(this.Tracepoints.Projection.OffCpuIntervals);
            for (int intervalIndex = 0; intervalIndex < intervals.Length; ++intervalIndex)
            {
                intervals[intervalIndex].StartMSec -= baseMSec;
            }
        }
    }

    public KallsymsTable KernelSymbols { get; set; }

    // Symbolization harvested from a companion `perf script` text file - the
    // names perf resolved ON THE RECORDING HOST, where the binaries actually
    // are. Takes precedence over local ELF resolution, because it cannot be
    // beaten: the host had the real modules, the real /proc/<pid>/maps and the
    // real /proc/kallsyms. See PerfScript/PerfScriptSymbolOracle.cs.
    public PerfScriptSymbolOracle HostSymbols { get; set; }

    public void BuildSymbols(PerfDataFile file, string extraSymbolPath)
    {
        List<string> searchRoots = PerfSymbolTable.BuildSearchRoots(extraSymbolPath);

        V6ThreadTable threadTable = new V6ThreadTable();
        List<EventRecord> syntheticEvents = new List<EventRecord>();

        // Module identity is the build id where there is one, and the path
        // otherwise - so two mappings of the same module (a shared object is
        // mapped in several segments, and in every process that loads it) share
        // one metadata id and one loaded symbol file.
        Dictionary<string, long> metadataIdByModuleKey = new Dictionary<string, long>(StringComparer.Ordinal);
        Dictionary<long, string> modulePathByMetadataId = new Dictionary<long, string>();
        long nextMetadataId = 1;

        HashSet<int> definedProcessIds = new HashSet<int>();

        for (int mappingIndex = 0; mappingIndex < this.mappings.Count; ++mappingIndex)
        {
            PerfMapping mapping = this.mappings[mappingIndex];

            if (!mapping.IsExecutable || mapping.FileName.Length == 0)
            {
                continue;
            }

            string buildId = file.BuildIdForModule(mapping.FileName, mapping.BuildId);
            string moduleKey = buildId.Length > 0 ? buildId : mapping.FileName;

            long metadataId;
            if (!metadataIdByModuleKey.TryGetValue(moduleKey, out metadataId))
            {
                metadataId = nextMetadataId;
                ++nextMetadataId;
                metadataIdByModuleKey[moduleKey] = metadataId;

                string modulePath = PerfSymbolTable.FindModuleFile(searchRoots, buildId, mapping.FileName);
                if (modulePath != null)
                {
                    modulePathByMetadataId[metadataId] = modulePath;
                }

                syntheticEvents.Add(CreateMappingMetadataEvent(metadataId, mapping.FileName, buildId, modulePath));
            }

            // The mapping's own owning process. UniversalSymbolTable reads the
            // process id from the thread table via the event's ThreadId, so a
            // synthetic thread standing for the process is enough.
            if (definedProcessIds.Add(mapping.ProcessId))
            {
                threadTable.Define((ulong)mapping.ProcessId, mapping.ProcessId, mapping.ProcessId, string.Empty);
            }

            syntheticEvents.Add(CreateMappingEvent(mapping, metadataId));
        }

        // Kernel symbols enter the same way the mappings did: as synthesized
        // ProcessSymbol rows, which is exactly the shape UniversalSymbolTable
        // already consumes for a v6 capture's own in-capture symbols. Only the
        // TEXT symbols are added and only when a table was supplied, so a
        // capture without one is unchanged.
        //
        // This is the one place a synthesized-event bridge is genuinely
        // expensive - a real kernel has 100k+ symbols and each row costs a
        // small dictionary - so it is done once, and only for the kernel,
        // where the payoff is that every off-CPU stack becomes readable
        // instead of a column of raw addresses.
        if (this.KernelSymbols != null && !this.KernelSymbols.IsEmpty)
        {
            this.KernelSymbols.AppendProcessSymbolEvents(syntheticEvents);
        }

        // The inlined functions, as one synthetic module plus a symbol per
        // distinct name. Without the mapping the symbol resolves but the frame
        // is reported as belonging to no module, which the CPU category
        // classifier reads as unresolved.
        if (this.syntheticAddressByInlineName.Count > 0)
        {
            long inlineMetadataId = nextMetadataId;
            ++nextMetadataId;

            long inlineRangeEnd = SyntheticInlineAddressBase + ((this.syntheticAddressByInlineName.Count + 1) * SyntheticInlineAddressStride);

            PerfMapping inlineMapping = new PerfMapping();
            inlineMapping.ProcessId = 0;
            inlineMapping.StartAddress = SyntheticInlineAddressBase;
            inlineMapping.Length = inlineRangeEnd - SyntheticInlineAddressBase;
            inlineMapping.FileOffset = 0;
            inlineMapping.FileName = "[inlined]";
            inlineMapping.BuildId = string.Empty;
            inlineMapping.IsExecutable = true;

            if (definedProcessIds.Add(0))
            {
                threadTable.Define(0, 0, 0, string.Empty);
            }

            syntheticEvents.Add(CreateMappingEvent(inlineMapping, inlineMetadataId));
            syntheticEvents.Add(CreateMappingMetadataEvent(inlineMetadataId, "[inlined]", string.Empty, null));

            foreach (KeyValuePair<string, long> entry in this.syntheticAddressByInlineName)
            {
                Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
                fields["StartAddress"] = entry.Value;
                fields["EndAddress"] = entry.Value + SyntheticInlineAddressStride;
                fields["Name"] = entry.Key;

                syntheticEvents.Add(new EventRecord(
                    V6Format.UniversalSystemProviderName,
                    "ProcessSymbol",
                    0,
                    1,
                    0,
                    0,
                    StackTable.EmptyStackIndex,
                    fields,
                    null,
                    0,
                    0));
            }
        }

        // Added BEFORE the table is built so they participate in the same
        // sorted lookup as everything else. Their one-address ranges sit
        // inside the module ranges above; UniversalSymbolTable resolves a
        // symbol before falling back to module+offset, so a harvested name
        // wins wherever it exists and local resolution covers the rest.
        if (this.HostSymbols != null && !this.HostSymbols.IsEmpty)
        {
            this.HostSymbols.AppendProcessSymbolEvents(syntheticEvents);
        }

        UniversalSymbolTable nativeSymbols = UniversalSymbolTable.Build(syntheticEvents, threadTable);

        foreach (KeyValuePair<long, string> module in modulePathByMetadataId)
        {
            string loadError;
            ElfSymbolFile symbols = ElfSymbolFile.TryLoad(module.Value, out loadError);

            if (symbols == null)
            {
                ++this.ModulesWithoutSymbols;
                this.UnresolvedModules.Add(module.Value + "  " + (loadError ?? "unreadable"));
                continue;
            }

            nativeSymbols.AddModuleSymbols(module.Key, symbols);
            ++this.ModulesWithSymbols;
        }

        this.ModulesWithoutSymbols += metadataIdByModuleKey.Count - modulePathByMetadataId.Count;

        // An EMPTY method symbol table, which is the correct one: a native
        // capture contains no managed method load events, so every address
        // resolves through the native path. That path also interns by NAME, so
        // the many distinct addresses inside one function collapse to one row -
        // which is exactly the merge a native profile needs.
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), 8, 1000000000, 0);
        symbolTable.SetNativeSymbols(nativeSymbols);

        this.SymbolTable = symbolTable;
        this.NativeSymbols = nativeSymbols;
    }

    private static EventRecord CreateMappingEvent(in PerfMapping mapping, long metadataId)
    {
        Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
        fields["StartAddress"] = mapping.StartAddress;
        fields["EndAddress"] = mapping.StartAddress + mapping.Length;
        fields["FileName"] = mapping.FileName;
        fields["FileOffset"] = mapping.FileOffset;
        fields["MetadataId"] = metadataId;

        return new EventRecord(
            V6Format.UniversalSystemProviderName,
            "ProcessMapping",
            0,
            1,
            0,
            mapping.ProcessId,
            StackTable.EmptyStackIndex,
            fields,
            null,
            0,
            0);
    }

    private static EventRecord CreateMappingMetadataEvent(long metadataId, string fileName, string buildId, string modulePath)
    {
        // p_vaddr/p_offset come from the module FILE, because a perf capture
        // does not record them - and without them an address translates into
        // the wrong function rather than into none. See
        // Symbols/ElfLoadSegments.cs for the formula and the ground-truth
        // measurement behind it.
        long programHeaderVirtualAddress = 0;
        long programHeaderFileOffset = 0;

        if (modulePath != null)
        {
            string segmentError;
            ElfLoadSegments segments = ElfLoadSegments.TryLoad(modulePath, out segmentError);

            if (segments != null)
            {
                segments.TryGetExecutableSegment(out programHeaderFileOffset, out programHeaderVirtualAddress);
            }
        }

        StringBuilder symbolMetadata = new StringBuilder();
        symbolMetadata.Append("{\"build_id\":\"");
        symbolMetadata.Append(buildId);
        symbolMetadata.Append("\",\"p_vaddr\":\"0x");
        symbolMetadata.Append(programHeaderVirtualAddress.ToString("x"));
        symbolMetadata.Append("\",\"p_offset\":\"0x");
        symbolMetadata.Append(programHeaderFileOffset.ToString("x"));
        symbolMetadata.Append("\"}");

        Dictionary<string, object> fields = new Dictionary<string, object>(StringComparer.Ordinal);
        fields["Id"] = metadataId;
        fields["FileName"] = fileName;
        fields["SymbolMetadata"] = symbolMetadata.ToString();

        return new EventRecord(
            V6Format.UniversalSystemProviderName,
            "ProcessMappingMetadata",
            0,
            1,
            0,
            0,
            StackTable.EmptyStackIndex,
            fields,
            null,
            0,
            0);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Analysis)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
