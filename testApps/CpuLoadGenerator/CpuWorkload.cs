////////////////////////////////////////////////////////////////////////////////
// Module: CpuWorkload.cs
//
// Notes:
// Real, sustained, CPU-bound arithmetic work spread across a handful of
// distinctly-named nested methods of very different per-call cost - this is
// what generates real Microsoft-DotNETCore-SampleProfiler events for
// nettraceParser's CPU sample-profiling feature to decode against, with a
// readable, demonstrable shape: FastPath is cheap but called often,
// MediumWork is moderate and itself calls FastPath (so FastPath shows up
// under two different callers in the resulting flame graph), and
// SlowRecursiveWork does real, non-trivial work at EVERY recursion depth
// (not just call overhead) so it dominates both self time (spread across
// each recursive frame) and total/inclusive time - the hot path this
// workload is deliberately built to make obvious.
//
// Every loop accumulates into the shared `accumulator` field (never read
// back within the same method) specifically so the JIT can't dead-code-
// eliminate the arithmetic as unused work.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.CpuLoadGenerator {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class CpuWorkload
{
    private static long accumulator;

    public static long RunIteration()
    {
        for (int callIndex = 0; callIndex < 25; ++callIndex)
        {
            FastPath();
        }

        for (int callIndex = 0; callIndex < 4; ++callIndex)
        {
            MediumWork();
        }

        SlowRecursiveWork(14);

        return accumulator;
    }

    // Cheap per call - most of its aggregate time in a capture comes from
    // how often it's called (from both RunIteration and MediumWork below),
    // not from its own per-call cost.
    private static void FastPath()
    {
        long localSum = 0;

        for (int index = 0; index < 400; ++index)
        {
            localSum += (index * 7) % 13;
        }

        accumulator += localSum;
    }

    // Moderate per-call cost, and itself calls FastPath twice - so a flame
    // graph built from a capture of this app shows FastPath nested under
    // both RunIteration directly and under MediumWork.
    private static void MediumWork()
    {
        long localSum = 0;

        for (int index = 0; index < 20_000; ++index)
        {
            localSum += (index * index) % 97;
        }

        FastPath();
        FastPath();

        accumulator += localSum;
    }

    // The most expensive call chain in this workload, by a wide margin -
    // real work at every recursion depth, not just recursive call overhead,
    // so self time is real and spread across each depth's own frame rather
    // than concentrated in one leaf call. This is the function a CPU sample
    // profile of this app is expected to show as the dominant hot path in
    // both the flame graph and the hot-methods table.
    private static long SlowRecursiveWork(int remainingDepth)
    {
        long localSum = 0;

        for (int index = 0; index < 300_000; ++index)
        {
            localSum += (index * index * 3) % 101;
        }

        if (remainingDepth > 0)
        {
            localSum += SlowRecursiveWork(remainingDepth - 1);
        }

        accumulator += localSum;

        return localSum;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.CpuLoadGenerator)
