////////////////////////////////////////////////////////////////////////////////
// Module: Program.cs
//
// Notes:
// Builds a large, RETAINED object graph and then holds it, so that
// `dotnet-gcdump collect` against this process produces a .gcdump with a
// known, controllable object count - the fixture nettraceParser's --gcdump
// mode is verified at scale against.
//
// Why a dedicated generator rather than reusing GcHeapLoadGenerator: that one
// mocks a TTL cache of LARGE payloads to produce a big, fragmented, churning
// heap, which is the right shape for gcHeapAnalyzer's fragmentation report.
// This one wants the opposite shape - an enormous COUNT of small objects, all
// retained, wired into a graph with real depth and sharing - because object
// count, reference count and graph depth are what the .gcdump reader, the
// dominator pass and the root-path walk are actually bounded by. A heap of
// ten 100MB byte arrays and a heap of twelve million 40-byte nodes are the
// same number of gigabytes and completely different problems.
//
// The graph is deliberately not a uniform forest:
//   - shards give the root a realistic fan-out rather than one giant array,
//   - chains give it depth, so the dominator tree is deep rather than flat,
//   - a pool of shared payloads is referenced from many chains, so plenty of
//     objects have multiple predecessors - the case where "retained size" and
//     "own size" genuinely differ, and the one a naive reachability count
//     gets wrong.
//
// Usage:
//   dotnet run -c Release -- [--objects <n>] [--seconds <n>]
//
//   --objects <n>   Approximate live object count to build (default 12,000,000).
//   --seconds <n>   How long to hold the graph before exiting (default 600).
//
// Prints its own PID and the real allocated counts, then holds. Capture with:
//   dotnet-gcdump collect -p <pid> -o <out.gcdump>
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using DotnetInsights.GcDumpObjectGraphGenerator;

int targetObjectCount = 12_000_000;
int holdSeconds = 600;

for (int argIndex = 0; argIndex < args.Length; ++argIndex)
{
    if (args[argIndex] == "--objects" && argIndex + 1 < args.Length)
    {
        if (!int.TryParse(args[argIndex + 1], out targetObjectCount))
        {
            Console.Error.WriteLine($"Invalid --objects value: '{args[argIndex + 1]}'");
            return 1;
        }

        ++argIndex;
    }
    else if (args[argIndex] == "--seconds" && argIndex + 1 < args.Length)
    {
        if (!int.TryParse(args[argIndex + 1], out holdSeconds))
        {
            Console.Error.WriteLine($"Invalid --seconds value: '{args[argIndex + 1]}'");
            return 1;
        }

        ++argIndex;
    }
    else
    {
        Console.Error.WriteLine($"Unrecognized argument: '{args[argIndex]}'");
        return 1;
    }
}

Stopwatch buildStopwatch = Stopwatch.StartNew();

ObjectGraphBuilder builder = new ObjectGraphBuilder(targetObjectCount);
GraphStatistics statistics = builder.Build();

buildStopwatch.Stop();

// The whole graph is rooted here for the process's lifetime - this local is
// what makes every object above reachable from a GC root, which is the entire
// point of the fixture.
GC.KeepAlive(builder);

Console.WriteLine($"pid           : {Environment.ProcessId}");
Console.WriteLine($"objects       : {statistics.ObjectCount:N0}");
Console.WriteLine($"  shards      : {statistics.ShardCount:N0}");
Console.WriteLine($"  nodes       : {statistics.NodeCount:N0}");
Console.WriteLine($"  payloads    : {statistics.PayloadCount:N0}");
Console.WriteLine($"  strings     : {statistics.StringCount:N0}");
Console.WriteLine($"build time    : {buildStopwatch.ElapsedMilliseconds:N0}ms");
Console.WriteLine($"managed heap  : {GC.GetTotalMemory(false) / (1024.0 * 1024.0):N0} MB");
Console.WriteLine($"working set   : {Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0):N0} MB");
Console.WriteLine();
Console.WriteLine($"Holding for {holdSeconds}s. Capture with:");
Console.WriteLine($"  dotnet-gcdump collect -p {Environment.ProcessId} -o <out.gcdump>");
Console.Out.Flush();

Thread.Sleep(TimeSpan.FromSeconds(holdSeconds));

GC.KeepAlive(builder);
return 0;
