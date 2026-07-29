////////////////////////////////////////////////////////////////////////////////
// Module: Program.cs
//
// Notes:
// Standalone load generator for exercising gcHeapAnalyzer against a live
// process with a large, fragmented, actively churning managed heap - the
// kind gcHeapAnalyzer is meant to diagnose. Runs Server GC and mocks a TTL
// cache of large payloads (see LoadGenerator.cs) on top of a baseline of
// retained small objects. Runs until Ctrl+C.
//
// Usage:
//   dotnet run -- [--target-gb <n>] [--loh-chance <0-1>] [--retain-chance <0-1>]
//                 [--loh-retain-chance <0-1>] [--status-interval-seconds <n>]
//
// Pair with gcHeapAnalyzer once the printed "retained" total is near the
// target:
//   dotnet run --project gcHeapAnalyzer -- --pid <pid>
//
// example-gcHeapAnalyzer-output.json in this directory is a real capture
// against this load generator (--target-gb 0.3, Server GC, 8 logical
// heaps) - a concrete reference for the full report shape (generations,
// per-generation histograms, large-chunk adjacency, segment occupancy,
// segment maps) without needing a live process on hand.
//
// example-gc-verbose.nettrace is a real ~3-second `dotnet-trace collect
// --profile gc-verbose` capture against the same load generator (169 GCs,
// ~95k allocation ticks) - a real nettraceParser input for exercising the
// GC/allocation JSON export path without needing a live process either.
// Kept short deliberately (~9 MB) rather than a longer capture (a 10s
// capture of this same load generator was ~32 MB) to stay reasonably close
// to this repo's other real fixture (nettrace-gcdata.json, 829 KB).
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Globalization;

using DotnetInsights.GcHeapLoadGenerator;

LoadGeneratorOptions options = new LoadGeneratorOptions();

for (int argIndex = 0; argIndex < args.Length; ++argIndex)
{
    switch (args[argIndex])
    {
        case "--target-gb":
            argIndex = ParseDouble(args, argIndex, ref options.TargetGb);
            break;

        case "--loh-chance":
            argIndex = ParseDouble(args, argIndex, ref options.LohChance);
            break;

        case "--retain-chance":
            argIndex = ParseDouble(args, argIndex, ref options.RetainChance);
            break;

        case "--loh-retain-chance":
            argIndex = ParseDouble(args, argIndex, ref options.LohRetainChance);
            break;

        case "--status-interval-seconds":
            argIndex = ParseInt(args, argIndex, ref options.StatusIntervalSeconds);
            break;

        default:
            Console.Error.WriteLine($"Unknown argument: '{args[argIndex]}'");
            return 1;
    }
}

LoadGenerator.Run(options);
return 0;

static int ParseDouble(string[] args, int argIndex, ref double target)
{
    if (argIndex + 1 >= args.Length || !double.TryParse(args[argIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out target))
    {
        Console.Error.WriteLine($"Expected a numeric value after '{args[argIndex]}'");
        Environment.Exit(1);
    }

    return argIndex + 1;
}

static int ParseInt(string[] args, int argIndex, ref int target)
{
    if (argIndex + 1 >= args.Length || !int.TryParse(args[argIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out target))
    {
        Console.Error.WriteLine($"Expected an integer value after '{args[argIndex]}'");
        Environment.Exit(1);
    }

    return argIndex + 1;
}
