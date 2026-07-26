////////////////////////////////////////////////////////////////////////////////
// Module: Program.cs
//
// Notes:
// Usage:
//   gcHeapAnalyzer --pid <pid> [--output <path>]
//
//   --pid <pid>       PID of the target .NET process (required)
//   --output <path>   Write JSON to this file; omit to write to stdout
//
// The target process is suspended briefly while the heap is walked.
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
}

if (!sample && pid <= 0)
{
    Console.Error.WriteLine("Usage: gcHeapAnalyzer --pid <pid> [--output <path>]");
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
