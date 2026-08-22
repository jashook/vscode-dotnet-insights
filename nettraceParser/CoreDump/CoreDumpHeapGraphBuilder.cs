////////////////////////////////////////////////////////////////////////////////
// Module: CoreDumpHeapGraphBuilder.cs
//
// Notes:
// Builds a HeapGraph from a PROCESS CORE DUMP, using ClrMD
// (Microsoft.Diagnostics.Runtime) as the reader. Everything downstream - the
// dominator pass, the type census, the root-path trie, the .gcdump writer and
// the whole webview - is shared with the other two heap sources and does not
// know where the graph came from.
//
// WHY THIS EXISTS, AND WHY IT IS NOT JUST ANOTHER CAPTURE MODE. The event
// paths (`--gcdump` and `--gcdump-from-trace`) both ultimately depend on the
// runtime walking its own heap and streaming it out while the process runs on.
// Measured against a real production service (see CLAUDE.md's "heap dumps on a
// busy process"), that walk is only trustworthy on a QUIET heap: on a service
// under load, the dump referenced 1,798,821 addresses it never described, 26,006
// of 41,919 roots pointed at objects that were never emitted, and 1.5% of the
// heap came out reachable - making every retained size and every retention path
// in it worthless. That reproduced on a churning test heap with `dotnet-trace`
// AND with `dotnet-gcdump collect`, at both verbosity levels and with a 1GB
// buffer, so it is not a capture-flag problem to be tuned around.
//
// A core dump has no such failure mode by construction: `createdump` suspends
// the process and writes the actual memory image, so the object graph, the type
// information and the root set are all one consistent instant. That is the
// whole reason to add a second reader rather than keep tuning the first.
//
// WHAT CLRMD IS AND IS NOT DOING HERE. It is the supported API over the DAC -
// the same component `dotnet-dump analyze` and SOS use to read a dump - so this
// is not a second hand-rolled parser the way the .nettrace reader is. Writing
// one would mean reimplementing the DAC protocol against a private, versioned
// contract, which is a different proposition entirely from decoding a
// documented event stream.
//
// THREE WALKS, EXACT ALLOCATIONS. Same discipline as HeapDumpEventDecoder for
// the same reason (a 12M-object heap is memory-bound, and a List<T> reaching
// its size by doubling holds the old array alive while allocating one twice as
// large):
//
//   Walk 0  Count objects, and nothing else. Cheap - it never asks an object
//           for its references, which is the expensive half of a walk - and it
//           is what lets the address map be sized exactly. AddressToIndexMap
//           does not resize BY DESIGN, so a guessed capacity does not merely
//           run slower, it spins forever once the table fills.
//   Walk 1  Assign every object an index, and count its outgoing references
//           into a chunked list (not a doubling one).
//   Walk 2  Fill the node arrays and write every edge straight into its final
//           CSR slot, resolving each target through the address map.
//
// More than one walk because CSR needs every child count before any child can
// be written. The alternative - buffering 8-byte target ADDRESSES during a
// single walk - is exactly the 286MB-at-35M-edges cost the trace path already
// measured and removed.
//
// ROOTS ARE THE POINT, so they get more care here than the trace path can give
// them. A .gcdump's roots hang off a synthetic node; real `dotnet-gcdump` output
// groups them one level further, under categories like "[static var ...]", and
// the trace path loses that grouping entirely (every root hangs directly off
// "[.NET Roots]", so every retention path ends by saying nothing). ClrMD reports
// a ClrRootKind per root, so this path rebuilds those categories - a retention
// path that ends "held by [strong handle]" or "held by [thread stack]" tells the
// reader which kind of leak they are looking at.
//
// STACK ROOTS AND THE macOS CAVEAT. Stack roots need the DAC to unwind each
// thread. On a macOS Mach-O core dump that unwind SEGFAULTS the process inside
// the DAC (verified against a real .NET 10 dump with ClrMD 3.1 and 4.1 alike -
// handles, the finalizer queue, objects and references all enumerate fine, and
// only the stack walk dies), and a native crash cannot be caught from managed
// code. So stack roots are opt-out via SkipStackRoots and the caller is expected
// to set it for macOS dumps. Losing them costs less than it sounds: handles
// cover statics and every GC handle, which is what a leak investigation is
// about - on the verification dump, handle and finalizer roots alone reached
// 100,003 of 100,003 objects in the retained graph under test (77.5% of the
// whole heap, the remainder being genuinely stack-rooted or runtime-internal).
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.CoreDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using DotnetInsights.NetTrace.GcDump;
using DotnetInsights.NetTrace.Progress;

