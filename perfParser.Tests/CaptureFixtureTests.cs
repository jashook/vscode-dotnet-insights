////////////////////////////////////////////////////////////////////////////////
// Module: CaptureFixtureTests.cs
//
// Notes:
// End-to-end checks against a real perf.data, gated on an environment variable
// exactly like nettraceParser.Tests' own fixture-gated tests. With the variable
// unset - the default, including in CI - each of these is a silent no-op rather
// than a failure or a skip: there is no fixture to read.
//
//   PERF_FIXTURE=~/projects/Investigations/perf-fixtures/rust-cpu.perf.data \
//   PERF_SYMBOL_PATH=~/projects/Investigations/perf-fixtures/symbols/.build-id \
//     dotnet test --filter CaptureFixtureTests
//
// These assert STRUCTURE and INVARIANTS rather than pinned counts, following
// the same reasoning as CoreDumpHeapGraphBuilderTests: a count changes with
// every re-capture, while "every frame resolves" and "no record was malformed"
// hold for any capture of the same shape and are what actually break when a
// decoding rule regresses.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Tests {

using System;
using System.Collections.Generic;
using System.IO;

using Xunit;

using DotnetInsights.Perf.Analysis;
using DotnetInsights.Perf.PerfData;
using DotnetInsights.Perf.Symbols;

public class CaptureFixtureTests
{
    private const string FixtureEnvironmentVariable = "PERF_FIXTURE";
    private const string SymbolPathEnvironmentVariable = "PERF_SYMBOL_PATH";

    private static string FixturePath()
    {
        string path = Environment.GetEnvironmentVariable(FixtureEnvironmentVariable);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        path = path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return File.Exists(path) ? path : null;
    }

    private static string SymbolPath()
    {
        string path = Environment.GetEnvironmentVariable(SymbolPathEnvironmentVariable);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private sealed class CountingSink : IPerfRecordSink
    {
        public long SampleCount;
        public long SamplesWithCallchain;
        public long ContextMarkerFrames;

        // Mappings are PER PROCESS, so resolving an address needs the pid the
        // sample came from - resolving against pid 0 finds no mappings at all
        // and reports every frame as unresolved.
        public int PrimaryProcessId;
        public readonly List<PerfMapping> Mappings = new List<PerfMapping>();

        public void OnSample(PerfEventAttr attr, ref PerfSample sample)
        {
            ++this.SampleCount;

            if (this.PrimaryProcessId == 0)
            {
                this.PrimaryProcessId = (int)sample.ProcessId;
            }

            if (!sample.HasCallchain)
            {
                return;
            }

            ++this.SamplesWithCallchain;

            for (int frameIndex = 0; frameIndex < sample.Callchain.Length; ++frameIndex)
            {
                if (PerfFormat.IsContextMarker(sample.Callchain[frameIndex]))
                {
                    ++this.ContextMarkerFrames;
                }
            }
        }

        public void OnMapping(in PerfMapping mapping)
        {
            this.Mappings.Add(mapping);
        }

        public void OnComm(int processId, int threadId, string comm)
        {
        }

        public void OnFork(int processId, int parentProcessId, int threadId, int parentThreadId, ulong time)
        {
        }

        public void OnExit(int processId, int threadId, ulong time)
        {
        }
    }

    // A clean read of a real capture: every record accounted for, none
    // malformed, no record type unrecognised. A nonzero malformed count means
    // some layout assumption is wrong even though the walk completed.
    [Fact]
    public void Read_RealCapture_HasNoMalformedOrUnknownRecords()
    {
        string fixturePath = FixturePath();
        if (fixturePath == null)
        {
            return;
        }

        CountingSink sink = new CountingSink();
        PerfDataFile file = new PerfDataFile();
        PerfReadResult result = file.Read(fixturePath, sink);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, file.MalformedRecordCount);
        Assert.Equal(0, file.UnknownRecordCount);
        Assert.True(sink.SampleCount > 0, "capture contained no samples");
        Assert.Equal(sink.SampleCount, file.SampleRecordCount);
    }

