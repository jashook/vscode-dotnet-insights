////////////////////////////////////////////////////////////////////////////////
// Module: PerfSymbolTable.cs
//
// Notes:
// Turns a sampled instruction pointer into a name. The perf.data counterpart
// of nettraceParser/Universal/UniversalSymbolTable.cs, and deliberately a
// separate type rather than a reuse of it: the two are fed by genuinely
// different models. A collect-linux capture carries its own ProcessSymbol
// rows and per-module metadata; a perf.data capture carries mappings and build
// ids and NOTHING ELSE - every name in it comes from a file on disk.
//
// Three things that are easy to get wrong here, all of which produce confident
// wrong answers rather than failures:
//
//   - The address translation. See ElfLoadSegments for the formula and the
//     measurement behind it. `ip - mapping.Start` names the wrong function.
//
//   - Mappings are PER PROCESS. A machine-wide capture contains many processes
//     whose address ranges overlap freely, so a single global address list
//     resolves a frame against whichever process happened to sort first.
//
//   - A module file must be matched by BUILD ID, not by path. The path
//     recorded in the capture is a path inside the traced machine's
//     filesystem - frequently a container's - and a file of that name on the
//     analysing machine is a different build far more often than it is the
//     right one. Keying on build id makes a mismatch impossible rather than
//     unlikely, and needs no invalidation rule: a different build is a
//     different key.
//
// Names are demangled Rust-first, then C++. Rust's v0 scheme is not a superset
// or subset of Itanium - `_R` and `_Z` are disjoint prefixes - so the order is
// about which check is cheaper, not about precedence.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Symbols {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using DotnetInsights.NetTrace.Symbols;
using DotnetInsights.Perf.PerfData;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class PerfSymbolTable
{
    private struct MappingRange
    {
        public long StartAddress;
        public long EndAddress;
        public long FileOffset;
        public string FileName;
        public string BuildId;
        public bool IsKernel;
    }

    private sealed class ModuleSymbols
    {
        public ElfSymbolFile Symbols;
        public ElfLoadSegments LoadSegments;
        public bool LookupAttempted;
        public string ResolvedFilePath;
        public string FailureReason;
    }

    // Process id -> that process's executable mappings, sorted by address.
    private readonly Dictionary<int, List<MappingRange>> mappingsByProcessId = new Dictionary<int, List<MappingRange>>();

    // The kernel image, kept OUT of the per-process table. perf records the
    // kernel mapping against its own pid (not the sampled process's), so a
    // lookup keyed on the sample's pid never finds it - and every kernel frame
    // then falls through to "[unknown] 0xffff8000...", which on aarch64 is
    // most of the syscall time in any capture that does I/O. The kernel is
    // mapped at the same addresses in every process, so one shared list is
    // both correct and what perf itself does.
    private readonly List<MappingRange> kernelMappings = new List<MappingRange>();

    // Build id (or, for a module without one, its path) -> the module's symbol
    // file and load segments, loaded at most once.
    private readonly Dictionary<string, ModuleSymbols> modulesByKey = new Dictionary<string, ModuleSymbols>(StringComparer.Ordinal);

    // Memoizes whole resolutions. A real capture resolves the same few thousand
    // distinct addresses across millions of samples and every frame of every
    // stack; without this the ELF lookup and the demangler would run once per
    // frame rather than once per distinct address.
    private readonly Dictionary<long, string> resolvedNameByAddress = new Dictionary<long, string>();

    private readonly List<string> buildIdSearchRoots = new List<string>();

    // Optional; see KallsymsTable. Without it every kernel frame renders as a
    // bare address, which matters most for off-CPU stacks - those are captured
    // inside the kernel by construction.
    private KallsymsTable kernelSymbols;

    public void SetKernelSymbols(KallsymsTable table)
    {
        this.kernelSymbols = table;
    }

    public bool HasKernelSymbols => this.kernelSymbols != null && !this.kernelSymbols.IsEmpty;

    public int MappingCount { get; private set; }

    public int ModulesWithSymbols { get; private set; }

    public int ModulesWithoutSymbols { get; private set; }

    public readonly List<string> UnresolvedModules = new List<string>();

    public PerfSymbolTable(string extraSymbolPath)
    {
        this.buildIdSearchRoots.AddRange(BuildSearchRoots(extraSymbolPath));
    }

    // The conventional build-id layouts, in priority order.
    // /usr/lib/debug/.build-id is what `apt install <pkg>-dbgsym` populates and
    // ~/.debug is what debuginfod clients cache into, so a machine that already
    // has symbols for a module needs no configuration at all.
    public static List<string> BuildSearchRoots(string extraSymbolPath)
    {
        List<string> roots = new List<string>();

        if (!string.IsNullOrEmpty(extraSymbolPath))
        {
            roots.Add(extraSymbolPath);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            roots.Add(Path.Combine(home, ".debug", ".build-id"));
        }

        roots.Add("/usr/lib/debug/.build-id");
        return roots;
    }

    public void AddMapping(in PerfMapping mapping, string buildId)
    {
        // A non-executable mapping cannot contain a sampled instruction, and
        // including it only creates opportunities to attribute a frame to the
        // wrong module - the heap and every data segment would otherwise be
        // candidates.
        if (!mapping.IsExecutable || mapping.FileName.Length == 0)
        {
            return;
        }

        MappingRange range = new MappingRange();
        range.StartAddress = mapping.StartAddress;
        range.EndAddress = mapping.StartAddress + mapping.Length;
        range.FileOffset = mapping.FileOffset;
        range.BuildId = buildId ?? string.Empty;
        range.FileName = mapping.FileName;

        // perf names the kernel image mapping `[kernel.kallsyms]_stext`. Its
        // symbols live in /proc/kallsyms on the recording machine and are not
        // in the capture at all, so kernel frames are identified rather than
        // resolved.
        range.IsKernel = mapping.FileName.StartsWith("[kernel", StringComparison.Ordinal);

        if (range.IsKernel)
        {
            this.kernelMappings.Add(range);
            ++this.MappingCount;
            return;
        }

        List<MappingRange> ranges;
        if (!this.mappingsByProcessId.TryGetValue(mapping.ProcessId, out ranges))
        {
            ranges = new List<MappingRange>();
            this.mappingsByProcessId[mapping.ProcessId] = ranges;
        }

        ranges.Add(range);
        ++this.MappingCount;
    }

    public void FinishBuilding()
    {
        foreach (KeyValuePair<int, List<MappingRange>> entry in this.mappingsByProcessId)
        {
            entry.Value.Sort(CompareByStartAddress);
        }

        this.kernelMappings.Sort(CompareByStartAddress);
    }

    private static int CompareByStartAddress(MappingRange left, MappingRange right)
    {
        return left.StartAddress.CompareTo(right.StartAddress);
    }

    // What the DWARF unwinder needs for an address: which file describes the
    // code there, and the bias that turns a runtime address into the ELF
    // virtual address `.eh_frame` is expressed in.
    //
    // `moduleKey` identifies the module for caching - the build id where there
    // is one - so two mappings of the same shared object share one parsed
    // .eh_frame rather than reparsing megabytes per segment.
    public bool TryGetUnwindContext(int processId, long instructionPointer, out string modulePath, out string moduleKey, out long elfVirtualAddressBias)
    {
        modulePath = null;
        moduleKey = null;
        elfVirtualAddressBias = 0;

        MappingRange range;
        if (!this.TryFindMapping(processId, instructionPointer, out range) || range.IsKernel)
        {
            return false;
        }

        ModuleSymbols module = this.GetOrLoadModule(in range);
        if (module == null || module.ResolvedFilePath == null || module.LoadSegments == null)
        {
            return false;
        }

        long segmentBias;
        module.LoadSegments.TryGetBiasForFileOffset(range.FileOffset, out segmentBias);

        modulePath = module.ResolvedFilePath;
        moduleKey = range.BuildId.Length > 0 ? range.BuildId : range.FileName;

        // elfVirtualAddress = ip + bias. Expanded from
        // (ip - mappingStart) + mappingFileOffset + (p_vaddr - p_offset).
        elfVirtualAddressBias = segmentBias + range.FileOffset - range.StartAddress;
        return true;
    }

    public string Resolve(int processId, long instructionPointer)
    {
        // Keyed by address alone rather than by (pid, address). Safe for a
        // single-process capture and wrong in principle for a machine-wide
        // one; revisit when the machine-wide path is actually wired up, since
        // the fix costs a wider key on a dictionary this hot.
        string cached;
        if (this.resolvedNameByAddress.TryGetValue(instructionPointer, out cached))
        {
            return cached;
        }

        string resolved = this.ResolveUncached(processId, instructionPointer);
        this.resolvedNameByAddress[instructionPointer] = resolved;
        return resolved;
    }

    private string ResolveUncached(int processId, long instructionPointer)
    {
        MappingRange range;
        bool found = this.TryFindMapping(processId, instructionPointer, out range);

        if (!found)
        {
            // The kernel is mapped identically into every process, so it is
            // the fallback for anything the sampled process's own mappings do
            // not cover.
            found = TryFindInRanges(this.kernelMappings, instructionPointer, out range);
        }

        if (!found)
        {
            return "[unknown] 0x" + instructionPointer.ToString("x");
        }

        if (range.IsKernel)
        {
            if (this.kernelSymbols != null)
            {
                string kernelName;
                long kernelOffset;
                if (this.kernelSymbols.TryResolve(instructionPointer, out kernelName, out kernelOffset))
                {
                    return kernelName;
                }
            }

            return "[kernel] 0x" + instructionPointer.ToString("x");
        }

        ModuleSymbols module = this.GetOrLoadModule(in range);

        string moduleDisplayName = Path.GetFileName(range.FileName);
        if (moduleDisplayName.Length == 0)
        {
            moduleDisplayName = range.FileName;
        }

        if (module == null || module.Symbols == null || module.LoadSegments == null)
        {
            // The module+offset form is a real, groupable identity - every
            // sample in the same function shares it only by accident, but every
            // sample in the same MODULE is at least attributed to the right
            // package. It is also the shape the CPU view uses to recognise an
            // unresolved frame.
            long moduleOffset = instructionPointer - range.StartAddress + range.FileOffset;
            return moduleDisplayName + "+0x" + moduleOffset.ToString("x");
        }

        long bias;
        module.LoadSegments.TryGetBiasForFileOffset(range.FileOffset, out bias);

        long elfVirtualAddress = (instructionPointer - range.StartAddress) + range.FileOffset + bias;

        string symbolName;
        long offsetIntoFunction;
        if (!module.Symbols.TryResolve(elfVirtualAddress, out symbolName, out offsetIntoFunction))
        {
            long moduleOffset = instructionPointer - range.StartAddress + range.FileOffset;
            return moduleDisplayName + "+0x" + moduleOffset.ToString("x");
        }

        return Demangle(symbolName);
    }

    // The name a frame should be RANKED and DISPLAYED by - the bare function,
    // with no +offset. Appending the offset splits one hot function into a row
    // per sampled instruction, which is the same mistake the .NET side's
    // name-keyed frame ids exist to prevent.
    public static string Demangle(string symbolName)
    {
        string demangled = RustDemangler.Demangle(symbolName);
        if (!ReferenceEquals(demangled, symbolName) && demangled != symbolName)
        {
            return demangled;
        }

        if (NativeSymbolDemangler.IsMangled(symbolName))
        {
            return NativeSymbolDemangler.Demangle(symbolName);
        }

        return symbolName;
    }

    private bool TryFindMapping(int processId, long instructionPointer, out MappingRange found)
    {
        found = default(MappingRange);

        List<MappingRange> ranges;
        if (!this.mappingsByProcessId.TryGetValue(processId, out ranges))
        {
            return false;
        }

        return TryFindInRanges(ranges, instructionPointer, out found);
    }

    private static bool TryFindInRanges(List<MappingRange> ranges, long instructionPointer, out MappingRange found)
    {
        found = default(MappingRange);

        // Last range starting at or before the address, then a containment
        // check - mappings can abut but a hole between them is real.
        int low = 0;
        int high = ranges.Count - 1;
        int candidate = -1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);

            if (ranges[middle].StartAddress <= instructionPointer)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0 || instructionPointer >= ranges[candidate].EndAddress)
        {
            return false;
        }

        found = ranges[candidate];
        return true;
    }

    private ModuleSymbols GetOrLoadModule(in MappingRange range)
    {
        string key = range.BuildId.Length > 0 ? range.BuildId : range.FileName;

        ModuleSymbols module;
        if (this.modulesByKey.TryGetValue(key, out module))
        {
            return module;
        }

        module = new ModuleSymbols();
        module.LookupAttempted = true;
        this.modulesByKey[key] = module;

        string modulePath = this.FindModuleFile(range.BuildId, range.FileName);
        if (modulePath == null)
        {
            module.FailureReason = "no file found for build id " + (range.BuildId.Length > 0 ? range.BuildId : "(none)");
            ++this.ModulesWithoutSymbols;
            this.UnresolvedModules.Add(range.FileName + "  " + module.FailureReason);
            return module;
        }

        module.ResolvedFilePath = modulePath;

        string segmentError;
        module.LoadSegments = ElfLoadSegments.TryLoad(modulePath, out segmentError);

        string symbolError;
        module.Symbols = ElfSymbolFile.TryLoad(modulePath, out symbolError);

        if (module.Symbols == null || module.LoadSegments == null)
        {
            module.FailureReason = symbolError ?? segmentError ?? "unreadable";
            ++this.ModulesWithoutSymbols;
            this.UnresolvedModules.Add(range.FileName + "  " + module.FailureReason);
            return module;
        }

        ++this.ModulesWithSymbols;
        return module;
    }

    private string FindModuleFile(string buildId, string fileName)
    {
        return FindModuleFile(this.buildIdSearchRoots, buildId, fileName);
    }

    public static string FindModuleFile(IReadOnlyList<string> buildIdSearchRoots, string buildId, string fileName)
    {
        if (buildId != null && buildId.Length >= 3)
        {
            string prefix = buildId.Substring(0, 2);
            string remainder = buildId.Substring(2);

            for (int rootIndex = 0; rootIndex < buildIdSearchRoots.Count; ++rootIndex)
            {
                string root = buildIdSearchRoots[rootIndex];
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                string candidate = Path.Combine(root, prefix, remainder + ".debug");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                // debuginfod caches the stripped binary under the same key
                // without the .debug suffix.
                candidate = Path.Combine(root, prefix, remainder);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // The literal path, last. This only ever hits when the capture was
        // taken on the machine now reading it, which is the case this whole
        // project exists to NOT require.
        if (File.Exists(fileName))
        {
            return fileName;
        }

        // perf appends " (deleted)" to the recorded path when the mapped file
        // was unlinked while the process still had it mapped - routine for a
        // container image layer or a binary replaced by a deploy. It is not
        // part of the name.
        const string DeletedSuffix = " (deleted)";
        if (fileName != null && fileName.EndsWith(DeletedSuffix, StringComparison.Ordinal))
        {
            string undeleted = fileName.Substring(0, fileName.Length - DeletedSuffix.Length);
            if (File.Exists(undeleted))
            {
                return undeleted;
            }
        }

        return null;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
