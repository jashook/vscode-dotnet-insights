////////////////////////////////////////////////////////////////////////////////
// Module: PerfScriptAnalysis.cs
//
// Notes:
// Turns `perf script` text into the same inputs the binary path produces, so
// the CPU view, its caller trees, the flame graph and the category breakdown
// are literally the same code for both.
//
// The one structural difference is where a frame's identity comes from. The
// binary path has addresses and resolves them; the text path has NAMES that
// perf already resolved on the recording host, and instruction pointers that
// mean nothing without mappings the text does not carry. So every distinct name
// is assigned a SYNTHETIC address and registered as a symbol covering it -
// exactly the mechanism inline frames use - and from there nothing downstream
// can tell the two paths apart.
//
// Names go through the demangler even though perf demangles as it prints:
// perf uses the Itanium demangler, which handles C++ and declines Rust v0
// entirely, so a Rust capture arrives with every frame mangled. For a C++
// capture (Envoy, say) the pass is a no-op.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Analysis {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Rundown;
using DotnetInsights.NetTrace.Symbols;
using DotnetInsights.NetTrace.Universal;
using DotnetInsights.NetTrace.V6;
using DotnetInsights.Perf.PerfScript;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class PerfScriptAnalysis : IPerfScriptSink
{
    // Above the 47-bit user address space on x86-64 and AArch64 and below the
    // kernel half, so a synthetic frame address can never be confused with a
    // real one. Same reasoning as the inline-frame synthetic range.
    private const long SyntheticAddressBase = 1L << 48;
    private const long SyntheticAddressStride = 16;

    private readonly StackTable stacks = new StackTable();
    private readonly List<SampleEvent> samples = new List<SampleEvent>();

    private readonly Dictionary<string, long> addressByFrameName = new Dictionary<string, long>(StringComparer.Ordinal);
    private readonly List<string> frameNamesInOrder = new List<string>();

    private readonly Dictionary<long, string> commByThreadId = new Dictionary<long, string>();
    private readonly Dictionary<string, long> samplesByEventName = new Dictionary<string, long>(StringComparer.Ordinal);

    private long[] frameScratch = new long[256];

    private double minimumTimeSeconds = double.MaxValue;
    private double maximumTimeSeconds = double.MinValue;

    public StackTable Stacks => this.stacks;

    public List<SampleEvent> Samples => this.samples;

    public IReadOnlyDictionary<long, string> CommByThreadId => this.commByThreadId;

    public IReadOnlyDictionary<string, long> SamplesByEventName => this.samplesByEventName;

    public int DistinctFrameNameCount => this.frameNamesInOrder.Count;

    public MethodSymbolTable SymbolTable { get; private set; }

    public UniversalSymbolTable NativeSymbols { get; private set; }

    public double CaptureDurationSeconds =>
        this.maximumTimeSeconds > this.minimumTimeSeconds ? this.maximumTimeSeconds - this.minimumTimeSeconds : 0;

    public void OnScriptSample(string comm, long threadId, double timeSeconds, string eventName, IReadOnlyList<string> frames)
    {
        if (eventName != null)
        {
            long existingForEvent;
            this.samplesByEventName.TryGetValue(eventName, out existingForEvent);
            this.samplesByEventName[eventName] = existingForEvent + 1;
        }

        if (frames.Count == 0)
        {
            return;
        }

        if (!string.IsNullOrEmpty(comm))
        {
            this.commByThreadId[threadId] = comm;
        }

        if (timeSeconds < this.minimumTimeSeconds)
        {
            this.minimumTimeSeconds = timeSeconds;
        }

        if (timeSeconds > this.maximumTimeSeconds)
        {
            this.maximumTimeSeconds = timeSeconds;
        }

        if (this.frameScratch.Length < frames.Count)
        {
            this.frameScratch = new long[frames.Count * 2];
        }

        for (int frameIndex = 0; frameIndex < frames.Count; ++frameIndex)
        {
            this.frameScratch[frameIndex] = this.AddressForFrameName(frames[frameIndex]);
        }

        int stackIndex = this.stacks.GetOrAdd(new ReadOnlySpan<long>(this.frameScratch, 0, frames.Count));

        // Absolute for now; rebased once the whole file has been read, exactly
        // as the binary path does. `perf script` prints seconds since boot.
        this.samples.Add(new SampleEvent(timeSeconds * 1000.0, threadId, stackIndex, ThreadSampleType.Unknown));
    }

    private long AddressForFrameName(string frameName)
    {
        long existing;
        if (this.addressByFrameName.TryGetValue(frameName, out existing))
        {
            return existing;
        }

        long assigned = SyntheticAddressBase + (this.frameNamesInOrder.Count * SyntheticAddressStride);
        this.addressByFrameName[frameName] = assigned;
        this.frameNamesInOrder.Add(frameName);
        return assigned;
    }

    public void RebaseTimestamps()
    {
        if (this.samples.Count == 0 || this.minimumTimeSeconds == double.MaxValue)
        {
            return;
        }

        double baseMSec = this.minimumTimeSeconds * 1000.0;

        Span<SampleEvent> span = CollectionsMarshal.AsSpan(this.samples);
        for (int sampleIndex = 0; sampleIndex < span.Length; ++sampleIndex)
        {
            ref SampleEvent existing = ref span[sampleIndex];
            span[sampleIndex] = new SampleEvent(existing.RelativeMSec - baseMSec, existing.ThreadId, existing.StackIndex, existing.SampleType);
        }
    }

    public void BuildSymbols()
    {
        V6ThreadTable threadTable = new V6ThreadTable();
        threadTable.Define(0, 0, 0, string.Empty);

        List<EventRecord> syntheticEvents = new List<EventRecord>();

        long metadataId = 1;
        long rangeEnd = SyntheticAddressBase + ((this.frameNamesInOrder.Count + 1) * SyntheticAddressStride);

        Dictionary<string, object> mappingFields = new Dictionary<string, object>(StringComparer.Ordinal);
        mappingFields["StartAddress"] = SyntheticAddressBase;
        mappingFields["EndAddress"] = rangeEnd;
        mappingFields["FileName"] = "[perf script]";
        mappingFields["FileOffset"] = 0L;
        mappingFields["MetadataId"] = metadataId;

        syntheticEvents.Add(new EventRecord(
            V6Format.UniversalSystemProviderName, "ProcessMapping", 0, 1, 0, 0,
            StackTable.EmptyStackIndex, mappingFields, null, 0, 0));

        for (int nameIndex = 0; nameIndex < this.frameNamesInOrder.Count; ++nameIndex)
        {
            long address = SyntheticAddressBase + (nameIndex * SyntheticAddressStride);

            Dictionary<string, object> symbolFields = new Dictionary<string, object>(StringComparer.Ordinal);
            symbolFields["StartAddress"] = address;
            symbolFields["EndAddress"] = address + SyntheticAddressStride;

            // Demangled here rather than at parse time so the name used as the
            // dictionary key stays exactly what perf printed - two mangled
            // names that demangle to the same string are still two symbols.
            symbolFields["Name"] = NativeSymbolDemangler.Demangle(this.frameNamesInOrder[nameIndex]);

            syntheticEvents.Add(new EventRecord(
                V6Format.UniversalSystemProviderName, "ProcessSymbol", 0, 1, 0, 0,
                StackTable.EmptyStackIndex, symbolFields, null, 0, 0));
        }

        UniversalSymbolTable nativeSymbols = UniversalSymbolTable.Build(syntheticEvents, threadTable);

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord>(), 8, 1000000000, 0);
        symbolTable.SetNativeSymbols(nativeSymbols);

        this.SymbolTable = symbolTable;
        this.NativeSymbols = nativeSymbols;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Analysis)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