    // Build ids are 20-byte SHA-1s: 40 hex characters. 42 is the signature of
    // reading the build_id_event's trailing `size` byte as part of the id,
    // which yields something that looks like a build id and matches nothing on
    // any symbol server.
    [Fact]
    public void Read_RealCapture_BuildIdsAreFortyHexCharacters()
    {
        string fixturePath = FixturePath();
        if (fixturePath == null)
        {
            return;
        }

        CountingSink sink = new CountingSink();
        PerfDataFile file = new PerfDataFile();
        Assert.True(file.Read(fixturePath, sink).Success);

        Assert.NotEmpty(file.BuildIdsByFileName);

        foreach (KeyValuePair<string, string> entry in file.BuildIdsByFileName)
        {
            Assert.Equal(40, entry.Value.Length);

            foreach (char character in entry.Value)
            {
                Assert.True(Uri.IsHexDigit(character), entry.Key + " has a non-hex build id: " + entry.Value);
            }
        }
    }

    // Context markers are present in a real callchain and must never reach the
    // StackTable. This asserts BOTH halves: the raw chains contain them, and
    // the analysis strips them.
    [Fact]
    public void Analysis_StripsContextMarkersFromEveryStack()
    {
        string fixturePath = FixturePath();
        if (fixturePath == null)
        {
            return;
        }

        CountingSink rawSink = new CountingSink();
        PerfDataFile rawFile = new PerfDataFile();
        Assert.True(rawFile.Read(fixturePath, rawSink).Success);

        // If a capture had none at all the second half of this test would pass
        // vacuously.
        Assert.True(rawSink.ContextMarkerFrames > 0, "capture had no context markers to strip");

        PerfCaptureAnalysis analysis = new PerfCaptureAnalysis();
        PerfDataFile file = new PerfDataFile();
        Assert.True(file.Read(fixturePath, analysis).Success);

        for (int stackIndex = 0; stackIndex < analysis.Stacks.Count; ++stackIndex)
        {
            long[] frames = analysis.Stacks.FramesAt(stackIndex);
            for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
            {
                Assert.False(
                    PerfFormat.IsContextMarker(unchecked((ulong)frames[frameIndex])),
                    "a context marker reached the stack table");
            }
        }
    }

    // Every sample must reference a stack that exists, and no sample may carry
    // the empty stack - a CPU sample with no frames at all means the callchain
    // was dropped somewhere between decode and the table.
    [Fact]
    public void Analysis_EverySampleReferencesARealStack()
    {
        string fixturePath = FixturePath();
        if (fixturePath == null)
        {
            return;
        }

        PerfCaptureAnalysis analysis = new PerfCaptureAnalysis();
        PerfDataFile file = new PerfDataFile();
        Assert.True(file.Read(fixturePath, analysis).Success);

        if (analysis.Samples.Count == 0)
        {
            // A tracepoint-only capture has no CPU samples; nothing to assert.
            return;
        }

        for (int sampleIndex = 0; sampleIndex < analysis.Samples.Count; ++sampleIndex)
        {
            int stackIndex = analysis.Samples[sampleIndex].StackIndex;

            Assert.InRange(stackIndex, 0, analysis.Stacks.Count - 1);
            Assert.NotEmpty(analysis.Stacks.FramesAt(stackIndex));
        }
    }