using Microsoft.Diagnostics.Runtime;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class CoreDumpBuildOptions
{
    // Path to the matching libmscordaccore for the runtime that produced the
    // dump. Left null, ClrMD locates one itself (next to the dump, or from a
    // locally installed runtime of the same version). A cross-OS dump - a Linux
    // core read on another platform - generally needs this pointed at the DAC
    // that shipped with the target's runtime.
    public string DacPath;

    // See this file's header: the DAC's stack unwind crashes the process on a
    // macOS core dump, and a SIGSEGV is not catchable, so this has to be a
    // decision made BEFORE the walk rather than an error handled during it.
    public bool SkipStackRoots;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class CoreDumpBuildResult
{
    public HeapGraph Graph;
    public GcDumpMetadata Metadata;
    public string ErrorMessage;

    public int ObjectCount;
    public int RootCount;
    public bool StackRootsIncluded;

    // References whose target was not a live object in the dump. Expected to be
    // zero: unlike the event paths, every reference here is read out of the same
    // frozen image the objects were, so a non-zero count is worth surfacing
    // rather than silently dropping.
    public long UnresolvedReferenceCount;

    public string RuntimeVersion;

    public bool Succeeded
    {
        get
        {
            return this.ErrorMessage == null;
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class CoreDumpHeapGraphBuilder
{
    // Root categories, in the order their synthetic nodes are appended. One
    // node per category, each holding the roots of that kind - mirroring the
    // shape real dotnet-gcdump output has, so the webview's retention paths
    // read the same way against either source.
    private const int StrongHandleCategory = 0;
    private const int PinnedHandleCategory = 1;
    private const int OtherHandleCategory = 2;
    private const int FinalizerQueueCategory = 3;
    private const int ThreadStackCategory = 4;
    private const int RootCategoryCount = 5;

    private static readonly string[] RootCategoryNames = new string[]
    {
        "[strong handle]",
        "[pinned handle]",
        "[other handle]",
        "[finalizer queue]",
        "[thread stack]"
    };

    public static CoreDumpBuildResult Build(string dumpPath, CoreDumpBuildOptions options)
    {
        CoreDumpBuildResult result = new CoreDumpBuildResult();

        DataTarget dataTarget;

        // ClrMD reports an unreadable or non-dump file by throwing, and the
        // caller wants an error message on the stack rather than a stack trace
        // on the console - this is the boundary where that conversion happens.
        try
        {
            dataTarget = DataTarget.LoadDump(dumpPath);
        }
        catch (Exception loadError)
        {
            result.ErrorMessage = $"Could not read '{dumpPath}' as a process dump: {loadError.Message}";
            return result;
        }

        using (dataTarget)
        {
            if (dataTarget.ClrVersions.Length == 0)
            {
                result.ErrorMessage = $"'{dumpPath}' contains no .NET runtime. A dump of a native process has no managed heap to read.";
                return result;
            }

            ClrInfo clrInfo = dataTarget.ClrVersions[0];
            result.RuntimeVersion = clrInfo.Version.ToString();

            ClrRuntime runtime;

            try
            {
                runtime = options.DacPath != null
                    ? clrInfo.CreateRuntime(options.DacPath)
                    : clrInfo.CreateRuntime();
            }
            catch (Exception runtimeError)
            {
                result.ErrorMessage = DescribeAttachFailure(dataTarget, clrInfo, dumpPath, runtimeError);
                return result;
            }

            using (runtime)
            {
                ClrHeap heap = runtime.Heap;

                if (!heap.CanWalkHeap)
                {
                    result.ErrorMessage =
                        "The heap in this dump cannot be walked. This normally means the dump was taken while a GC was in " +
                        "progress, or that it is a minidump without heap memory - collect with `dotnet-dump collect --type Heap` or `--type Full`.";
                    return result;
                }

                BuildGraph(runtime, heap, options, result);
                result.Metadata = BuildMetadata(dataTarget, clrInfo, dumpPath);
                result.Metadata.StackRootsOmitted = !result.StackRootsIncluded;
                return result;
            }
        }
    }

    private static void BuildGraph(ClrRuntime runtime, ClrHeap heap, CoreDumpBuildOptions options, CoreDumpBuildResult result)
    {
        // The four phases below subdivide the SAME [0, 34) slice of the bar that
        // GcDumpProgressPlan gives the .gcdump file read, so everything after
        // this - dominators, census, root paths, references, export - lands on
        // exactly the percentages it already does for the other heap source and
        // needs no per-source branching.
        //
        // The 34 itself is inherited rather than measured: this path's cost
        // profile against a multi-gigabyte dump has not been calibrated the way
        // the .nettrace plan's constants were, and reading memory through the
        // DAC may well deserve a larger share than reading a file does. The
        // `Timing:` line reports read= and the phase breakdown precisely so
        // recalibrating is one run rather than new scaffolding.
        double buildRangeEnd = GcDumpProgressPlan.PlanRead().End;

        // Roots first, because their count is part of the edge total the CSR
        // array has to be sized for, and they are cheap (thousands, against an
        // object walk that runs to millions).
        ProgressReporter.BeginPhase("Reading GC roots", 0, buildRangeEnd * 0.09);
        List<RootReference> roots = CollectRoots(runtime, heap, options, result);
        ProgressReporter.CompletePhase();

        // AddressToIndexMap is sized ONCE and never resizes - that is the whole
        // point of it (see its own header), and seeding it with a guess instead
        // of a count does not degrade, it hangs: a full table under linear
        // probing spins forever on the next insert. So the object count comes
        // first, from a walk that enumerates objects WITHOUT touching their
        // references, which is the cheap half of a heap walk.
        ProgressReporter.BeginPhase("Sizing the heap", buildRangeEnd * 0.09, buildRangeEnd * 0.30);
        int totalObjectCount = 0;

        foreach (ClrObject countedObject in heap.EnumerateObjects())
        {
            if (IsRealObject(countedObject))
            {
                ++totalObjectCount;
            }
        }

        ProgressReporter.CompletePhase();

        ProgressReporter.BeginPhase("Walking the heap", buildRangeEnd * 0.30, buildRangeEnd * 0.60);
        AddressToIndexMap indexByAddress = new AddressToIndexMap(totalObjectCount);
        ChunkedIntList childCounts = new ChunkedIntList();

        int objectCount = 0;
        long referenceCount = 0;

        foreach (ClrObject clrObject in heap.EnumerateObjects())
        {
            if (!IsRealObject(clrObject))
            {
                continue;
            }

            indexByAddress.GetOrAdd(clrObject.Address, objectCount);

            int childCount = 0;
            foreach (ClrObject reference in clrObject.EnumerateReferences())
            {
                if (reference.Address != 0)
                {
                    ++childCount;
                }
            }

            childCounts.Add(childCount);
            referenceCount += childCount;
            ++objectCount;

            if ((objectCount & ProgressReporter.IndexProgressMask) == 0)
            {
                ProgressReporter.ReportFraction((double)objectCount / totalObjectCount);
            }
        }

        ProgressReporter.CompletePhase();

        result.ObjectCount = objectCount;

        // Resolve each root to the node it names, dropping any that do not land
        // on a live object, and deduplicate: one category holding the same
        // object twice would double-count it in every branch share the UI shows.
        int rootNodeIndex = objectCount + RootCategoryCount;
        int nodeCount = rootNodeIndex + 1;

        List<int>[] rootTargetsByCategory = new List<int>[RootCategoryCount];
        for (int categoryIndex = 0; categoryIndex < RootCategoryCount; ++categoryIndex)
        {
            rootTargetsByCategory[categoryIndex] = new List<int>();
        }

        HashSet<long> seenRoots = new HashSet<long>();
        int rootEdgeCount = 0;

        for (int rootIndex = 0; rootIndex < roots.Count; ++rootIndex)
        {
            RootReference root = roots[rootIndex];

            int targetNodeIndex;
            if (!indexByAddress.TryGetValue(root.Address, out targetNodeIndex))
            {
                continue;
            }

            // Category and node index packed into one key rather than a tuple
            // in a HashSet<(int, int)> - same reasoning as the hot-loop key rule
            // this codebase already follows elsewhere.
            long dedupeKey = ((long)root.Category << 32) | (uint)targetNodeIndex;

            if (!seenRoots.Add(dedupeKey))
            {
                continue;
            }

            rootTargetsByCategory[root.Category].Add(targetNodeIndex);
            ++rootEdgeCount;
        }

        result.RootCount = rootEdgeCount;

        // Every category node is a child of the root node, whether or not it
        // holds anything: a category that came back empty is information (no
        // stack roots were read, say), and hiding it would make an absent
        // category indistinguishable from one that was never looked for.
        long totalEdgeCount = referenceCount + rootEdgeCount + RootCategoryCount;

        HeapGraph graph = new HeapGraph();
        graph.NodeCount = nodeCount;
        graph.RootNodeIndex = rootNodeIndex;
        graph.NodeAddresses = new ulong[nodeCount];
        graph.NodeTypeIndex = new int[nodeCount];
        graph.NodeSize = new int[nodeCount];
        graph.ChildStart = new int[nodeCount + 1];
        graph.ChildTarget = new int[totalEdgeCount];

        // Prefix-sum the counts from walk 1 into CSR start offsets. The category
        // and root nodes are appended after the objects, so their offsets are
        // filled once the object edges are placed.
        int runningEdgeOffset = 0;
        for (int nodeIndex = 0; nodeIndex < objectCount; ++nodeIndex)
        {
            graph.ChildStart[nodeIndex] = runningEdgeOffset;
            runningEdgeOffset += childCounts[nodeIndex];
        }

        TypeTable typeTable = new TypeTable();

        ProgressReporter.BeginPhase("Reading objects and references", buildRangeEnd * 0.60, buildRangeEnd);
        int walkedObjectCount = 0;

        foreach (ClrObject clrObject in heap.EnumerateObjects())
        {
            if (!IsRealObject(clrObject))
            {
                continue;
            }

            int nodeIndex;
            if (!indexByAddress.TryGetValue(clrObject.Address, out nodeIndex))
            {
                // Only reachable if the two walks disagreed about what a valid
                // object is, which would be a ClrMD bug rather than a dump
                // problem - counted rather than crashed on.
                continue;
            }

            graph.NodeAddresses[nodeIndex] = clrObject.Address;
            graph.NodeTypeIndex[nodeIndex] = typeTable.IndexOf(clrObject.Type);
            graph.NodeSize[nodeIndex] = ClampSize(clrObject.Size);

            int writeCursor = graph.ChildStart[nodeIndex];
            int limit = writeCursor + childCounts[nodeIndex];

            foreach (ClrObject reference in clrObject.EnumerateReferences())
            {
                if (reference.Address == 0)
                {
                    continue;
                }

                // The count from walk 1 bounds this: a reference that appeared
                // only in walk 2 would otherwise write into the next node's
                // slice.
                if (writeCursor >= limit)
                {
                    ++result.UnresolvedReferenceCount;
                    break;
                }

                int targetNodeIndex;
                if (!indexByAddress.TryGetValue(reference.Address, out targetNodeIndex))
                {
                    ++result.UnresolvedReferenceCount;
                    continue;
                }

                graph.ChildTarget[writeCursor] = targetNodeIndex;
                ++writeCursor;
            }

            // A reference that vanished between the walks would leave a hole in
            // this node's slice pointing at node 0. Filling the remainder with
            // self-references keeps the CSR dense and the graph acyclic-safe
            // (a self-edge changes no dominator or reachability answer) rather
            // than inventing an edge to whatever object happens to be first.
            while (writeCursor < limit)
            {
                graph.ChildTarget[writeCursor] = nodeIndex;
                ++writeCursor;
                ++result.UnresolvedReferenceCount;
            }

            ++walkedObjectCount;

            if ((walkedObjectCount & ProgressReporter.IndexProgressMask) == 0)
            {
                ProgressReporter.ReportFraction((double)walkedObjectCount / objectCount);
            }
        }

        ProgressReporter.CompletePhase();

        // Category nodes, then the root node, each with their own edges.
        int categoryEdgeCursor = (int)referenceCount;

        for (int categoryIndex = 0; categoryIndex < RootCategoryCount; ++categoryIndex)
        {
            int categoryNodeIndex = objectCount + categoryIndex;
            List<int> targets = rootTargetsByCategory[categoryIndex];

            graph.ChildStart[categoryNodeIndex] = categoryEdgeCursor;
            graph.NodeTypeIndex[categoryNodeIndex] = typeTable.IndexOfSyntheticType(RootCategoryNames[categoryIndex]);
            graph.NodeSize[categoryNodeIndex] = 0;
            graph.NodeAddresses[categoryNodeIndex] = 0;

            for (int targetIndex = 0; targetIndex < targets.Count; ++targetIndex)
            {
                graph.ChildTarget[categoryEdgeCursor] = targets[targetIndex];
                ++categoryEdgeCursor;
            }
        }

        graph.ChildStart[rootNodeIndex] = categoryEdgeCursor;
        graph.NodeTypeIndex[rootNodeIndex] = typeTable.IndexOfSyntheticType("[.NET Roots]");
        graph.NodeSize[rootNodeIndex] = 0;
        graph.NodeAddresses[rootNodeIndex] = 0;

        for (int categoryIndex = 0; categoryIndex < RootCategoryCount; ++categoryIndex)
        {
            graph.ChildTarget[categoryEdgeCursor] = objectCount + categoryIndex;
            ++categoryEdgeCursor;
        }

        graph.ChildStart[nodeCount] = categoryEdgeCursor;

        long totalSize = 0;
        for (int nodeIndex = 0; nodeIndex < objectCount; ++nodeIndex)
        {
            totalSize += graph.NodeSize[nodeIndex];
        }

        graph.TotalSize = totalSize;
        typeTable.Materialize(graph);

        result.Graph = graph;
    }

    // What counts as an object, applied identically in all three walks - if
    // they disagreed, the count sizing the address map would not match the
    // objects inserted into it.
    //
    // "Free" is the GC's own placeholder for a gap in a segment, not an object:
    // ClrMD enumerates them (they are how it walks a segment linearly), and
    // dotnet-gcdump excludes them. Keeping them would put a phantom "Free" type
    // in the census holding megabytes - 86,626 objects and 2.8MB of a 387K
    // object verification dump - and inflate the object count against every
    // other tool's answer for the same heap.
    // Why attaching failed, in terms the reader can act on. The two causes look
    // identical from the exception alone and have completely different fixes:
    //
    //   - The DAC for that runtime is not on this machine. Fixable here, with
    //     --dac or by installing the runtime.
    //   - The dump is from another OS. NOT fixable here at all: ClrMD refuses
    //     ("Debugging a 'LINUX' crash is not supported on 'OSX'") because the
    //     DAC is native code for the target platform. Telling someone to pass
    //     --dac in that case sends them looking for a file that cannot help.
    //
    // Both messages name the exact runtime the dump wants, which is otherwise a
    // separate investigation: it is the version stamped into the coreclr module
    // path the dump itself carries.
    private static string DescribeAttachFailure(DataTarget dataTarget, ClrInfo clrInfo, string dumpPath, Exception runtimeError)
    {
        string dumpPlatform = dataTarget.DataReader.TargetPlatform.ToString();
        string dumpArchitecture = dataTarget.DataReader.Architecture.ToString();
        string hostPlatform = HostPlatformName();
        string runtimePath = clrInfo.ModuleInfo != null ? clrInfo.ModuleInfo.FileName : null;

        StringBuilder message = new StringBuilder();
        message.AppendLine($"Could not read the managed heap in '{System.IO.Path.GetFileName(dumpPath)}': {runtimeError.Message}");
        message.AppendLine();
        message.AppendLine($"  This dump : {dumpPlatform} / {dumpArchitecture}");
        message.AppendLine($"  This host : {hostPlatform} / {RuntimeInformation.OSArchitecture}");

        if (runtimePath != null)
        {
            message.AppendLine($"  Needs     : the runtime at {runtimePath}");
        }

        message.AppendLine();

        bool isCrossPlatform = !string.Equals(dumpPlatform, hostPlatform, StringComparison.OrdinalIgnoreCase);

        if (isCrossPlatform)
        {
            // Deliberately not offering --dac here. A DAC is native code for
            // the platform it debugs, so there is no file that makes this work
            // on this host.
            message.AppendLine("A dump can only be read on the platform it came from (Windows can additionally read Linux dumps; macOS cannot).");
            message.AppendLine("Convert it where it belongs and bring back the result, which is far smaller than the dump:");
            message.AppendLine();
            message.AppendLine($"  nettraceParser --gcdump-from-dump <dump> --json heap.json    # a few hundred KB, opens in VS Code");
            message.AppendLine($"  nettraceParser --gcdump-from-dump <dump> -o heap.gcdump      # a full .gcdump, if you want one");
        }
        else
        {
            message.AppendLine("This usually means the matching DAC is not installed here. Either install that runtime version, or pass");
            message.AppendLine("--dac <path to libmscordaccore> taken from a machine that has it.");
        }

        return message.ToString().TrimEnd();
    }

    private static string HostPlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "WINDOWS";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "OSX";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "LINUX";
        }

        return RuntimeInformation.OSDescription;
    }

    private static bool IsRealObject(ClrObject clrObject)
    {
        return clrObject.IsValid && !clrObject.IsFree;
    }

    // A .gcdump stores a node's size in 32 bits. Nothing on a real heap is
    // larger than int.MaxValue (the CLR's own array limit is below it), but the
    // size comes out of a dump that could be damaged, and a negative size would
    // corrupt every total downstream rather than fail here.
    private static int ClampSize(ulong size)
    {
        return size > int.MaxValue ? int.MaxValue : (int)size;
    }

    private struct RootReference
    {
        public ulong Address;
        public int Category;
    }

    private static List<RootReference> CollectRoots(ClrRuntime runtime, ClrHeap heap, CoreDumpBuildOptions options, CoreDumpBuildResult result)
    {
        List<RootReference> roots = new List<RootReference>();

        foreach (ClrHandle handle in runtime.EnumerateHandles())
        {
            // A weak handle is not a root - it is exactly the thing that does
            // NOT keep its target alive - so including it would report objects
            // as retained by something that would let them go.
            if (!handle.IsStrong)
            {
                continue;
            }

            if (handle.Object.Address == 0)
            {
                continue;
            }

            RootReference root = new RootReference();
            root.Address = handle.Object.Address;
            root.Category = CategoryForHandle(handle);
            roots.Add(root);
        }

        foreach (ClrObject finalizable in heap.EnumerateFinalizableObjects())
        {
            if (finalizable.Address == 0)
            {
                continue;
            }

            RootReference root = new RootReference();
            root.Address = finalizable.Address;
            root.Category = FinalizerQueueCategory;
            roots.Add(root);
        }

        if (options.SkipStackRoots)
        {
            result.StackRootsIncluded = false;
            return roots;
        }

        foreach (ClrThread thread in runtime.Threads)
        {
            foreach (ClrRoot stackRoot in thread.EnumerateStackRoots())
            {
                if (stackRoot.Object.Address == 0)
                {
                    continue;
                }

                RootReference root = new RootReference();
                root.Address = stackRoot.Object.Address;
                root.Category = ThreadStackCategory;
                roots.Add(root);
            }
        }

        result.StackRootsIncluded = true;
        return roots;
    }

    private static int CategoryForHandle(ClrHandle handle)
    {
        if (handle.HandleKind == ClrHandleKind.Pinned || handle.HandleKind == ClrHandleKind.AsyncPinned)
        {
            return PinnedHandleCategory;
        }

        if (handle.HandleKind == ClrHandleKind.Strong)
        {
            return StrongHandleCategory;
        }

        return OtherHandleCategory;
    }

    private static GcDumpMetadata BuildMetadata(DataTarget dataTarget, ClrInfo clrInfo, string dumpPath)
    {
        GcDumpMetadata metadata = new GcDumpMetadata();
        metadata.ProcessId = (int)dataTarget.DataReader.ProcessId;

        // NOT DataReader.DisplayName: for a core dump that is the dump's own
        // file path, which the webview would then render as its heading in
        // place of a process name (GcDumpRenderer.ts's describeSource prefers a
        // process name when one is present, and falls back to the file name
        // when it is not - a path is neither). The executable's own name is the
        // first module the dump lists.
        metadata.ProcessName = ExecutableNameOf(dataTarget);
        metadata.TimeCollectedTicks = System.IO.File.GetLastWriteTimeUtc(dumpPath).Ticks;
        metadata.CreationTool = "nettraceParser";
        metadata.CollectionLog = $"Converted from the core dump {System.IO.Path.GetFileName(dumpPath)} (runtime {clrInfo.Version}) by nettraceParser.";
        metadata.AverageCountMultiplier = 1.0f;
        metadata.AverageSizeMultiplier = 1.0f;
        return metadata;
    }

    private static string ExecutableNameOf(DataTarget dataTarget)
    {
        foreach (ModuleInfo module in dataTarget.EnumerateModules())
        {
            if (!string.IsNullOrEmpty(module.FileName))
            {
                return System.IO.Path.GetFileName(module.FileName);
            }
        }

        // Empty rather than guessed - the renderer already treats an absent
        // process name as "use the file name instead".
        return "";
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Type table over ClrType identities. Keyed by method table address, which is
// the runtime's own identity for a type - two ClrType instances for the same
// type compare unequal by reference but always carry the same method table.
internal sealed class TypeTable
{
    private readonly Dictionary<ulong, int> indexByMethodTable = new Dictionary<ulong, int>();
    private readonly List<string> names = new List<string>();
    private readonly List<string> moduleNames = new List<string>();

    // Objects arrive in runs of the same type (the heap walk follows allocation
    // order within a segment), so remembering the last answer turns most of the
    // per-object lookups into one comparison - the same one-entry cache the
    // trace path's own type table measured at ~3.6% of a conversion.
    private ulong lastMethodTable;
    private int lastIndex = -1;

    public TypeTable()
    {
        // Index 0 is UNDEFINED, matching both a real .gcdump's own type table
        // and HeapGraph.UndefinedTypeIndex.
        this.names.Add("UNDEFINED");
        this.moduleNames.Add("");
    }

    public int IndexOf(ClrType type)
    {
        if (type == null)
        {
            return HeapGraph.UndefinedTypeIndex;
        }

        ulong methodTable = type.MethodTable;

        if (this.lastIndex >= 0 && methodTable == this.lastMethodTable)
        {
            return this.lastIndex;
        }

        int index;
        if (!this.indexByMethodTable.TryGetValue(methodTable, out index))
        {
            index = this.names.Count;
            this.indexByMethodTable.Add(methodTable, index);
            this.names.Add(type.Name ?? "UNDEFINED");

            // Module names are available here and are NOT on the trace path
            // (BulkType identifies a module by id, and resolving it needs
            // rundown events) - the UI already renders one when it is there.
            this.moduleNames.Add(type.Module != null ? (type.Module.Name ?? "") : "");
        }

        this.lastMethodTable = methodTable;
        this.lastIndex = index;
        return index;
    }

    // Root categories and the root node itself are not types on the heap, so
    // they are appended without a method table to key them by.
    public int IndexOfSyntheticType(string name)
    {
        int index = this.names.Count;
        this.names.Add(name);
        this.moduleNames.Add("");
        return index;
    }

    public void Materialize(HeapGraph graph)
    {
        graph.TypeCount = this.names.Count;
        graph.TypeNames = this.names.ToArray();
        graph.TypeModuleNames = this.moduleNames.ToArray();

        // Zero throughout, which is what tells a .gcdump reader that each
        // node's size is stored per node rather than taken from its type - the
        // only correct choice when arrays and strings vary per instance.
        graph.TypeSizes = new int[this.names.Count];
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Append-only int list that never copies. A List<int> reaching 12M entries by
// doubling holds its old array alive while allocating one twice as large, which
// is the specific cost the whole two-walk design exists to avoid - so the one
// structure that has to grow during walk 1 grows in fixed blocks instead.
internal sealed class ChunkedIntList
{
    private const int BlockShift = 20;
    private const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;

    private readonly List<int[]> blocks = new List<int[]>();
    private int count;

    public int Count
    {
        get
        {
            return this.count;
        }
    }

    public void Add(int value)
    {
        int blockIndex = this.count >> BlockShift;

        if (blockIndex == this.blocks.Count)
        {
            this.blocks.Add(new int[BlockSize]);
        }

        this.blocks[blockIndex][this.count & BlockMask] = value;
        ++this.count;
    }

    public int this[int index]
    {
        get
        {
            return this.blocks[index >> BlockShift][index & BlockMask];
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.CoreDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
