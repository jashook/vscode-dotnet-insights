////////////////////////////////////////////////////////////////////////////////
// Module: Program.cs
//
// Notes:
// Standalone load generator for exercising nettraceParser's CPU
// sample-profiling feature (Microsoft-DotNETCore-SampleProfiler provider)
// against a real capture instead of only synthetic payload bytes - see
// CpuWorkload.cs for the actual call tree exercised (a cheap, frequently
// called FastPath; a moderate MediumWork that itself calls FastPath; and a
// deliberately expensive SlowRecursiveWork that should dominate both self
// and total time in the resulting profile). Runs a fixed wall-clock
// duration rather than a fixed iteration count so a capture's length is
// predictable regardless of the host machine's speed.
//
// Usage:
//   dotnet run -- [--duration-seconds <n>]
//
// Capture with (launches this process under trace from the very start, so
// no attach race and no need for this app to wait around for a listener).
// --profile cpu-sampling enables Microsoft-DotNETCore-SampleProfiler plus
// the minimal CLR rundown providers nettraceParser's MethodSymbolTable
// needs to resolve sampled instruction pointers back to method names:
//   dotnet-trace collect --profile cpu-sampling -- dotnet run --project testApps/CpuLoadGenerator -- --duration-seconds 6
//
// example-cpu-sample.nettrace in this directory is a real capture from this
// load generator, used as a nettraceParser input for exercising the CPU
// sample-profiling JSON export path and as ground truth for
// GroundTruthDiffTests.cs's CPU sample diff test, without needing a live
// process on hand.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Diagnostics;
using System.Globalization;

using DotnetInsights.CpuLoadGenerator;

int durationSeconds = 6;

for (int argIndex = 0; argIndex < args.Length; ++argIndex)
{
    switch (args[argIndex])
    {
        case "--duration-seconds":
            argIndex = ParseInt(args, argIndex, ref durationSeconds);
            break;

        default:
            Console.Error.WriteLine($"Unknown argument: '{args[argIndex]}'");
            return 1;
    }
}

Stopwatch stopwatch = Stopwatch.StartNew();
long iterationCount = 0;
long accumulatorResult = 0;

while (stopwatch.Elapsed.TotalSeconds < durationSeconds)
{
    accumulatorResult = CpuWorkload.RunIteration();
    ++iterationCount;
}

Console.WriteLine($"Ran {iterationCount} iterations in {stopwatch.Elapsed.TotalSeconds:F1}s (accumulator={accumulatorResult}).");

return 0;

static int ParseInt(string[] args, int argIndex, ref int target)
{
    if (argIndex + 1 >= args.Length || !int.TryParse(args[argIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out target))
    {
        Console.Error.WriteLine($"Expected an integer value after '{args[argIndex]}'");
        Environment.Exit(1);
    }

    return argIndex + 1;
}
