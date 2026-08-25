////////////////////////////////////////////////////////////////////////////////
// Module: Program.cs
//
// Notes:
// CLI entry point for perfParser. Mirrors nettraceParser's own shape: a plain
// mode that prints a human-readable summary of what is in the capture, and
// (later) a --json mode that writes the analysis the VS Code extension renders.
//
// The plain mode exists for the same reason nettraceParser's does - it is how
// a capture's contents get checked against the tool that wrote it (`perf
// report` / `perf script`) without a webview in the loop.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;

using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.Perf.Analysis;
using DotnetInsights.Perf.Dwarf;
using DotnetInsights.Perf.Symbols;
using DotnetInsights.Perf.Tracepoints;
using DotnetInsights.NetTrace.Contention;
using DotnetInsights.Perf.PerfData;
using DotnetInsights.Perf.PerfScript;
using DotnetInsights.NetTrace.Symbols;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: perfParser <perf.data> [--dump-records]");
    Console.Error.WriteLine("       perfParser --demangle-check <symbols.tsv>");
    Console.Error.WriteLine("       perfParser --demangle <mangled-name>");
    return 1;
}

// Diffs this project's Rust demangler against rustc's own, over a two-column
// mangled/demangled table produced by rustfilt (see
// testApps/perfWorkload/capture.sh). The reference implementation is the only
// thing worth checking a demangler against - a demangler tested against its
// author's reading of the grammar agrees with the reading, not the compiler.
int demangleCheckIndex = Array.IndexOf(args, "--demangle-check");
if (demangleCheckIndex >= 0)
{
    if (demangleCheckIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--demangle-check requires a path to a tab-separated mangled/demangled table");
        return 1;
    }

    return RunDemangleCheck(args[demangleCheckIndex + 1]);
}

int demangleIndex = Array.IndexOf(args, "--demangle");
if (demangleIndex >= 0 && demangleIndex + 1 < args.Length)
{
    Console.WriteLine(RustDemangler.Demangle(args[demangleIndex + 1]));
    return 0;
}

string capturePath = args[0];
bool dumpRecords = Array.IndexOf(args, "--dump-records") >= 0;

string symbolPath = null;
int symbolPathIndex = Array.IndexOf(args, "--symbol-path");
if (symbolPathIndex >= 0 && symbolPathIndex + 1 < args.Length)
{
    symbolPath = args[symbolPathIndex + 1];
}

// A companion `perf script` text file, used as the symbol oracle for a
// perf.data. Auto-detected beside the capture so the common case - both files
// downloaded together - needs no flag.
string hostSymbolsPath = null;
int hostSymbolsIndex = Array.IndexOf(args, "--symbols-from");
if (hostSymbolsIndex >= 0 && hostSymbolsIndex + 1 < args.Length)
{
    hostSymbolsPath = args[hostSymbolsIndex + 1];
}

string kallsymsPath = null;
int kallsymsPathIndex = Array.IndexOf(args, "--kallsyms");
if (kallsymsPathIndex >= 0 && kallsymsPathIndex + 1 < args.Length)
{
    kallsymsPath = args[kallsymsPathIndex + 1];
}
else
{
    // Saved beside the capture by collect-perf.sh, because it can only come
    // from the recording machine and only matches captures from that boot.
    string captureDirectory = System.IO.Path.GetDirectoryName(capturePath);
    if (string.IsNullOrEmpty(captureDirectory))
    {
        captureDirectory = ".";
    }

    string captureFileName = System.IO.Path.GetFileName(capturePath);
    string captureBaseName = captureFileName.EndsWith(".perf.data", StringComparison.OrdinalIgnoreCase)
        ? captureFileName.Substring(0, captureFileName.Length - ".perf.data".Length)
        : System.IO.Path.GetFileNameWithoutExtension(captureFileName);

    string[] kallsymsCandidates = new string[]
    {
        System.IO.Path.Combine(captureDirectory, captureBaseName + ".kallsyms"),
        System.IO.Path.Combine(captureDirectory, "kallsyms"),
        symbolPath != null
            ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(symbolPath.TrimEnd('/')) ?? ".", "kallsyms")
            : null
    };

    for (int candidateIndex = 0; candidateIndex < kallsymsCandidates.Length; ++candidateIndex)
    {
        if (kallsymsCandidates[candidateIndex] != null && System.IO.File.Exists(kallsymsCandidates[candidateIndex]))
        {
            kallsymsPath = kallsymsCandidates[candidateIndex];
            break;
        }
    }
}

// Runs the DWARF unwinder over a --call-graph dwarf capture and reports what
// it recovered. Separate from --json so the unwinder can be checked against a
// frame-pointer capture of the same binary before anything depends on it.
// Reports what the scheduling and futex tracepoints say about blocking and
// lock contention - the two questions a CPU profile structurally cannot
// answer.
if (Array.IndexOf(args, "--contention-check") >= 0)
{
    return RunContentionCheck(capturePath, symbolPath, kallsymsPath, hostSymbolsPath);
}

// Reports what DWARF inline information recovers for the capture's hottest
// addresses - the frames that exist in the source and not on the stack.
if (Array.IndexOf(args, "--inline-check") >= 0)
{
    return RunInlineCheck(capturePath, symbolPath);
}

if (Array.IndexOf(args, "--unwind-check") >= 0)
{
    return RunUnwindCheck(capturePath, symbolPath);
}


// The analysis export: turns the capture into the same JSON the .NET tool's
// webview already renders, via the same exporters. See
// Analysis/PerfCaptureAnalysis.cs.
int jsonIndex = Array.IndexOf(args, "--json");
if (jsonIndex >= 0)
{
    if (jsonIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("--json requires an output path");
        return 1;
    }

    return RunJsonExport(capturePath, args[jsonIndex + 1], symbolPath, kallsymsPath, hostSymbolsPath, Array.IndexOf(args, "--no-inline") < 0);
}

if (PerfScriptReader.LooksLikePerfScript(capturePath))
{
    return RunScriptSummary(capturePath);
}

Stopwatch readTimer = Stopwatch.StartNew();

SummarySink sink = new SummarySink();
PerfDataFile file = new PerfDataFile();
PerfReadResult result = file.Read(capturePath, sink);

readTimer.Stop();

if (!result.Success)
{
    Console.Error.WriteLine("perfParser: " + result.ErrorMessage);
    return 1;
}