    // Timestamps must be capture-relative and monotonic in aggregate: a
    // negative one means the rebase used a minimum that was not the real
    // minimum, which happens if only some sample kinds were considered.
    [Fact]
    public void Analysis_RebasedTimestampsAreNonNegative()
    {
        string fixturePath = FixturePath();
        if (fixturePath == null)
        {
            return;
        }

        PerfCaptureAnalysis analysis = new PerfCaptureAnalysis();
        PerfDataFile file = new PerfDataFile();
        analysis.AttachFile(file);
        Assert.True(file.Read(fixturePath, analysis).Success);

        analysis.RebaseTimestamps();

        for (int sampleIndex = 0; sampleIndex < analysis.Samples.Count; ++sampleIndex)
        {
            Assert.True(analysis.Samples[sampleIndex].RelativeMSec >= 0, "a sample rebased to a negative time");
        }

        if (analysis.Tracepoints != null)
        {
            List<DotnetInsights.NetTrace.Contention.ContentionEvent> contention = analysis.Tracepoints.Projection.ContentionEvents;
            for (int eventIndex = 0; eventIndex < contention.Count; ++eventIndex)
            {
                Assert.True(contention[eventIndex].RelativeMSec >= 0, "a contention event rebased to a negative time");
                Assert.True(contention[eventIndex].DurationMSec > 0, "a contention event has a non-positive duration");
            }
        }
    }

    // With symbols available, a native capture should resolve essentially
    // everything in the main binary. This is deliberately a THRESHOLD, not an
    // exact figure: some frames genuinely have no symbol (PLT stubs, the vDSO)
    // and a capture recorded without a symbol bundle legitimately resolves
    // none, which is why the test only runs when one was named.
    [Fact]
    public void Symbolization_ResolvesTheOverwhelmingMajorityOfFrames()
    {
        string fixturePath = FixturePath();
        string symbolPath = SymbolPath();
        if (fixturePath == null || symbolPath == null || !Directory.Exists(symbolPath))
        {
            return;
        }

        CountingSink sink = new CountingSink();
        PerfDataFile file = new PerfDataFile();
        Assert.True(file.Read(fixturePath, sink).Success);

        PerfSymbolTable symbols = new PerfSymbolTable(symbolPath);
        for (int mappingIndex = 0; mappingIndex < sink.Mappings.Count; ++mappingIndex)
        {
            PerfMapping mapping = sink.Mappings[mappingIndex];
            symbols.AddMapping(in mapping, file.BuildIdForModule(mapping.FileName, mapping.BuildId));
        }

        symbols.FinishBuilding();

        PerfCaptureAnalysis analysis = new PerfCaptureAnalysis();
        PerfDataFile analysisFile = new PerfDataFile();
        Assert.True(analysisFile.Read(fixturePath, analysis).Success);

        if (analysis.Samples.Count == 0)
        {
            return;
        }

        int resolved = 0;
        int total = 0;

        for (int sampleIndex = 0; sampleIndex < analysis.Samples.Count && sampleIndex < 2000; ++sampleIndex)
        {
            long[] frames = analysis.Stacks.FramesAt(analysis.Samples[sampleIndex].StackIndex);
            if (frames.Length == 0)
            {
                continue;
            }

            ++total;

            string leafName = symbols.Resolve(sink.PrimaryProcessId, frames[0]);
            if (!leafName.StartsWith("[unknown]", StringComparison.Ordinal))
            {
                ++resolved;
            }
        }

        Assert.True(total > 0);
        Assert.True(resolved * 100 / total >= 90, "only " + resolved + " of " + total + " leaf frames resolved to a module");
    }
}


public class InlineExpansionFixtureTests
{
    private const string FixtureEnvironmentVariable = "PERF_FIXTURE";
    private const string SymbolPathEnvironmentVariable = "PERF_SYMBOL_PATH";

