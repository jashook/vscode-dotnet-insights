////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpConvertCommand.cs
//
// Notes:
// `nettraceParser --gcdump-from-trace <trace.nettrace> -o <out.gcdump>`
//
// Turns a `.nettrace` captured by `dotnet-trace` with the heap-snapshot
// keyword into a real `.gcdump`, with `dotnet-gcdump` involved nowhere in the
// pipeline - neither its `collect` nor its `convert`. See
// HeapDumpEventDecoder.cs for why that matters (dotnet-gcdump silently
// truncates at 10,000,000 nodes) and README.md for the end-to-end recipe.
//
// The capture side is:
//   dotnet-trace collect -p <pid> \
//     --providers Microsoft-Windows-DotNETRuntime:0x1980001:5
//
// 0x1980001 is GCHeapSnapshot (GC | GCHeapCollect | GCHeapDump |
// GCHeapAndTypeNames | Type); enabling it is what makes the runtime induce a
// blocking gen2 GC and emit the bulk node/edge/type/root events this reads.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpConvertCommand
{
    public static int Run(string[] args, string tracePath)
    {
        string outputPath = ResolveOutputPath(args, tracePath);

        if (!File.Exists(tracePath))
        {
            Console.Error.WriteLine($"File not found: {tracePath}");
            return 1;
        }

        Stopwatch totalStopwatch = Stopwatch.StartNew();

        // The decoder streams the capture itself (three passes, none of them
        // retaining it) - see HeapDumpEventDecoder.cs. Nothing here reads the
        // trace into memory first, which is the whole point.
        Stopwatch decodeStopwatch = Stopwatch.StartNew();
        HeapDumpDecodeResult decodeResult = HeapDumpEventDecoder.Decode(tracePath);
        decodeStopwatch.Stop();

        if (!decodeResult.Succeeded)
        {
            Console.Error.WriteLine(decodeResult.ErrorMessage);
            return 1;
        }

        HeapGraph graph = decodeResult.Graph;

        GcDumpMetadata metadata = new GcDumpMetadata();
        metadata.ProcessId = decodeResult.ProcessId;
        metadata.TimeCollectedTicks = decodeResult.SyncTimeUtcTicks;
        metadata.CollectionLog = $"Converted from {Path.GetFileName(tracePath)} by nettraceParser.";
        metadata.AverageCountMultiplier = 1.0f;
        metadata.AverageSizeMultiplier = 1.0f;

        Stopwatch writeStopwatch = Stopwatch.StartNew();
        GcDumpWriter.WriteToFile(outputPath, graph, metadata);
        writeStopwatch.Stop();

        totalStopwatch.Stop();

        Console.WriteLine($"Wrote {outputPath}");
        Console.WriteLine($"  {graph.TotalSize,15:N0}  GC Heap bytes");
        Console.WriteLine($"  {graph.NodeCount - 1,15:N0}  GC Heap objects");
        Console.WriteLine($"  {graph.EdgeCount,15:N0}  References");
        Console.WriteLine($"  {graph.TypeCount,15:N0}  Types");

        // Memory is reported alongside the timings because this path's cost is
        // dominated by it, not by CPU - it holds an entire heap's worth of
        // nodes and edges at once, and "why did this get killed on a big
        // capture" is a question the timings alone cannot answer.
        //
        // Working set at exit, not PEAK working set: Process.PeakWorkingSet64
        // returns 0 on macOS, so reporting it would be quietly meaningless on
        // one of the three platforms this ships to. For a real peak, run the
        // process under `/usr/bin/time -l`.
        Console.Error.WriteLine(
            $"Timing: total={totalStopwatch.ElapsedMilliseconds}ms " +
            $"decode={decodeStopwatch.ElapsedMilliseconds}ms " +
            $"write={writeStopwatch.ElapsedMilliseconds}ms " +
            $"rss={Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)}MB " +
            $"managedHeap={GC.GetTotalMemory(false) / (1024 * 1024)}MB " +
            $"nodes={graph.NodeCount} edges={graph.EdgeCount} types={graph.TypeCount} " +
            $"gcPause={GC.GetTotalPauseDuration().TotalMilliseconds:F1}ms " +
            $"gcCounts=[{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}]");

        return 0;
    }

    // Defaults to the input path with a .gcdump extension, matching
    // dotnet-gcdump convert's own default.
    private static string ResolveOutputPath(string[] args, string tracePath)
    {
        int outputArgIndex = Array.IndexOf(args, "-o");

        if (outputArgIndex < 0)
        {
            outputArgIndex = Array.IndexOf(args, "--output");
        }

        if (outputArgIndex >= 0 && outputArgIndex + 1 < args.Length)
        {
            return args[outputArgIndex + 1];
        }

        return Path.ChangeExtension(tracePath, ".gcdump");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