Console.WriteLine("== Capture ==");
Console.WriteLine("  file           : " + capturePath);
Console.WriteLine("  size           : " + FormatBytes(file.FileSizeBytes));
Console.WriteLine("  hostname       : " + file.HostName);
Console.WriteLine("  os release     : " + file.OsRelease);
Console.WriteLine("  arch           : " + file.Architecture);
Console.WriteLine("  perf version   : " + file.PerfVersion);
Console.WriteLine("  command line   : " + file.CommandLine);
Console.WriteLine("  tracing data   : " + (file.HasTracingData ? "present" : "absent"));
if (file.TraceFormats != null)
{
    Console.WriteLine("  tracepoint fmts: " + file.TraceFormats.Count.ToString("N0")
        + "  kallsyms=" + (file.TraceFormats.KallsymsText.Length > 0 ? (file.TraceFormats.KallsymsText.Length / 1024).ToString("N0") + "KB" : "absent"));

    foreach (PerfEventAttr tracepointAttr in file.Attrs)
    {
        if (!tracepointAttr.IsTracepoint)
        {
            continue;
        }

        DotnetInsights.Perf.Tracepoints.TraceEventFormat format;
        if (file.TraceFormats.TryGetById((int)tracepointAttr.Config, out format))
        {
            System.Text.StringBuilder fieldList = new System.Text.StringBuilder();
            for (int fieldIndex = 0; fieldIndex < format.Fields.Count; ++fieldIndex)
            {
                if (format.Fields[fieldIndex].Name.StartsWith("common_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (fieldList.Length > 0)
                {
                    fieldList.Append(", ");
                }

                fieldList.Append(format.Fields[fieldIndex].Name);
                fieldList.Append('@');
                fieldList.Append(format.Fields[fieldIndex].Offset);
                fieldList.Append(':');
                fieldList.Append(format.Fields[fieldIndex].Size);
            }

            Console.WriteLine("      " + format.QualifiedName + " -> " + fieldList.ToString());
        }
    }
}
Console.WriteLine();

Console.WriteLine("== Events ==");
for (int attrIndex = 0; attrIndex < file.Attrs.Count; ++attrIndex)
{
    PerfEventAttr attr = file.Attrs[attrIndex];
    string name = attr.Name.Length > 0 ? attr.Name : ("type " + attr.Type.ToString() + " config 0x" + attr.Config.ToString("x"));

    long samplesForEvent;
    sink.SamplesByEventName.TryGetValue(name, out samplesForEvent);

    Console.WriteLine("  " + name);
    Console.WriteLine("      type=" + attr.Type.ToString() + " config=0x" + attr.Config.ToString("x") + " ids=" + attr.Ids.Count.ToString() + " sampleIdAll=" + attr.SampleIdAll.ToString());
    Console.WriteLine("      sample_type=" + attr.DescribeSampleType());
    Console.WriteLine("      samples=" + samplesForEvent.ToString("N0"));
}

Console.WriteLine();
Console.WriteLine("== Records ==");
Console.WriteLine("  total          : " + file.TotalRecordCount.ToString("N0"));
Console.WriteLine("  samples        : " + file.SampleRecordCount.ToString("N0"));
Console.WriteLine("  mappings       : " + sink.MappingCount.ToString("N0") + " (" + sink.ExecutableMappingCount.ToString("N0") + " executable)");
Console.WriteLine("  comm           : " + sink.CommRecordCount.ToString("N0"));
Console.WriteLine("  fork/exit      : " + sink.ForkCount.ToString("N0") + "/" + sink.ExitCount.ToString("N0"));
Console.WriteLine("  lost           : " + file.LostRecordCount.ToString("N0"));
Console.Write("  unknown type   : " + file.UnknownRecordCount.ToString("N0"));
foreach (KeyValuePair<uint, long> unknown in file.UnknownRecordCountsByType)
{
    Console.Write("  [type " + unknown.Key.ToString() + " x" + unknown.Value.ToString() + "]");
}

Console.WriteLine();
Console.WriteLine("  malformed      : " + file.MalformedRecordCount.ToString("N0"));

Console.WriteLine();
Console.WriteLine("== Samples ==");
Console.WriteLine("  with callchain : " + sink.SamplesWithCallchain.ToString("N0"));
Console.WriteLine("  with raw       : " + sink.SamplesWithRaw.ToString("N0"));
Console.WriteLine("  frames total   : " + sink.TotalFrames.ToString("N0"));
Console.WriteLine("  context marks  : " + sink.ContextMarkerFrames.ToString("N0"));
Console.WriteLine("  max depth      : " + sink.MaxCallchainDepth.ToString("N0"));
if (sink.SampleCount > 0)
{
    double spanSeconds = (sink.MaxTime - sink.MinTime) / 1000000000.0;
    Console.WriteLine("  time span      : " + spanSeconds.ToString("F3") + "s");
}

Console.WriteLine();
Console.WriteLine("== Threads by sample count ==");
List<KeyValuePair<int, long>> rankedThreads = new List<KeyValuePair<int, long>>(sink.SamplesByThreadId);
rankedThreads.Sort(CompareBySampleCountDescending);

int shownThreadCount = 0;
for (int threadIndex = 0; threadIndex < rankedThreads.Count && shownThreadCount < 25; ++threadIndex)
{
    int threadId = rankedThreads[threadIndex].Key;

    string comm;
    if (!sink.CommByThreadId.TryGetValue(threadId, out comm))
    {
        comm = "?";
    }

    int processId;
    sink.ProcessIdByThreadId.TryGetValue(threadId, out processId);

    Console.WriteLine("  " + rankedThreads[threadIndex].Value.ToString("N0").PadLeft(10) + "  " + comm.PadRight(18) + " tid=" + threadId.ToString() + " pid=" + processId.ToString());
    ++shownThreadCount;
}

Console.WriteLine();
Console.WriteLine("== Executable mappings ==");
Console.WriteLine("  distinct modules: " + sink.DistinctExecutableModules.Count.ToString("N0"));

int shownModuleCount = 0;
foreach (KeyValuePair<string, string> module in sink.DistinctExecutableModules)
{
    if (shownModuleCount >= 20)
    {
        break;
    }

    string resolvedBuildId = file.BuildIdForModule(module.Key, module.Value);
    string buildId = resolvedBuildId.Length > 0 ? resolvedBuildId : "(no build id)";
    Console.WriteLine("  " + module.Key + "  " + buildId);
    ++shownModuleCount;
}

////////////////////////////////////////////////////////////////////////////////
// Hot methods.
//
// Symbolization happens AFTER the record walk, not during it. Two reasons, and
// the second is structural: build ids live in a feature section stored after
// the data section, and a mapping's own record does not necessarily carry one -
// so a mapping delivered during the walk cannot yet be matched to a file.
////////////////////////////////////////////////////////////////////////////////

Stopwatch symbolTimer = Stopwatch.StartNew();

PerfSymbolTable symbolTable = new PerfSymbolTable(symbolPath);
for (int mappingIndex = 0; mappingIndex < sink.Mappings.Count; ++mappingIndex)
{
    PerfMapping mapping = sink.Mappings[mappingIndex];
    symbolTable.AddMapping(in mapping, file.BuildIdForModule(mapping.FileName, mapping.BuildId));
}

symbolTable.FinishBuilding();

List<KeyValuePair<string, long>> hotMethods = new List<KeyValuePair<string, long>>();
{
    Dictionary<string, long> samplesByMethod = new Dictionary<string, long>(StringComparer.Ordinal);

    foreach (KeyValuePair<long, long> leaf in sink.SamplesByLeafAddress)
    {
        string methodName = symbolTable.Resolve(sink.PrimaryProcessId, leaf.Key);

        long existing;
        samplesByMethod.TryGetValue(methodName, out existing);
        samplesByMethod[methodName] = existing + leaf.Value;
    }

    foreach (KeyValuePair<string, long> entry in samplesByMethod)
    {
        hotMethods.Add(entry);
    }
}

hotMethods.Sort(CompareBySampleCountDescendingByName);
symbolTimer.Stop();

Console.WriteLine();
Console.WriteLine("== Hot methods (self) ==");
Console.WriteLine("  modules with symbols   : " + symbolTable.ModulesWithSymbols.ToString("N0"));
Console.WriteLine("  modules without        : " + symbolTable.ModulesWithoutSymbols.ToString("N0"));
Console.WriteLine();

long resolvedSampleTotal = 0;
for (int methodIndex = 0; methodIndex < hotMethods.Count; ++methodIndex)
{
    resolvedSampleTotal += hotMethods[methodIndex].Value;
}

for (int methodIndex = 0; methodIndex < hotMethods.Count && methodIndex < 20; ++methodIndex)
{
    double share = resolvedSampleTotal > 0 ? (100.0 * hotMethods[methodIndex].Value / resolvedSampleTotal) : 0;
    Console.WriteLine("  " + share.ToString("F2").PadLeft(6) + "%  " + hotMethods[methodIndex].Value.ToString("N0").PadLeft(8) + "  " + hotMethods[methodIndex].Key);
}

if (symbolTable.UnresolvedModules.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("  Modules with no symbols available:");
    for (int moduleIndex = 0; moduleIndex < symbolTable.UnresolvedModules.Count && moduleIndex < 10; ++moduleIndex)
    {
        Console.WriteLine("    " + symbolTable.UnresolvedModules[moduleIndex]);
    }
}

Console.WriteLine();
Console.WriteLine("Timing: read=" + readTimer.ElapsedMilliseconds.ToString() + "ms symbolize=" + symbolTimer.ElapsedMilliseconds.ToString() + "ms");

if (dumpRecords)
{
    Console.WriteLine();
    Console.WriteLine("== First callchains ==");
    for (int chainIndex = 0; chainIndex < sink.FirstCallchains.Count; ++chainIndex)
    {
        Console.WriteLine("  sample " + chainIndex.ToString() + ":");
        long[] chain = sink.FirstCallchains[chainIndex];
        for (int frameIndex = 0; frameIndex < chain.Length; ++frameIndex)
        {
            Console.WriteLine("      0x" + chain[frameIndex].ToString("x12"));
        }
    }
}

return 0;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Off-CPU time: where threads were NOT running, and why. Deliberately its own
// section rather than folded into cpuProfile - a CPU profile's percentages are
// shares of time SPENT ON CPU, and mixing a quantity measured in wall-clock
// milliseconds into them would make both unreadable.
// The capture's own embedded copy first, then an explicit file. Both come from
// the RECORDING machine; there is deliberately no fallback to the local
// /proc/kallsyms, which would resolve a Linux capture's kernel frames against
// whatever kernel happens to be running here - silently, and wrongly.
static PerfScriptSymbolOracle LoadHostSymbols(string explicitPath, string capturePath)
{
    string path = explicitPath ?? PerfScriptSymbolOracle.FindCompanionScriptFile(capturePath);
    if (string.IsNullOrEmpty(path))
    {
        return null;
    }

    PerfScriptSymbolOracle oracle = new PerfScriptSymbolOracle();

    string loadError;
    if (!oracle.TryLoad(path, out loadError))
    {
        Console.Error.WriteLine("perfParser: ignoring " + path + ": " + loadError);
        return null;
    }

    Console.Error.WriteLine("perfParser: using host symbols from " + System.IO.Path.GetFileName(path)
        + " (" + oracle.Count.ToString("N0") + " addresses)");

    return oracle;
}

static KallsymsTable LoadKernelSymbols(string kallsymsPath, PerfDataFile file)
{
    string parseError;

    if (file.TraceFormats != null && file.TraceFormats.KallsymsText.Length > 0)
    {
        KallsymsTable embedded = KallsymsTable.TryParse(file.TraceFormats.KallsymsText, out parseError);
        if (embedded != null)
        {
            return embedded;
        }
    }

    if (!string.IsNullOrEmpty(kallsymsPath))
    {
        KallsymsTable fromFile = KallsymsTable.TryLoadFromFile(kallsymsPath, out parseError);
        if (fromFile == null)
        {
            Console.Error.WriteLine("perfParser: could not read kallsyms: " + parseError);
        }

        return fromFile;
    }

    return null;
}

static void WriteOffCpu(System.Text.Json.Utf8JsonWriter writer, PerfCaptureAnalysis analysis)
{
    TracepointProjection projection = analysis.Tracepoints.Projection;

    Dictionary<OffCpuReason, double> byReason = new Dictionary<OffCpuReason, double>();
    Dictionary<long, double> byThread = new Dictionary<long, double>();
    Dictionary<int, double> byStack = new Dictionary<int, double>();
    Dictionary<int, long> countByStack = new Dictionary<int, long>();
    double totalMSec = 0;

    for (int intervalIndex = 0; intervalIndex < projection.OffCpuIntervals.Count; ++intervalIndex)
    {
        OffCpuInterval interval = projection.OffCpuIntervals[intervalIndex];
        totalMSec += interval.DurationMSec;

        double existing;
        byReason.TryGetValue(interval.Reason, out existing);
        byReason[interval.Reason] = existing + interval.DurationMSec;

        byThread.TryGetValue(interval.ThreadId, out existing);
        byThread[interval.ThreadId] = existing + interval.DurationMSec;

        byStack.TryGetValue(interval.StackIndex, out existing);
        byStack[interval.StackIndex] = existing + interval.DurationMSec;

        long existingCount;
        countByStack.TryGetValue(interval.StackIndex, out existingCount);
        countByStack[interval.StackIndex] = existingCount + 1;
    }

    writer.WriteStartObject();
    writer.WriteNumber("totalMSec", totalMSec);
    writer.WriteNumber("intervalCount", projection.OffCpuIntervals.Count);
    writer.WriteNumber("schedSwitchCount", projection.SchedSwitchCount);
    writer.WriteNumber("futexWaitCount", projection.FutexWaitCount);

    writer.WritePropertyName("byReason");
    writer.WriteStartArray();
    foreach (KeyValuePair<OffCpuReason, double> entry in byReason)
    {
        writer.WriteStartObject();
        writer.WriteString("reason", TracepointProjector.NameForReason(entry.Key));
        writer.WriteNumber("mSec", entry.Value);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();

    List<KeyValuePair<long, double>> rankedThreads = new List<KeyValuePair<long, double>>(byThread);
    rankedThreads.Sort(static (left, right) => right.Value.CompareTo(left.Value));

    writer.WritePropertyName("threads");
    writer.WriteStartArray();
    for (int threadIndex = 0; threadIndex < rankedThreads.Count && threadIndex < 200; ++threadIndex)
    {
        long threadId = rankedThreads[threadIndex].Key;

        string threadName;
        if (!projection.ThreadNames.TryGetValue(threadId, out threadName) && !analysis.CommByThreadId.TryGetValue((int)threadId, out threadName))
        {
            threadName = "?";
        }

        writer.WriteStartObject();
        writer.WriteNumber("threadId", threadId);
        writer.WriteString("name", threadName);
        writer.WriteNumber("offCpuMSec", rankedThreads[threadIndex].Value);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();

    List<KeyValuePair<int, double>> rankedStacks = new List<KeyValuePair<int, double>>(byStack);
    rankedStacks.Sort(static (left, right) => right.Value.CompareTo(left.Value));

    writer.WritePropertyName("stacks");
    writer.WriteStartArray();
    for (int stackRank = 0; stackRank < rankedStacks.Count && stackRank < 200; ++stackRank)
    {
        int stackIndex = rankedStacks[stackRank].Key;

        writer.WriteStartObject();
        writer.WriteNumber("mSec", rankedStacks[stackRank].Value);
        writer.WriteNumber("count", countByStack[stackIndex]);

        writer.WritePropertyName("frames");
        writer.WriteStartArray();

        long[] frames = analysis.Stacks.FramesAt(stackIndex);
        for (int frameIndex = 0; frameIndex < frames.Length && frameIndex < 32; ++frameIndex)
        {
            writer.WriteStringValue(analysis.SymbolTable.Resolve(frames[frameIndex], 0));
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
}

static int RunInlineCheck(string capturePath, string symbolPath)
{
    PerfDataFile mappingPass = new PerfDataFile();
    MappingCollectorSink mappingSink = new MappingCollectorSink();
    if (!mappingPass.Read(capturePath, mappingSink).Success)
    {
        Console.Error.WriteLine("perfParser: could not read " + capturePath);
        return 1;
    }

    PerfSymbolTable symbols = new PerfSymbolTable(symbolPath);
    for (int mappingIndex = 0; mappingIndex < mappingSink.Mappings.Count; ++mappingIndex)
    {
        PerfMapping mapping = mappingSink.Mappings[mappingIndex];
        symbols.AddMapping(in mapping, mappingPass.BuildIdForModule(mapping.FileName, mapping.BuildId));
    }

    symbols.FinishBuilding();

    PerfCaptureAnalysis analysis = new PerfCaptureAnalysis();
    PerfDataFile file = new PerfDataFile();
    if (!file.Read(capturePath, analysis).Success)
    {
        return 1;
    }

    // Rank leaf addresses, then ask each one what it was inlined into.
    Dictionary<long, long> samplesByLeafAddress = new Dictionary<long, long>();
    int processId = 0;

    for (int sampleIndex = 0; sampleIndex < analysis.Samples.Count; ++sampleIndex)
    {
        long[] frames = analysis.Stacks.FramesAt(analysis.Samples[sampleIndex].StackIndex);
        if (frames.Length == 0)
        {
            continue;
        }

        long existing;
        samplesByLeafAddress.TryGetValue(frames[0], out existing);
        samplesByLeafAddress[frames[0]] = existing + 1;
    }

    // The kernel image is recorded as pid -1, and pseudo-modules ([vdso],
    // [rosetta], ...) are not the traced process either. The first mapping
    // with a real filesystem path is.
    for (int mappingIndex = 0; mappingIndex < mappingSink.Mappings.Count; ++mappingIndex)
    {
        PerfMapping candidate = mappingSink.Mappings[mappingIndex];
        if (candidate.IsExecutable && candidate.ProcessId > 0 && candidate.FileName.Length > 0 && candidate.FileName[0] == '/')
        {
            processId = candidate.ProcessId;
            break;
        }
    }

    List<KeyValuePair<long, long>> ranked = new List<KeyValuePair<long, long>>(samplesByLeafAddress);
    ranked.Sort(static (left, right) => right.Value.CompareTo(left.Value));

    Console.WriteLine("== Inline expansion ==");
    Console.WriteLine("  distinct leaf addresses : " + ranked.Count.ToString("N0"));

    Dictionary<string, DebugInfoIndex> indexByModule = new Dictionary<string, DebugInfoIndex>(StringComparer.Ordinal);
    List<string> inlineChain = new List<string>();

    long addressesWithInlining = 0;
    long framesRecovered = 0;
    int shown = 0;

    for (int rankIndex = 0; rankIndex < ranked.Count; ++rankIndex)
    {
        long address = ranked[rankIndex].Key;

        string modulePath;
        string moduleKey;
        long bias;
        if (!symbols.TryGetUnwindContext(processId, address, out modulePath, out moduleKey, out bias))
        {
            continue;
        }

        DebugInfoIndex index;
        if (!indexByModule.TryGetValue(moduleKey, out index))
        {
            string sectionError;
            ElfSections sections = ElfSections.TryLoad(modulePath, out sectionError);

            string indexError;
            index = sections != null ? DebugInfoIndex.TryLoad(sections, out indexError) : null;
            indexByModule[moduleKey] = index;

            if (index != null)
            {
                Console.WriteLine("  " + System.IO.Path.GetFileName(modulePath) + ": " + index.RangeCount.ToString("N0") + " inline ranges");
            }
        }

        if (index == null)
        {
            continue;
        }

        int recovered = index.GetInlineChain(address + bias, inlineChain);
        if (recovered == 0)
        {
            continue;
        }

        ++addressesWithInlining;
        framesRecovered += recovered;

        if (shown < 6)
        {
            ++shown;
            Console.WriteLine();
            Console.WriteLine("  " + ranked[rankIndex].Value.ToString("N0") + " samples at 0x" + address.ToString("x") + ":");
            for (int frameIndex = 0; frameIndex < inlineChain.Count; ++frameIndex)
            {
                Console.WriteLine("      [inlined] " + inlineChain[frameIndex]);
            }

            Console.WriteLine("      " + symbols.Resolve(processId, address));
        }
    }

    Console.WriteLine();
    Console.WriteLine("  leaf addresses with inlined frames : " + addressesWithInlining.ToString("N0") + " of " + ranked.Count.ToString("N0"));
    Console.WriteLine("  inlined frames recovered           : " + framesRecovered.ToString("N0"));
    return 0;
}

static int RunContentionCheck(string capturePath, string symbolPath, string kallsymsPath, string hostSymbolsPath)
{
    Stopwatch timer = Stopwatch.StartNew();

    PerfCaptureAnalysis analysis = new PerfCaptureAnalysis();
    PerfDataFile file = new PerfDataFile();
    analysis.AttachFile(file);

    PerfReadResult result = file.Read(capturePath, analysis);
    if (!result.Success)
    {
        Console.Error.WriteLine("perfParser: " + result.ErrorMessage);
        return 1;
    }

    if (analysis.Tracepoints == null)
    {
        Console.Error.WriteLine("perfParser: this capture has no tracepoint data. Record with -e sched:sched_switch and -e syscalls:sys_enter_futex,syscalls:sys_exit_futex.");
        return 1;
    }

    analysis.Tracepoints.Finish();
    analysis.RebaseTimestamps();
    analysis.KernelSymbols = LoadKernelSymbols(kallsymsPath, file);
    analysis.HostSymbols = LoadHostSymbols(hostSymbolsPath, capturePath);
    analysis.BuildSymbols(file, symbolPath);
    timer.Stop();

    TracepointProjection projection = analysis.Tracepoints.Projection;

    Console.WriteLine("== Scheduling and contention ==");
    Console.WriteLine("  sched_switch events : " + projection.SchedSwitchCount.ToString("N0"));
    Console.WriteLine("  futex waits paired  : " + projection.FutexWaitCount.ToString("N0"));
    Console.WriteLine("  unpaired futex      : " + projection.UnpairedFutexEnters.ToString("N0"));
    Console.WriteLine("  off-CPU intervals   : " + projection.OffCpuIntervals.Count.ToString("N0"));
    Console.WriteLine("  elapsed             : " + timer.ElapsedMilliseconds.ToString() + "ms");

    ////////////////////////////////////////////////////////////////////////////
    // Locks, ranked by the total time threads spent waiting on each. Count
    // alone ranks the wrong lock: a lock taken a million times for a
    // microscopic critical section is not the one holding the program up.
    ////////////////////////////////////////////////////////////////////////////

    Dictionary<long, double> waitByLock = new Dictionary<long, double>();
    Dictionary<long, long> countByLock = new Dictionary<long, long>();

    for (int eventIndex = 0; eventIndex < projection.ContentionEvents.Count; ++eventIndex)
    {
        ContentionEvent contention = projection.ContentionEvents[eventIndex];

        double existingWait;
        waitByLock.TryGetValue(contention.LockId, out existingWait);
        waitByLock[contention.LockId] = existingWait + contention.DurationMSec;

        long existingCount;
        countByLock.TryGetValue(contention.LockId, out existingCount);
        countByLock[contention.LockId] = existingCount + 1;
    }

    List<KeyValuePair<long, double>> rankedLocks = new List<KeyValuePair<long, double>>(waitByLock);
    rankedLocks.Sort(static (left, right) => right.Value.CompareTo(left.Value));

    Console.WriteLine();
    Console.WriteLine("== Locks by total wait ==");
    for (int lockIndex = 0; lockIndex < rankedLocks.Count && lockIndex < 8; ++lockIndex)
    {
        long lockAddress = rankedLocks[lockIndex].Key;
        Console.WriteLine("  0x" + lockAddress.ToString("x") + "  " + rankedLocks[lockIndex].Value.ToString("N1").PadLeft(10) + " ms over "
            + countByLock[lockAddress].ToString("N0") + " waits");
    }

    ////////////////////////////////////////////////////////////////////////////
    // Where threads blocked, by the stack they blocked in.
    ////////////////////////////////////////////////////////////////////////////

    Dictionary<int, double> waitByStack = new Dictionary<int, double>();
    Dictionary<int, long> countByStack = new Dictionary<int, long>();

    for (int eventIndex = 0; eventIndex < projection.ContentionEvents.Count; ++eventIndex)
    {
        ContentionEvent contention = projection.ContentionEvents[eventIndex];

        double existingWait;
        waitByStack.TryGetValue(contention.StackIndex, out existingWait);
        waitByStack[contention.StackIndex] = existingWait + contention.DurationMSec;

        long existingCount;
        countByStack.TryGetValue(contention.StackIndex, out existingCount);
        countByStack[contention.StackIndex] = existingCount + 1;
    }

    List<KeyValuePair<int, double>> rankedStacks = new List<KeyValuePair<int, double>>(waitByStack);
    rankedStacks.Sort(static (left, right) => right.Value.CompareTo(left.Value));

    Console.WriteLine();
    Console.WriteLine("== Lock waits by call site ==");
    for (int stackIndex = 0; stackIndex < rankedStacks.Count && stackIndex < 5; ++stackIndex)
    {
        int stack = rankedStacks[stackIndex].Key;
        Console.WriteLine("  " + rankedStacks[stackIndex].Value.ToString("N1").PadLeft(9) + " ms over "
            + countByStack[stack].ToString("N0").PadLeft(6) + " waits:");

        long[] frames = analysis.Stacks.FramesAt(stack);
        for (int frameIndex = 0; frameIndex < frames.Length && frameIndex < 6; ++frameIndex)
        {
            Console.WriteLine("        " + analysis.SymbolTable.Resolve(frames[frameIndex], 0));
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Off-CPU time, split by WHY. Preemption and blocking are different
    // problems and a single "not running" number hides which one you have.
    ////////////////////////////////////////////////////////////////////////////

    Dictionary<OffCpuReason, double> offCpuByReason = new Dictionary<OffCpuReason, double>();
    Dictionary<long, double> offCpuByThread = new Dictionary<long, double>();

    for (int intervalIndex = 0; intervalIndex < projection.OffCpuIntervals.Count; ++intervalIndex)
    {
        OffCpuInterval interval = projection.OffCpuIntervals[intervalIndex];

        double existingReason;
        offCpuByReason.TryGetValue(interval.Reason, out existingReason);
        offCpuByReason[interval.Reason] = existingReason + interval.DurationMSec;

        double existingThread;
        offCpuByThread.TryGetValue(interval.ThreadId, out existingThread);
        offCpuByThread[interval.ThreadId] = existingThread + interval.DurationMSec;
    }

    Console.WriteLine();
    Console.WriteLine("== Off-CPU time by reason ==");
    foreach (KeyValuePair<OffCpuReason, double> entry in offCpuByReason)
    {
        Console.WriteLine("  " + entry.Value.ToString("N1").PadLeft(11) + " ms  " + TracepointProjector.NameForReason(entry.Key));
    }

    List<KeyValuePair<long, double>> rankedStates = new List<KeyValuePair<long, double>>(projection.OffCpuMSecByRawState);
    rankedStates.Sort(static (left, right) => right.Value.CompareTo(left.Value));

    Console.WriteLine();
    Console.WriteLine("== Off-CPU by raw prev_state ==");
    for (int stateIndex = 0; stateIndex < rankedStates.Count && stateIndex < 10; ++stateIndex)
    {
        Console.WriteLine("  0x" + rankedStates[stateIndex].Key.ToString("x").PadLeft(4)
            + "  " + rankedStates[stateIndex].Value.ToString("N1").PadLeft(11) + " ms  -> "
            + TracepointProjector.NameForReason(TracepointProjector.ClassifyStateForDiagnostics(rankedStates[stateIndex].Key)));
    }

    List<KeyValuePair<long, double>> rankedThreads = new List<KeyValuePair<long, double>>(offCpuByThread);
    rankedThreads.Sort(static (left, right) => right.Value.CompareTo(left.Value));

    Console.WriteLine();
    Console.WriteLine("== Threads by off-CPU time ==");
    for (int threadIndex = 0; threadIndex < rankedThreads.Count && threadIndex < 12; ++threadIndex)
    {
        long threadId = rankedThreads[threadIndex].Key;

        string comm;
        if (!projection.ThreadNames.TryGetValue(threadId, out comm) && !analysis.CommByThreadId.TryGetValue((int)threadId, out comm))
        {
            comm = "?";
        }

        Console.WriteLine("  " + rankedThreads[threadIndex].Value.ToString("N1").PadLeft(11) + " ms  "
            + comm + " (tid " + threadId.ToString() + ")");
    }

    return 0;
}

static int RunUnwindCheck(string capturePath, string symbolPath)
{
    Stopwatch timer = Stopwatch.StartNew();

    // TWO PASSES, and the second one is not an optimisation that could be
    // folded away. Unwinding a sample needs the module its addresses fall in,
    // and a capture's MMAP2 records are NOT guaranteed to precede its samples
    // in FILE order - perf writes one ring buffer per CPU and interleaves them
    // at flush points, so a mapping recorded microseconds before a sample can
    // land after it in the file. Building the symbol table on first sight of a
    // sample therefore built it from a partial address space, and every
    // address resolved to nothing: 5,021 of 5,021 samples stopped at depth 1
    // against a capture whose mappings were completely intact.
    PerfDataFile mappingPass = new PerfDataFile();
    MappingCollectorSink mappingSink = new MappingCollectorSink();
    PerfReadResult mappingResult = mappingPass.Read(capturePath, mappingSink);
    if (!mappingResult.Success)
    {
        Console.Error.WriteLine("perfParser: " + mappingResult.ErrorMessage);
        return 1;
    }

    PerfSymbolTable symbols = new PerfSymbolTable(symbolPath);
    for (int mappingIndex = 0; mappingIndex < mappingSink.Mappings.Count; ++mappingIndex)
    {
        PerfMapping mapping = mappingSink.Mappings[mappingIndex];
        symbols.AddMapping(in mapping, mappingPass.BuildIdForModule(mapping.FileName, mapping.BuildId));
    }

    symbols.FinishBuilding();

    PerfDataFile file = new PerfDataFile();
    UnwindSink sink = new UnwindSink(symbols, new DwarfUnwinder(symbols, mappingPass.Architecture), mappingSink.CommByThreadId);
    PerfReadResult result = file.Read(capturePath, sink);
    if (!result.Success)
    {
        Console.Error.WriteLine("perfParser: " + result.ErrorMessage);
        return 1;
    }

    timer.Stop();

    DwarfUnwinder unwinder = sink.Unwinder;

    Console.WriteLine("== DWARF unwind ==");
    Console.WriteLine("  architecture      : " + unwinder.Architecture.ToString());
    Console.WriteLine("  samples with regs : " + sink.SamplesWithRegisters.ToString("N0"));
    Console.WriteLine("  samples with stack: " + sink.SamplesWithStack.ToString("N0"));
    Console.WriteLine("  samples unwound   : " + unwinder.SamplesUnwound.ToString("N0"));
    Console.WriteLine("  frames recovered  : " + unwinder.FramesRecovered.ToString("N0"));

    if (unwinder.SamplesUnwound > 0)
    {
        Console.WriteLine("  mean depth        : " + ((double)unwinder.FramesRecovered / unwinder.SamplesUnwound).ToString("F2"));
    }

    Console.WriteLine("  stopped: no FDE   : " + unwinder.SamplesStoppedNoFde.ToString("N0"));
    Console.WriteLine("  stopped: off stack: " + unwinder.SamplesStoppedOffStack.ToString("N0"));
    Console.WriteLine("  stopped: expr CFA : " + unwinder.SamplesStoppedExpression.ToString("N0"));
    Console.WriteLine("  elapsed           : " + timer.ElapsedMilliseconds.ToString() + "ms");

    Console.WriteLine();
    Console.WriteLine("== Sample reconstructed stacks ==");
    for (int stackIndex = 0; stackIndex < sink.ExampleStacks.Count; ++stackIndex)
    {
        Console.WriteLine("  --- " + sink.ExampleThreadNames[stackIndex] + " ---");
        List<string> stack = sink.ExampleStacks[stackIndex];
        for (int frameIndex = 0; frameIndex < stack.Count; ++frameIndex)
        {
            Console.WriteLine("      " + stack[frameIndex]);
        }
    }

    Console.WriteLine();
    Console.WriteLine("== Hot methods (self, from unwound stacks) ==");
    List<KeyValuePair<string, long>> ranked = new List<KeyValuePair<string, long>>(sink.SamplesByLeaf);
    ranked.Sort(CompareBySampleCountDescendingByName);

    long total = 0;
    for (int index = 0; index < ranked.Count; ++index)
    {
        total += ranked[index].Value;
    }

    for (int index = 0; index < ranked.Count && index < 12; ++index)
    {
        double share = total > 0 ? (100.0 * ranked[index].Value / total) : 0;
        Console.WriteLine("  " + share.ToString("F2").PadLeft(6) + "%  " + ranked[index].Value.ToString("N0").PadLeft(7) + "  " + ranked[index].Key);
    }

    return 0;
}

// `perf script` text output (a `perf.stacks` file), which is the artifact that
// actually leaves a production host - see PerfScript/PerfScriptReader.cs. Frames
// arrive already symbolized, so none of the symbol/unwind machinery applies and
// the shared exporters are fed directly.
static int RunScriptSummary(string scriptPath)
{
    PerfScriptAnalysis analysis = new PerfScriptAnalysis();
    PerfScriptReader reader = new PerfScriptReader();

    Stopwatch timer = Stopwatch.StartNew();
    PerfScriptReadResult result = reader.Read(scriptPath, analysis);
    timer.Stop();

    if (!result.Success)
    {
        Console.Error.WriteLine("perfParser: " + result.ErrorMessage);
        return 1;
    }

    analysis.RebaseTimestamps();
    analysis.BuildSymbols();

    Console.WriteLine("== Capture (perf script text) ==");
    Console.WriteLine("  file           : " + scriptPath);
    Console.WriteLine("  hostname       : " + reader.Header.HostName);
    Console.WriteLine("  os release     : " + reader.Header.OsRelease);
    Console.WriteLine("  arch           : " + reader.Header.Architecture);
    Console.WriteLine("  perf version   : " + reader.Header.PerfVersion);
    Console.WriteLine("  captured on    : " + reader.Header.CapturedOn);
    Console.WriteLine("  command line   : " + reader.Header.CommandLine);
    Console.WriteLine("  events         : " + string.Join(", ", reader.Header.EventNames));
    Console.WriteLine();
    Console.WriteLine("  samples        : " + reader.SampleCount.ToString("N0"));
    Console.WriteLine("  frames         : " + reader.FrameCount.ToString("N0"));
    Console.WriteLine("  distinct stacks: " + analysis.Stacks.Count.ToString("N0"));
    Console.WriteLine("  distinct frames: " + analysis.DistinctFrameNameCount.ToString("N0"));
    Console.WriteLine("  duration       : " + analysis.CaptureDurationSeconds.ToString("F3") + "s");
    Console.WriteLine("  unparsed lines : " + reader.UnparsedLineCount.ToString("N0"));

    Console.WriteLine();
    Console.WriteLine("== Hot methods (self) ==");

    Dictionary<int, long> samplesByLeaf = new Dictionary<int, long>();
    for (int sampleIndex = 0; sampleIndex < analysis.Samples.Count; ++sampleIndex)
    {
        long[] frames = analysis.Stacks.FramesAt(analysis.Samples[sampleIndex].StackIndex);
        if (frames.Length == 0)
        {
            continue;
        }

        int frameId = analysis.SymbolTable.ResolveId(frames[0], 0);
        long existing;
        samplesByLeaf.TryGetValue(frameId, out existing);
        samplesByLeaf[frameId] = existing + 1;
    }

    List<KeyValuePair<int, long>> ranked = new List<KeyValuePair<int, long>>(samplesByLeaf);
    ranked.Sort(static (left, right) => right.Value.CompareTo(left.Value));

    for (int rankIndex = 0; rankIndex < ranked.Count && rankIndex < 15; ++rankIndex)
    {
        double share = reader.SampleCount > 0 ? (100.0 * ranked[rankIndex].Value / reader.SampleCount) : 0;
        Console.WriteLine("  " + share.ToString("F2").PadLeft(6) + "%  " + ranked[rankIndex].Value.ToString("N0").PadLeft(8)
            + "  " + analysis.SymbolTable.NameForId(ranked[rankIndex].Key));
    }

    Console.WriteLine();
    Console.WriteLine("Timing: read=" + timer.ElapsedMilliseconds.ToString() + "ms");
    return 0;
}

static int RunScriptJsonExport(string scriptPath, string outputPath)
{
    Stopwatch readTimer = Stopwatch.StartNew();

    PerfScriptAnalysis analysis = new PerfScriptAnalysis();
    PerfScriptReader reader = new PerfScriptReader();
    PerfScriptReadResult result = reader.Read(scriptPath, analysis);

    if (!result.Success)
    {
        Console.Error.WriteLine("perfParser: " + result.ErrorMessage);
        return 1;
    }

    analysis.RebaseTimestamps();
    analysis.BuildSymbols();
    readTimer.Stop();

    Stopwatch exportTimer = Stopwatch.StartNew();

    using (System.IO.FileStream output = new System.IO.FileStream(outputPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
    using (System.Text.Json.Utf8JsonWriter writer = new System.Text.Json.Utf8JsonWriter(output))
    {
        writer.WriteStartObject();
        writer.WriteString("source", "perf-script");

        writer.WritePropertyName("captureInfo");
        writer.WriteStartObject();
        writer.WriteString("filePath", scriptPath);
        writer.WriteNumber("fileSizeBytes", new System.IO.FileInfo(scriptPath).Length);
        writer.WriteString("hostName", reader.Header.HostName);
        writer.WriteString("osRelease", reader.Header.OsRelease);
        writer.WriteString("architecture", reader.Header.Architecture);
        writer.WriteString("perfVersion", reader.Header.PerfVersion);
        writer.WriteString("commandLine", reader.Header.CommandLine);
        writer.WriteString("capturedOn", reader.Header.CapturedOn);
        writer.WriteNumber("sampleCount", analysis.Samples.Count);
        writer.WriteNumber("distinctStackCount", analysis.Stacks.Count);
        writer.WriteNumber("distinctFrameNameCount", analysis.DistinctFrameNameCount);
        writer.WriteNumber("captureDurationSeconds", analysis.CaptureDurationSeconds);

        // Named so the view can say where the symbols came from. In this mode
        // they came from perf, on the recording host - which is better
        // symbolization than this tool could do, and worth stating.
        writer.WriteString("stackSource", "perf-script");
        writer.WriteString("symbolSource", "resolved by perf on the recording host");
        writer.WriteEndObject();

        writer.WritePropertyName("threads");
        writer.WriteStartArray();
        {
            Dictionary<long, long> samplesByThread = new Dictionary<long, long>();
            for (int sampleIndex = 0; sampleIndex < analysis.Samples.Count; ++sampleIndex)
            {
                long threadId = analysis.Samples[sampleIndex].ThreadId;
                long existing;
                samplesByThread.TryGetValue(threadId, out existing);
                samplesByThread[threadId] = existing + 1;
            }

            List<KeyValuePair<long, long>> ranked = new List<KeyValuePair<long, long>>(samplesByThread);
            ranked.Sort(static (left, right) => right.Value.CompareTo(left.Value));

            for (int threadIndex = 0; threadIndex < ranked.Count; ++threadIndex)
            {
                string comm;
                writer.WriteStartObject();
                writer.WriteNumber("threadId", ranked[threadIndex].Key);
                writer.WriteString("name", analysis.CommByThreadId.TryGetValue(ranked[threadIndex].Key, out comm) ? comm : "?");
                writer.WriteNumber("sampleCount", ranked[threadIndex].Value);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();

        writer.WritePropertyName("cpuProfile");
        CpuProfileJsonExporter.Write(
            writer,
            analysis.Samples,
            analysis.Stacks,
            analysis.SymbolTable,
            out CpuCategoryBuilder.CategoryTotals[] scriptCategoryTotals,
            null,
            analysis.NativeSymbols);

        writer.WriteEndObject();
        writer.Flush();
    }

    exportTimer.Stop();

    Console.Error.WriteLine("Timing: read=" + readTimer.ElapsedMilliseconds.ToString()
        + "ms export=" + exportTimer.ElapsedMilliseconds.ToString()
        + "ms samples=" + analysis.Samples.Count.ToString("N0")
        + " stacks=" + analysis.Stacks.Count.ToString("N0")
        + " frames=" + analysis.DistinctFrameNameCount.ToString("N0")
        + (reader.UnparsedLineCount > 0 ? " unparsedLines=" + reader.UnparsedLineCount.ToString("N0") : string.Empty));

    return 0;
}

static int RunJsonExport(string capturePath, string outputPath, string symbolPath, string kallsymsPath, string hostSymbolsPath, bool expandInlineFrames)
{
    // Dispatch on what the file actually IS rather than on its extension - a
    // `perf.stacks` may be named anything, and a perf.data has a magic number.
    if (PerfScriptReader.LooksLikePerfScript(capturePath))
    {
        return RunScriptJsonExport(capturePath, outputPath);
    }

    Stopwatch readTimer = Stopwatch.StartNew();

    // A mapping pass first, unconditionally. Unwinding needs a COMPLETE
    // address space before the first sample it touches, and perf's per-CPU
    // buffers mean a mapping can land after a sample in file order even when
    // it happened first - see RunUnwindCheck's note. Cheap relative to the
    // work that follows (a 41MB capture walks in ~17ms), and it also supplies
    // the thread names.
    PerfDataFile mappingPass = new PerfDataFile();
    MappingCollectorSink mappingSink = new MappingCollectorSink();
    PerfReadResult mappingResult = mappingPass.Read(capturePath, mappingSink);
    if (!mappingResult.Success)
    {
        Console.Error.WriteLine("perfParser: " + mappingResult.ErrorMessage);
        return 1;
    }

    PerfSymbolTable unwindSymbols = new PerfSymbolTable(symbolPath);
    for (int mappingIndex = 0; mappingIndex < mappingSink.Mappings.Count; ++mappingIndex)
    {
        PerfMapping mapping = mappingSink.Mappings[mappingIndex];
        unwindSymbols.AddMapping(in mapping, mappingPass.BuildIdForModule(mapping.FileName, mapping.BuildId));
    }

    unwindSymbols.FinishBuilding();

    // Tells the shared category classifier it is reading a native capture; see
    // CpuCategoryClassifier.NativeProfile. Without it a Rust profile reports
    // 95% Uncategorized, because a crate path and a C++ namespace path are the
    // same shape.
    DotnetInsights.NetTrace.Cpu.CpuCategoryClassifier.NativeProfile = true;

    PerfCaptureAnalysis analysis = new PerfCaptureAnalysis();
    analysis.AttachUnwinder(new DwarfUnwinder(unwindSymbols, mappingPass.Architecture));

    // Inline expansion needs the same module lookup the unwinder uses. Enabled
    // whenever the modules are locatable at all - a module built without -g
    // simply contributes no inlined frames.
    if (expandInlineFrames)
    {
        analysis.AttachInlineExpander(unwindSymbols);
    }

    PerfDataFile file = new PerfDataFile();
    analysis.AttachFile(file);
    PerfReadResult result = file.Read(capturePath, analysis);

    if (!result.Success)
    {
        Console.Error.WriteLine("perfParser: " + result.ErrorMessage);
        return 1;
    }

    if (analysis.Tracepoints != null)
    {
        analysis.Tracepoints.Finish();
    }

    analysis.RebaseTimestamps();
    analysis.KernelSymbols = LoadKernelSymbols(kallsymsPath, file);
    analysis.HostSymbols = LoadHostSymbols(hostSymbolsPath, capturePath);
    readTimer.Stop();

    Stopwatch symbolTimer = Stopwatch.StartNew();
    analysis.BuildSymbols(file, symbolPath);
    symbolTimer.Stop();

    Stopwatch exportTimer = Stopwatch.StartNew();

    using (System.IO.FileStream output = new System.IO.FileStream(outputPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
    using (System.Text.Json.Utf8JsonWriter writer = new System.Text.Json.Utf8JsonWriter(output))
    {
        writer.WriteStartObject();

        // Named so the webview can tell the two sources apart without
        // sniffing the shape of the payload. The .NET side already carries a
        // comparable marker for whether its thread sample types are the
        // runtime's own or derived.
        writer.WriteString("source", "perf");

        writer.WritePropertyName("captureInfo");
        writer.WriteStartObject();
        writer.WriteString("filePath", capturePath);
        writer.WriteNumber("fileSizeBytes", file.FileSizeBytes);
        writer.WriteString("hostName", file.HostName);
        writer.WriteString("osRelease", file.OsRelease);
        writer.WriteString("architecture", file.Architecture);
        writer.WriteString("perfVersion", file.PerfVersion);
        writer.WriteString("commandLine", file.CommandLine);
        writer.WriteNumber("sampleCount", analysis.Samples.Count);
        writer.WriteNumber("distinctStackCount", analysis.Stacks.Count);
        writer.WriteNumber("modulesWithSymbols", analysis.ModulesWithSymbols);
        writer.WriteNumber("modulesWithoutSymbols", analysis.ModulesWithoutSymbols);

        // Surfaced rather than inferred: a reader looking at a shallow profile
        // needs to know whether the stacks came from frame pointers or from a
        // DWARF unwind, because the two fail in completely different ways.
        writer.WriteNumber("dwarfUnwoundSampleCount", analysis.DwarfUnwoundSampleCount);
        writer.WriteString("stackSource", analysis.DwarfUnwoundSampleCount > 0 ? "dwarf" : "frame-pointer");
        writer.WriteNumber("addressesWithInlinedFrames", analysis.AddressesWithInlinedFrames);
        writer.WriteNumber("distinctInlinedFunctions", analysis.DistinctInlinedFunctionCount);
        writer.WriteEndObject();

        writer.WritePropertyName("threads");
        writer.WriteStartArray();
        {
            Dictionary<long, long> samplesByThread = new Dictionary<long, long>();
            for (int sampleIndex = 0; sampleIndex < analysis.Samples.Count; ++sampleIndex)
            {
                long threadId = analysis.Samples[sampleIndex].ThreadId;
                long existing;
                samplesByThread.TryGetValue(threadId, out existing);
                samplesByThread[threadId] = existing + 1;
            }

            List<KeyValuePair<long, long>> ranked = new List<KeyValuePair<long, long>>(samplesByThread);
            ranked.Sort(static (left, right) => right.Value.CompareTo(left.Value));

            for (int threadIndex = 0; threadIndex < ranked.Count; ++threadIndex)
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", ranked[threadIndex].Key);

                string comm;
                writer.WriteString("name", analysis.CommByThreadId.TryGetValue((int)ranked[threadIndex].Key, out comm) ? comm : "?");
                writer.WriteNumber("sampleCount", ranked[threadIndex].Value);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();

        // The contention view and the lock timeline are rendered by
        // dotnetInsights' own exporter and webview, unchanged: a futex wait
        // maps onto ContentionEvent exactly - a blocked thread, a duration, a
        // lock identity and a stack. See Tracepoints/TracepointProjector.cs.
        if (analysis.Tracepoints != null && analysis.Tracepoints.Projection.ContentionEvents.Count > 0)
        {
            writer.WritePropertyName("contentionSummary");
            ContentionJsonExporter.Write(
                writer,
                analysis.Tracepoints.Projection.ContentionEvents,
                analysis.Stacks,
                analysis.SymbolTable);
        }

        if (analysis.Tracepoints != null && analysis.Tracepoints.Projection.OffCpuIntervals.Count > 0)
        {
            writer.WritePropertyName("offCpu");
            WriteOffCpu(writer, analysis);
        }

        writer.WritePropertyName("cpuProfile");
        CpuProfileJsonExporter.Write(
            writer,
            analysis.Samples,
            analysis.Stacks,
            analysis.SymbolTable,
            out CpuCategoryBuilder.CategoryTotals[] categoryTotals,
            null,
            analysis.NativeSymbols);

        writer.WriteEndObject();
        writer.Flush();
    }

    exportTimer.Stop();

    Console.Error.WriteLine("Timing: read=" + readTimer.ElapsedMilliseconds.ToString()
        + "ms symbols=" + symbolTimer.ElapsedMilliseconds.ToString()
        + "ms export=" + exportTimer.ElapsedMilliseconds.ToString()
        + "ms samples=" + analysis.Samples.Count.ToString("N0")
        + " dwarfUnwound=" + analysis.DwarfUnwoundSampleCount.ToString("N0")
        + " stacks=" + analysis.Stacks.Count.ToString("N0"));

    return 0;
}

static int RunDemangleCheck(string tablePath)
{
    if (!System.IO.File.Exists(tablePath))
    {
        Console.Error.WriteLine("perfParser: no such file: " + tablePath);
        return 1;
    }

    int total = 0;
    int matched = 0;
    int declined = 0;
    List<string> mismatches = new List<string>();

    foreach (string line in System.IO.File.ReadLines(tablePath))
    {
        int separator = line.IndexOf('\t');
        if (separator <= 0)
        {
            continue;
        }

        string mangled = line.Substring(0, separator);
        string expected = line.Substring(separator + 1);

        ++total;
        string actual = RustDemangler.Demangle(mangled);

        if (actual == expected)
        {
            ++matched;
            continue;
        }

        // Returning the input unchanged is a DECLINE, not a wrong answer - it
        // is the documented behaviour for anything not fully understood, and a
        // raw mangled name is still usable. Counted separately from a name
        // this got wrong, which is the only genuinely bad outcome.
        if (actual == mangled)
        {
            ++declined;
            if (mismatches.Count < 200)
            {
                mismatches.Add("DECLINED  " + mangled + "\n     want  " + expected);
            }

            continue;
        }

        if (mismatches.Count < 200)
        {
            mismatches.Add("WRONG     " + mangled + "\n     want  " + expected + "\n     got   " + actual);
        }
    }

    int wrong = total - matched - declined;

    Console.WriteLine("== Demangler vs rustc-demangle ==");
    Console.WriteLine("  symbols   : " + total.ToString("N0"));
    Console.WriteLine("  exact     : " + matched.ToString("N0") + " (" + (total > 0 ? (100.0 * matched / total) : 0).ToString("F2") + "%)");
    Console.WriteLine("  declined  : " + declined.ToString("N0"));
    Console.WriteLine("  wrong     : " + wrong.ToString("N0"));

    if (mismatches.Count > 0)
    {
        Console.WriteLine();
        for (int mismatchIndex = 0; mismatchIndex < mismatches.Count && mismatchIndex < 25; ++mismatchIndex)
        {
            Console.WriteLine("  " + mismatches[mismatchIndex]);
        }
    }

    return wrong == 0 ? 0 : 2;
}

static int CompareBySampleCountDescending(KeyValuePair<int, long> left, KeyValuePair<int, long> right)
{
    return right.Value.CompareTo(left.Value);
}

// Ties broken by name so the ranking is deterministic across runs - the same
// reason nettraceParser's WriteHotMethods tie-breaks by frame id.
static int CompareBySampleCountDescendingByName(KeyValuePair<string, long> left, KeyValuePair<string, long> right)
{
    int byCount = right.Value.CompareTo(left.Value);
    if (byCount != 0)
    {
        return byCount;
    }

    return string.CompareOrdinal(left.Key, right.Key);
}

static string FormatBytes(long byteCount)
{
    if (byteCount >= 1024L * 1024 * 1024)
    {
        return ((double)byteCount / (1024 * 1024 * 1024)).ToString("F2") + " GB";
    }

    if (byteCount >= 1024 * 1024)
    {
        return ((double)byteCount / (1024 * 1024)).ToString("F2") + " MB";
    }

    return byteCount.ToString("N0") + " bytes";
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Counts what a capture contains without building the analysis model, so that
// the reader can be checked against `perf report` before anything downstream
// of it exists.
// Collects only what pass two needs to know before it can unwind anything:
// the address space, and thread names for display.
internal sealed class MappingCollectorSink : IPerfRecordSink
{
    public readonly List<PerfMapping> Mappings = new List<PerfMapping>();
    public readonly Dictionary<int, string> CommByThreadId = new Dictionary<int, string>();

    public void OnSample(PerfEventAttr attr, ref PerfSample sample)
    {
    }

    public void OnMapping(in PerfMapping mapping)
    {
        this.Mappings.Add(mapping);
    }

    public void OnComm(int processId, int threadId, string comm)
    {
        this.CommByThreadId[threadId] = comm;
    }

    public void OnFork(int processId, int parentProcessId, int threadId, int parentThreadId, ulong time)
    {
        string parentName;
        if (!this.CommByThreadId.ContainsKey(threadId) && this.CommByThreadId.TryGetValue(parentThreadId, out parentName))
        {
            this.CommByThreadId[threadId] = parentName;
        }
    }

    public void OnExit(int processId, int threadId, ulong time)
    {
    }
}

// Pass two: unwinds every sample against an address space that is already
// complete.
internal sealed class UnwindSink : IPerfRecordSink
{
    private readonly PerfSymbolTable symbolTable;
    private readonly Dictionary<int, string> commByThreadId;
    private readonly List<long> frameScratch = new List<long>();

    public long SamplesWithRegisters;
    public long SamplesWithStack;

    public DwarfUnwinder Unwinder { get; private set; }

    public readonly Dictionary<string, long> SamplesByLeaf = new Dictionary<string, long>(StringComparer.Ordinal);
    public readonly List<List<string>> ExampleStacks = new List<List<string>>();
    public readonly List<string> ExampleThreadNames = new List<string>();

    public UnwindSink(PerfSymbolTable symbolTable, DwarfUnwinder unwinder, Dictionary<int, string> commByThreadId)
    {
        this.symbolTable = symbolTable;
        this.Unwinder = unwinder;
        this.commByThreadId = commByThreadId;
    }

    public void OnSample(PerfEventAttr attr, ref PerfSample sample)
    {
        if (attr.IsTracepoint)
        {
            return;
        }

        if (sample.HasUserRegisters)
        {
            ++this.SamplesWithRegisters;
        }

        if (sample.HasUserStack)
        {
            ++this.SamplesWithStack;
        }

        if (!sample.HasUserRegisters || !sample.HasUserStack)
        {
            return;
        }

        if (!this.Unwinder.TryUnwind((int)sample.ProcessId, attr, ref sample, this.frameScratch) || this.frameScratch.Count == 0)
        {
            return;
        }

        string leafName = this.symbolTable.Resolve((int)sample.ProcessId, this.frameScratch[0]);

        long existing;
        this.SamplesByLeaf.TryGetValue(leafName, out existing);
        this.SamplesByLeaf[leafName] = existing + 1;

        if (this.ExampleStacks.Count < 3 && this.frameScratch.Count > 3)
        {
            List<string> resolved = new List<string>();
            for (int frameIndex = 0; frameIndex < this.frameScratch.Count && frameIndex < 12; ++frameIndex)
            {
                resolved.Add(this.symbolTable.Resolve((int)sample.ProcessId, this.frameScratch[frameIndex]));
            }

            string comm;
            this.ExampleThreadNames.Add(this.commByThreadId.TryGetValue((int)sample.ThreadId, out comm) ? comm : "?");
            this.ExampleStacks.Add(resolved);
        }
    }

    public void OnMapping(in PerfMapping mapping)
    {
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

internal sealed class SummarySink : IPerfRecordSink
{
    public long SampleCount;
    public long SamplesWithCallchain;
    public long SamplesWithRaw;
    public long TotalFrames;
    public long ContextMarkerFrames;
    public int MaxCallchainDepth;
    public ulong MinTime = ulong.MaxValue;
    public ulong MaxTime;

    public long MappingCount;
    public long ExecutableMappingCount;
    public long CommRecordCount;
    public long ForkCount;
    public long ExitCount;

    public readonly Dictionary<int, long> SamplesByThreadId = new Dictionary<int, long>();
    public readonly Dictionary<int, string> CommByThreadId = new Dictionary<int, string>();
    public readonly Dictionary<int, int> ProcessIdByThreadId = new Dictionary<int, int>();
    public readonly Dictionary<string, long> SamplesByEventName = new Dictionary<string, long>(StringComparer.Ordinal);
    public readonly Dictionary<string, string> DistinctExecutableModules = new Dictionary<string, string>(StringComparer.Ordinal);
    public readonly List<long[]> FirstCallchains = new List<long[]>();
    public readonly List<PerfMapping> Mappings = new List<PerfMapping>();

    // Leaf frame address -> how many samples had it innermost. Addresses are
    // accumulated rather than resolved here because symbolization cannot begin
    // until the whole file has been read; see Program's note.
    public readonly Dictionary<long, long> SamplesByLeafAddress = new Dictionary<long, long>();

    public int PrimaryProcessId;

    public void OnSample(PerfEventAttr attr, ref PerfSample sample)
    {
        ++this.SampleCount;

        string eventName = attr.Name.Length > 0 ? attr.Name : ("type " + attr.Type.ToString() + " config 0x" + attr.Config.ToString("x"));
        long existingForEvent;
        this.SamplesByEventName.TryGetValue(eventName, out existingForEvent);
        this.SamplesByEventName[eventName] = existingForEvent + 1;

        int threadId = (int)sample.ThreadId;
        long existingForThread;
        this.SamplesByThreadId.TryGetValue(threadId, out existingForThread);
        this.SamplesByThreadId[threadId] = existingForThread + 1;

        this.ProcessIdByThreadId[threadId] = (int)sample.ProcessId;

        if (this.PrimaryProcessId == 0)
        {
            this.PrimaryProcessId = (int)sample.ProcessId;
        }

        if (sample.HasTime)
        {
            if (sample.Time < this.MinTime)
            {
                this.MinTime = sample.Time;
            }

            if (sample.Time > this.MaxTime)
            {
                this.MaxTime = sample.Time;
            }
        }

        if (sample.HasRaw)
        {
            ++this.SamplesWithRaw;
        }

        if (!sample.HasCallchain)
        {
            return;
        }

        ++this.SamplesWithCallchain;
        this.TotalFrames += sample.Callchain.Length;

        if (sample.Callchain.Length > this.MaxCallchainDepth)
        {
            this.MaxCallchainDepth = sample.Callchain.Length;
        }

        for (int frameIndex = 0; frameIndex < sample.Callchain.Length; ++frameIndex)
        {
            if (PerfFormat.IsContextMarker(sample.Callchain[frameIndex]))
            {
                ++this.ContextMarkerFrames;
            }
        }

        // The innermost REAL frame. PERF_CONTEXT_* markers are not addresses -
        // they delimit the kernel and user portions of a chain - so taking
        // entry 0 blindly ranks 0xFFFFFFFFFFFFFF80 as a hot method.
        for (int frameIndex = 0; frameIndex < sample.Callchain.Length; ++frameIndex)
        {
            ulong frame = sample.Callchain[frameIndex];
            if (PerfFormat.IsContextMarker(frame))
            {
                continue;
            }

            long leafAddress = unchecked((long)frame);

            long existingForLeaf;
            this.SamplesByLeafAddress.TryGetValue(leafAddress, out existingForLeaf);
            this.SamplesByLeafAddress[leafAddress] = existingForLeaf + 1;
            break;
        }

        if (this.FirstCallchains.Count < 3)
        {
            long[] copy = new long[sample.Callchain.Length];
            for (int frameIndex = 0; frameIndex < copy.Length; ++frameIndex)
            {
                copy[frameIndex] = unchecked((long)sample.Callchain[frameIndex]);
            }

            this.FirstCallchains.Add(copy);
        }
    }

    public void OnMapping(in PerfMapping mapping)
    {
        ++this.MappingCount;
        this.Mappings.Add(mapping);

        if (!mapping.IsExecutable)
        {
            return;
        }

        ++this.ExecutableMappingCount;

        if (mapping.FileName.Length > 0 && !this.DistinctExecutableModules.ContainsKey(mapping.FileName))
        {
            this.DistinctExecutableModules[mapping.FileName] = mapping.BuildId;
        }
    }

    public void OnComm(int processId, int threadId, string comm)
    {
        ++this.CommRecordCount;
        this.CommByThreadId[threadId] = comm;
        this.ProcessIdByThreadId[threadId] = processId;
    }

    public void OnFork(int processId, int parentProcessId, int threadId, int parentThreadId, ulong time)
    {
        ++this.ForkCount;
        this.ProcessIdByThreadId[threadId] = processId;
    }

    public void OnExit(int processId, int threadId, ulong time)
    {
        ++this.ExitCount;
    }
}
