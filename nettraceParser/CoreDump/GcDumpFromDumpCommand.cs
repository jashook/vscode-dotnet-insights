////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpFromDumpCommand.cs
//
// Notes:
// `nettraceParser --gcdump-from-dump <core.dmp> [-o <out.gcdump>] [--json <out.json>]`
//
// Turns a process core dump into the same .gcdump the other two heap paths
// produce, so the analysis and the webview are shared. See
// CoreDumpHeapGraphBuilder.cs for why this source exists at all (short version:
// it is the only one of the three that stays correct on a process under load).
//
// The capture side is:
//   dotnet-dump collect -p <pid> --type Heap -o heap.dmp
//
// --type Heap rather than Full keeps the file to roughly the heap's own size.
// The process is suspended while createdump writes it, so the graph, the types
// and the roots all come from one instant - which is the entire point.
//
// WHERE TO RUN IT. ClrMD needs the DAC that matches the runtime the dump came
// from. On a host with that runtime installed it is found automatically;
// otherwise pass --dac. A Linux dump is therefore most easily converted on
// Linux (nettraceParser ships a linux-x64 build), and the .gcdump - or better,
// the --json output, which is a few hundred KB for any heap size - is what
// travels back to the machine running VS Code.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.CoreDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.GcDump;
using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpFromDumpCommand
{
    public static int Run(string[] args, string dumpPath)
    {
        if (!File.Exists(dumpPath))
        {
            Console.Error.WriteLine($"File not found: {dumpPath}");
            return 1;
        }

        CoreDumpBuildOptions options = new CoreDumpBuildOptions();
        options.DacPath = ValueOfArgument(args, "--dac");
        options.SkipStackRoots = Array.IndexOf(args, "--skip-stack-roots") >= 0 || ShouldDefaultToSkippingStackRoots(dumpPath);

        string jsonOutputPath = ValueOfArgument(args, "--json");
        string explicitGcDumpPath = ExplicitOutputPath(args);

        // Write a .gcdump when one was asked for, or when nothing else was:
        // `--json` alone means the caller wants the analysis, and a
        // heap-sized .gcdump appearing next to their dump because they did not
        // pass -o is a surprise, not a convenience. This is the path the
        // extension takes (see DotnetInsightsGcDumpEditor.ts).
        string gcDumpOutputPath = explicitGcDumpPath;

        if (gcDumpOutputPath == null && jsonOutputPath == null)
        {
            gcDumpOutputPath = Path.ChangeExtension(dumpPath, ".gcdump");
        }

        // Progress goes to stderr, the same channel and format the .nettrace
        // and .gcdump paths already use, so the extension's loading bar needs
        // no per-source branching (see NettraceProgress.ts).
        if (jsonOutputPath != null)
        {
            ProgressReporter.Enable();
            ProgressReporter.Warmup();
        }

        Stopwatch totalStopwatch = Stopwatch.StartNew();

        Stopwatch readStopwatch = Stopwatch.StartNew();
        CoreDumpBuildResult buildResult = CoreDumpHeapGraphBuilder.Build(dumpPath, options);
        readStopwatch.Stop();

        if (!buildResult.Succeeded)
        {
            Console.Error.WriteLine(buildResult.ErrorMessage);
            return 1;
        }

        HeapGraph graph = buildResult.Graph;

        Stopwatch writeStopwatch = Stopwatch.StartNew();

        if (gcDumpOutputPath != null)
        {
            GcDumpWriter.WriteToFile(gcDumpOutputPath, graph, buildResult.Metadata);
            Console.WriteLine($"Wrote {gcDumpOutputPath}");
        }

        writeStopwatch.Stop();

        Console.WriteLine($"  {graph.TotalSize,15:N0}  GC Heap bytes");
        Console.WriteLine($"  {buildResult.ObjectCount,15:N0}  GC Heap objects");
        Console.WriteLine($"  {graph.EdgeCount,15:N0}  References");
        Console.WriteLine($"  {graph.TypeCount,15:N0}  Types");
        Console.WriteLine($"  {buildResult.RootCount,15:N0}  GC roots");

        // Said out loud rather than left to be inferred from a thin retention
        // tree: without stack roots, objects held only by a running frame come
        // out unrooted, and a reader comparing this against a dotnet-gcdump of
        // the same process deserves to know which of the two is missing them.
        if (!buildResult.StackRootsIncluded)
        {
            Console.WriteLine();
            Console.WriteLine("  NOTE: thread stack roots were not read (see --skip-stack-roots). Handles, statics and the");
            Console.WriteLine("        finalizer queue are all present; objects held ONLY by a live stack frame will show as unrooted.");
        }

        if (buildResult.UnresolvedReferenceCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  NOTE: {buildResult.UnresolvedReferenceCount:N0} references did not resolve to a live object.");
        }

        if (jsonOutputPath != null)
        {
            // Analysing straight from the graph rather than re-reading the file
            // just written: same result, one less full parse of a file that can
            // run to hundreds of megabytes.
            GcDumpAnalysis analysis = GcDumpAnalysisBuilder.Build(graph);

            GcDumpFile file = new GcDumpFile();
            file.Graph = graph;
            file.Metadata = buildResult.Metadata;

            GcDumpJsonExporter.WriteToFile(jsonOutputPath, file, analysis);
            Console.WriteLine($"Wrote {jsonOutputPath}");
        }

        totalStopwatch.Stop();

        Console.Error.WriteLine(
            $"Timing: total={totalStopwatch.ElapsedMilliseconds}ms " +
            $"read={readStopwatch.ElapsedMilliseconds}ms " +
            $"write={writeStopwatch.ElapsedMilliseconds}ms " +
            $"rss={Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)}MB " +
            $"managedHeap={GC.GetTotalMemory(false) / (1024 * 1024)}MB " +
            $"objects={buildResult.ObjectCount} edges={graph.EdgeCount} types={graph.TypeCount} roots={buildResult.RootCount} " +
            $"stackRoots={(buildResult.StackRootsIncluded ? "yes" : "no")} runtime={buildResult.RuntimeVersion} " +
            $"gcPause={GC.GetTotalPauseDuration().TotalMilliseconds:F1}ms " +
            $"gcCounts=[{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}]");

        return 0;
    }

    // The DAC's stack unwind segfaults on a macOS Mach-O core dump (see
    // CoreDumpHeapGraphBuilder's header - verified against a real .NET 10 dump
    // on ClrMD 3.1 and 4.1). A SIGSEGV takes the process down with no error to
    // report, so the only place this can be handled is before the walk starts:
    // default to skipping stack roots for a dump that came from macOS, and let
    // every other platform read them.
    //
    // The check is on the DUMP, not on the host - a Linux dump converted on a
    // Mac still has readable stacks, and a macOS dump does not become readable
    // by being converted on Linux. A Mach-O core starts with the 64-bit Mach-O
    // magic 0xFEEDFACF.
    private static bool ShouldDefaultToSkippingStackRoots(string dumpPath)
    {
        try
        {
            using (FileStream dumpStream = new FileStream(dumpPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Span<byte> magic = stackalloc byte[4];

                if (dumpStream.Read(magic) != magic.Length)
                {
                    return false;
                }

                uint value = (uint)(magic[0] | (magic[1] << 8) | (magic[2] << 16) | (magic[3] << 24));
                return value == 0xFEEDFACF || value == 0xFEEDFACE;
            }
        }
        catch (IOException)
        {
            // Unreadable here means unreadable in the builder a moment later,
            // where it is reported properly - no need to fail twice.
            return false;
        }
    }

    private static string ValueOfArgument(string[] args, string name)
    {
        int argIndex = Array.IndexOf(args, name);

        if (argIndex >= 0 && argIndex + 1 < args.Length)
        {
            return args[argIndex + 1];
        }

        return null;
    }

    private static string ExplicitOutputPath(string[] args)
    {
        string explicitPath = ValueOfArgument(args, "-o");

        if (explicitPath == null)
        {
            explicitPath = ValueOfArgument(args, "--output");
        }

        return explicitPath;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.CoreDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
