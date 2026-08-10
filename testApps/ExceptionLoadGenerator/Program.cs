////////////////////////////////////////////////////////////////////////////////
// Module: Program.cs
//
// Notes:
// Standalone load generator for exercising nettraceParser's exception-event
// feature (CLR ExceptionThrown_V1, Microsoft-Windows-DotNETRuntime provider,
// event ID 80) against a real capture instead of only synthetic payload
// bytes - see ExceptionThrower.cs for the actual throw patterns exercised
// (plain throw, caught-and-rethrown, throw-with-inner-exception). Throws a
// fixed number of exceptions from several-frames-deep call chains, then
// allocates enough garbage to force at least one real gen0 GC before
// exiting - unlike GcHeapLoadGenerator, this doesn't need to run until
// Ctrl+C, but a capture with genuinely zero GCs is an edge case several
// other real consumers of nettraceParser's JSON output (e.g.
// GcStatsCalculations.ts's computeAllocationAmountStats) don't handle
// cleanly, so this avoids exercising that unrelated gap incidentally.
//
// Usage:
//   dotnet run -- [--iterations <n>]
//
// Capture with (launches this process under trace from the very start, so
// no attach race and no need for this app to wait around for a listener).
// Keyword 0x8001 = Exception (0x8000) | GC (0x1) - Exception alone omits
// every GC event (GCStart/GCEnd/...) even though this app forces a real
// GC, which produced a capture GcStatsCalculations.ts's
// computeAllocationAmountStats didn't handle cleanly (see the comment
// above) the first time this fixture was generated with 0x8000 alone:
//   dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x8001:5 -- dotnet run --project testApps/ExceptionLoadGenerator
//
// example-exceptions.nettrace in this directory is a real capture from this
// load generator, used as a nettraceParser input for exercising the
// exception JSON export path and as ground truth for
// GroundTruthDiffTests.cs's exception diff test, without needing a live
// process on hand.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Globalization;

using DotnetInsights.ExceptionLoadGenerator;

int iterations = 200;

for (int argIndex = 0; argIndex < args.Length; ++argIndex)
{
    switch (args[argIndex])
    {
        case "--iterations":
            argIndex = ParseInt(args, argIndex, ref iterations);
            break;

        default:
            Console.Error.WriteLine($"Unknown argument: '{args[argIndex]}'");
            return 1;
    }
}

ExceptionThrower.Run(iterations);

// Forces at least one real gen0 GC (default Workstation GC gen0 budget is a
// few MB) so this capture isn't a degenerate "zero GCs" edge case - see the
// comment above.
for (int allocationIndex = 0; allocationIndex < 64; ++allocationIndex)
{
    GC.KeepAlive(new byte[256 * 1024]);
}

GC.Collect();

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
