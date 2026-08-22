////////////////////////////////////////////////////////////////////////////////
// Module: NativeSymbolResolution.cs
//
// Notes:
// Decides WHICH modules in a `dotnet-trace collect-linux` capture are worth
// fetching symbols for, fetches them through the SymbolStore, and attaches
// them to the capture's UniversalSymbolTable.
//
// The policy is the whole point of this file, so it is stated rather than
// buried: a real capture describes hundreds of module mappings (561 on the
// reference capture) and ships symbols for a small minority of them (42). The
// rest are overwhelmingly managed assemblies and libraries no sampled stack
// ever touches. Fetching everything would mean hundreds of megabytes of
// downloads to name functions nobody executed, and libcoreclr.so.dbg alone is
// 138MB - so this fetches by DEMAND, ordered by how many stack frames actually
// land in each module, and stops at both a share floor and a count cap.
//
// On the reference capture that selects a handful of modules and, in
// particular, libcoreclr.so - which owns 5.4% of all CPU samples, more than
// any single managed method, and every frame of which was previously an
// unreadable hex offset.
//
// Everything here degrades rather than fails. A capture must still open with
// no network, no cache directory, a symbol server that 404s, or a symbol file
// that turns out to be unreadable; in every one of those cases the affected
// frames simply keep the module+offset form they had before.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Symbols {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Universal;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class NativeSymbolResolution
{
    // The ONLY bound on how much this will download. A module qualifies by
    // appearing in the sampled stacks at all; the ranking then decides which
    // ones fit under this cap.
    //
    // There used to be a minimum-share floor as well (0.1% of unnamed frames)
    // and it was removed after it measurably lost symbols: the counts feeding
    // it come from a strided sample (see UniversalSymbolTable.
    // CountFramesByModule, where the exact pass cost 4.8 seconds), and a
    // module sitting near the floor lands on either side of it depending on
    // which stacks the stride happened to hit. On the reference capture that
    // dropped libSystem.Native.so - and with it 274 real symbols - between two
    // runs over the same file. A floor and a sampled input do not belong
    // together; the cap alone bounds the work and is immune to sampling noise,
    // because a module has to be genuinely absent from ~100,000 sampled stacks
    // to fall out of a list this short.
    public const int MaximumModulesToFetch = 12;

    public struct ModuleRequest
    {
        public long MetadataId;
        public string FileName;
        public string BuildId;
        public long FrameCount;
    }

    public struct Result
    {
        public int ModulesConsidered;
        public int ModulesFetched;
        public int ModulesFailed;
        public long DownloadedBytes;
        public int SymbolsLoaded;

        // How long ranking the modules took, separate from fetching them. The
        // two have completely different characters - the ranking is a local
        // pass over every decoded stack and always costs the same, while a
        // fetch is a network download the first time and free forever after -
        // so a single number would hide whichever one was the problem.
        public long SelectMs;
    }

    // Ranks every module that has unnamed frames, most-sampled first, and
    // keeps the ones worth fetching. Separated from Run so the policy can be
    // tested without a symbol store or a network.
    public static List<ModuleRequest> SelectModules(UniversalSymbolTable table, StackTable stacks)
    {
        List<ModuleRequest> selected = new List<ModuleRequest>();

        if (table == null || stacks == null)
        {
            return selected;
        }

        Dictionary<long, long> framesByModule = table.CountFramesByModule(stacks);

        if (framesByModule.Count == 0)
        {
            return selected;
        }

        List<ModuleRequest> candidates = new List<ModuleRequest>();

        foreach (KeyValuePair<long, long> entry in framesByModule)
        {
            UniversalSymbolTable.ModuleDebugInfo info;

            if (!table.TryGetModuleDebugInfo(entry.Key, out info))
            {
                continue;
            }

            // No build id means no way to ask a symbol server for the right
            // build, and asking for the wrong one is worse than not asking.
            if (string.IsNullOrEmpty(info.BuildId))
            {
                continue;
            }

            // Nothing serves symbols for runtime-generated code, kernel-
            // provided mappings or managed assemblies, so a lookup could only
            // ever be a wasted round trip - and on a slow server, a wasted
            // timeout.
            if (ModuleSymbolSourceMap.Classify(info.FileName) == ModuleSymbolSource.NotFetchable)
            {
                continue;
            }

            ModuleRequest request = new ModuleRequest();
            request.MetadataId = entry.Key;
            request.FileName = info.FileName;
            request.BuildId = info.BuildId;
            request.FrameCount = entry.Value;

            candidates.Add(request);
        }

        candidates.Sort(CompareByFrameCountDescending);

        for (int candidateIndex = 0; candidateIndex < candidates.Count && selected.Count < MaximumModulesToFetch; ++candidateIndex)
        {
            selected.Add(candidates[candidateIndex]);
        }

        return selected;
    }

    // onModuleStarting is called with a human-readable module name before each
    // fetch, so a caller driving a progress bar can name what it is waiting on
    // - a first-time libcoreclr.so download is 138MB and needs to say so.
    public static Result Run(UniversalSymbolTable table, StackTable stacks, SymbolStore store, Action<ModuleRequest> onModuleStarting = null)
    {
        Result result = new Result();

        if (table == null || stacks == null || store == null)
        {
            return result;
        }

        System.Diagnostics.Stopwatch selectStopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<ModuleRequest> requests = SelectModules(table, stacks);
        result.SelectMs = selectStopwatch.ElapsedMilliseconds;
        result.ModulesConsidered = requests.Count;

        for (int requestIndex = 0; requestIndex < requests.Count; ++requestIndex)
        {
            ModuleRequest request = requests[requestIndex];

            onModuleStarting?.Invoke(request);

            string status;
            ElfSymbolFile symbols = store.TryGetSymbols(request.BuildId, request.FileName, out status);

            if (symbols == null)
            {
                ++result.ModulesFailed;
                continue;
            }

            table.AddModuleSymbols(request.MetadataId, symbols);
            ++result.ModulesFetched;
            result.SymbolsLoaded += symbols.SymbolCount;
        }

        result.DownloadedBytes = store.DownloadedBytes;
        return result;
    }

    private static int CompareByFrameCountDescending(ModuleRequest left, ModuleRequest right)
    {
        int byCount = right.FrameCount.CompareTo(left.FrameCount);

        if (byCount != 0)
        {
            return byCount;
        }

        // Ties broken by build id so the selection is deterministic across
        // runs - the same reason WriteHotMethods has a frame-id tie-break.
        return string.CompareOrdinal(left.BuildId, right.BuildId);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Symbols)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
