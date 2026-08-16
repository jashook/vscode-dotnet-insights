////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpCommand.cs
//
// Notes:
// Drives the `--gcdump` mode end to end: read the file, run the analyses, and
// write the output the VS Code extension consumes.
//
//   nettraceParser --gcdump <file.gcdump>
//     Human-readable summary on stdout. The manual verification harness,
//     mirroring what the plain .nettrace CLI path prints - and deliberately
//     shaped like `dotnet-gcdump report`'s own output so the two can be
//     eyeballed side by side.
//
//   nettraceParser --gcdump <file.gcdump> --json <out>
//     What the extension invokes. Emits PROGRESS lines on stderr, exactly as
//     the .nettrace --json path does (see Progress/ProgressReporter.cs).
//
// NO --binary HERE, deliberately. The .nettrace path writes a DNIBIN binary
// container (Binary/BinaryCaptureFormat.cs) because its payloads are millions
// of per-sample values that JSON cannot carry cheaply. This mode's output is
// aggregated to the type level before it is written, so the same 237MB /
// 10-million-object heap dump that produces a 30M-edge graph in memory leaves
// here as **53KB of JSON**; a type-rich 445KB dump produces 829KB. There is
// nothing for a binary container to save, and adding one would mean a second
// wire format to keep in sync with the webview for no measured benefit.
//
// Progress phases and their shares of the bar live in GcDumpProgressPlan.cs.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Diagnostics;
using System.IO;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpCommand
{
    public static int Run(string[] args, string gcDumpFilePath)
    {
        int jsonArgIndex = Array.IndexOf(args, "--json");
        bool isJsonMode = jsonArgIndex >= 0 && jsonArgIndex + 1 < args.Length;
        string jsonOutputPath = isJsonMode ? args[jsonArgIndex + 1] : null;

        if (isJsonMode)
        {
            ProgressReporter.Enable();
            ProgressReporter.Warmup();
        }

        Stopwatch totalStopwatch = Stopwatch.StartNew();

        ProgressRange readRange = GcDumpProgressPlan.PlanRead();
        ProgressReporter.BeginPhase("Reading heap snapshot", readRange.Start, readRange.End);

        Stopwatch readStopwatch = Stopwatch.StartNew();
        GcDumpReadResult readResult = GcDumpReader.Read(gcDumpFilePath);
        readStopwatch.Stop();

        ProgressReporter.CompletePhase();

        if (!readResult.Succeeded)
        {
            Console.Error.WriteLine(readResult.ErrorMessage);
            return 1;
        }

        GcDumpFile file = readResult.File;
        HeapGraph graph = file.Graph;

        Stopwatch analysisStopwatch = Stopwatch.StartNew();
        GcDumpAnalysis analysis = GcDumpAnalysisBuilder.Build(graph);
        analysisStopwatch.Stop();

        Stopwatch exportStopwatch = Stopwatch.StartNew();

        if (isJsonMode)
        {
            GcDumpJsonExporter.WriteToFile(jsonOutputPath, file, analysis);
        }
        else
        {
            GcDumpTextReport.Write(Console.Out, file, analysis);
        }

        exportStopwatch.Stop();
        totalStopwatch.Stop();

        // Same shape as the .nettrace path's own trailing diagnostic line, on
        // the same channel, so "why was this run slow" is answerable from the
        // extension's output channel without attaching anything.
        Console.Error.WriteLine(
            $"Timing: total={totalStopwatch.ElapsedMilliseconds}ms " +
            $"read={readStopwatch.ElapsedMilliseconds}ms " +
            $"analysis={analysisStopwatch.ElapsedMilliseconds}ms(" +
            $"census={analysis.CensusMSec}ms,dominators={analysis.DominatorMSec}ms," +
            $"roots={analysis.RootPathMSec}ms,refs={analysis.ReferenceGraphMSec}ms) " +
            $"export={exportStopwatch.ElapsedMilliseconds}ms " +
            $"nodes={graph.NodeCount} edges={graph.EdgeCount} types={graph.TypeCount} " +
            $"gcPause={GC.GetTotalPauseDuration().TotalMilliseconds:F1}ms " +
            $"gcCounts=[{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}]");

        return 0;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