    private static string Expand(string path)
    {
        return path == null ? null : path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    // Inline expansion must ADD frames without changing what was already there:
    // the same samples, the same leaf attribution per address, and strictly
    // more distinct methods. Comparing the two runs of the same capture is the
    // only way to assert that, since the correct number of inlined frames
    // depends entirely on how the module was optimized.
    [Fact]
    public void InlineExpansion_AddsFramesWithoutLosingSamples()
    {
        string fixturePath = Expand(Environment.GetEnvironmentVariable(FixtureEnvironmentVariable));
        string symbolPath = Expand(Environment.GetEnvironmentVariable(SymbolPathEnvironmentVariable));

        if (fixturePath == null || !File.Exists(fixturePath) || symbolPath == null || !Directory.Exists(symbolPath))
        {
            return;
        }

        PerfCaptureAnalysis withoutInlining = ReadCapture(fixturePath, null);
        if (withoutInlining.Samples.Count == 0)
        {
            return;
        }

        PerfSymbolTable symbols = BuildSymbolTable(fixturePath, symbolPath);
        PerfCaptureAnalysis withInlining = ReadCapture(fixturePath, symbols);

        // Same capture, same samples - expansion changes stacks, never counts.
        Assert.Equal(withoutInlining.Samples.Count, withInlining.Samples.Count);

        if (withInlining.AddressesWithInlinedFrames == 0)
        {
            // A module with no debug info contributes no inlined frames; that
            // is a legitimate outcome and the rest of this test is vacuous.
            return;
        }

        Assert.True(withInlining.DistinctInlinedFunctionCount > 0);

        // Every expanded stack must be at least as deep as its unexpanded
        // counterpart, and the physical frames must survive - an expansion that
        // REPLACED a frame rather than preceding it would still lengthen the
        // stack while losing the function the sample was actually in.
        for (int sampleIndex = 0; sampleIndex < withInlining.Samples.Count && sampleIndex < 500; ++sampleIndex)
        {
            long[] plain = withoutInlining.Stacks.FramesAt(withoutInlining.Samples[sampleIndex].StackIndex);
            long[] expanded = withInlining.Stacks.FramesAt(withInlining.Samples[sampleIndex].StackIndex);

            Assert.True(expanded.Length >= plain.Length);

            // Every original address still appears, in order.
            int plainIndex = 0;
            for (int expandedIndex = 0; expandedIndex < expanded.Length && plainIndex < plain.Length; ++expandedIndex)
            {
                if (expanded[expandedIndex] == plain[plainIndex])
                {
                    ++plainIndex;
                }
            }

            Assert.Equal(plain.Length, plainIndex);
        }
    }

    private static PerfSymbolTable BuildSymbolTable(string fixturePath, string symbolPath)
    {
        CountingMappingSink sink = new CountingMappingSink();
        PerfDataFile file = new PerfDataFile();
        file.Read(fixturePath, sink);

        PerfSymbolTable symbols = new PerfSymbolTable(symbolPath);
        for (int mappingIndex = 0; mappingIndex < sink.Mappings.Count; ++mappingIndex)
        {
            PerfMapping mapping = sink.Mappings[mappingIndex];
            symbols.AddMapping(in mapping, file.BuildIdForModule(mapping.FileName, mapping.BuildId));
        }

        symbols.FinishBuilding();
        return symbols;
    }

    private static PerfCaptureAnalysis ReadCapture(string fixturePath, PerfSymbolTable inlineSymbols)
    {
        PerfCaptureAnalysis analysis = new PerfCaptureAnalysis();
        if (inlineSymbols != null)
        {
            analysis.AttachInlineExpander(inlineSymbols);
        }

        PerfDataFile file = new PerfDataFile();
        file.Read(fixturePath, analysis);
        return analysis;
    }

    private sealed class CountingMappingSink : IPerfRecordSink
    {
        public readonly List<PerfMapping> Mappings = new List<PerfMapping>();

        public void OnSample(PerfEventAttr attr, ref PerfSample sample) { }
        public void OnMapping(in PerfMapping mapping) { this.Mappings.Add(mapping); }
        public void OnComm(int processId, int threadId, string comm) { }
        public void OnFork(int processId, int parentProcessId, int threadId, int parentThreadId, ulong time) { }
        public void OnExit(int processId, int threadId, ulong time) { }
    }
}

} // end of namespace(DotnetInsights.Perf.Tests)
