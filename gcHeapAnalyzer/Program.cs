////////////////////////////////////////////////////////////////////////////////
// Module: Program.cs
//
// Notes:
// Usage:
//   gcHeapAnalyzer --pid <pid> [--output <path>]
//   gcHeapAnalyzer --pid <pid> --watch --output <path> [--interval-seconds <n>]
//
//   --pid <pid>              PID of the target .NET process (required)
//   --output <path>          Write JSON to this file; omit to write to stdout
//   --watch                  Keep attaching on a loop, re-walking the heap
//                             each cycle and overwriting --output with the
//                             latest capture (see LiveWatcher.cs). A walk
//                             can take seconds on large heaps, so a GC has
//                             likely already happened by the next cycle -
//                             we don't try to detect that, just always
//                             patch in the newest snapshot. Requires
//                             --output.
//   --interval-seconds <n>   Minimum seconds between watch-cycle starts
//                             (default 5). If a single walk takes longer than
//                             this, the next cycle starts immediately after.
//   --stop-file <path>       In --watch mode, exit cleanly as soon as this
//                             file exists (checked every cycle and while
//                             waiting between cycles; deleted once seen).
//                             A second, OS-signal-independent way to ask
//                             for a graceful stop.
//   --duration-seconds <n>   In --watch mode, stop automatically after this
//                             many seconds (default: run until Ctrl+C,
//                             SIGTERM, or --stop-file). Use to align with a
//                             fixed-duration dotnet-trace capture run
//                             against the same process.
//
// The target process is suspended for the duration of each heap walk.
// Analysis status messages are written to stderr so they do not corrupt
// JSON written to stdout.
//
// Elevated privileges may be required on some platforms:
//   macOS:  SIP may block attachment to processes owned by other users.
//   Linux:  ptrace_scope may need to be set to 0 or the tool run as root.
//   Windows: Standard user can attach to processes with the same identity.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.IO;

using DotnetInsights.GcHeapAnalyzer;

int pid = -1;
string outputPath = null;
bool sample = false;
bool watch = false;
int intervalSeconds = 5;
string stopFilePath = null;
int? durationSeconds = null;

for (int argIndex = 0; argIndex < args.Length; ++argIndex)
{
    if (args[argIndex] == "--pid" && argIndex + 1 < args.Length)
    {
        if (!int.TryParse(args[argIndex + 1], out pid))
        {
            Console.Error.WriteLine($"Invalid PID: '{args[argIndex + 1]}'");
            return 1;
        }

        ++argIndex;
    }
    else if (args[argIndex] == "--output" && argIndex + 1 < args.Length)
    {
        outputPath = args[argIndex + 1];
        ++argIndex;
    }
    else if (args[argIndex] == "--sample")
    {
        sample = true;
    }
    else if (args[argIndex] == "--watch")
    {
        watch = true;
    }
    else if (args[argIndex] == "--interval-seconds" && argIndex + 1 < args.Length)
    {
        if (!int.TryParse(args[argIndex + 1], out intervalSeconds) || intervalSeconds < 0)
        {
            Console.Error.WriteLine($"Invalid --interval-seconds: '{args[argIndex + 1]}'");
            return 1;
        }

        ++argIndex;
    }
    else if (args[argIndex] == "--stop-file" && argIndex + 1 < args.Length)
    {
        stopFilePath = args[argIndex + 1];
        ++argIndex;
    }
    else if (args[argIndex] == "--duration-seconds" && argIndex + 1 < args.Length)
    {
        int parsedDurationSeconds;

        if (!int.TryParse(args[argIndex + 1], out parsedDurationSeconds) || parsedDurationSeconds <= 0)
        {
            Console.Error.WriteLine($"Invalid --duration-seconds: '{args[argIndex + 1]}'");
            return 1;
        }

        durationSeconds = parsedDurationSeconds;
        ++argIndex;
    }
}

if (watch)
{
    if (pid <= 0)
    {
        Console.Error.WriteLine("--watch requires --pid <pid>");
        return 1;
    }

    if (outputPath == null)
    {
        Console.Error.WriteLine("--watch requires --output <path> (the file patched on each change)");
        return 1;
    }

    return LiveWatcher.Watch(pid, outputPath, intervalSeconds, stopFilePath, durationSeconds);
}

if (!sample && pid <= 0)
{
    Console.Error.WriteLine("Usage: gcHeapAnalyzer --pid <pid> [--output <path>]");
    Console.Error.WriteLine("       gcHeapAnalyzer --pid <pid> --watch --output <path> [--interval-seconds <n>]");
    Console.Error.WriteLine("       gcHeapAnalyzer --sample [--output <path>]");
    return 1;
}

FragmentationReport report;
string errorMessage = null;
bool success;

if (sample)
{
    report = SampleReportGenerator.Generate();
    success = true;
}
else
{
    success = HeapAnalyzer.TryAnalyze(pid, out report, out errorMessage);
}

if (!success)
{
    Console.Error.WriteLine($"Analysis failed: {errorMessage}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("If this is a permissions error, check:");
    Console.Error.WriteLine("  macOS:   run as root, or verify SIP is not blocking cross-process attachment");
    Console.Error.WriteLine("  Linux:   sudo sysctl kernel.yama.ptrace_scope=0, or run as root");
    Console.Error.WriteLine("  Windows: ensure you have debug privileges for the target process");
    return 1;
}

string json = ReportJsonExporter.ToJson(report);

if (outputPath != null)
{
    File.WriteAllText(outputPath, json);
    Console.Error.WriteLine($"Report written to {outputPath}");
}
else
{
    Console.WriteLine(json);
}

return 0;
