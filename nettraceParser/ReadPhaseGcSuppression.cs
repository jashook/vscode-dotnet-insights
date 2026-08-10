////////////////////////////////////////////////////////////////////////////////
// Module: ReadPhaseGcSuppression.cs
//
// Notes:
// Suppresses garbage collection for the duration of NettraceFile.Read (see
// Program.cs) via GC.TryStartNoGCRegion, and nothing else.
//
// Why the read phase specifically: it allocates roughly 2.6x the input
// file's size (measured across two real captures - a 737MB capture
// allocated 1,926MB, a 1,115MB capture allocated 2,971MB), and
// approximately ALL of it survives - Blocks/StackBlock.cs's decoded long[]
// instruction-pointer arrays are referenced by EventRecord.Stack for the
// rest of the process's life (see EventBlock.cs's eager stack-resolution
// comment), as is the decoded EventRecord list itself. That breaks the
// generational GC's core "most objects die young" assumption outright:
// measured against a real capture, individual collections reclaimed
// 0.6-2.0 KB each. Every gen0 collection therefore copies/promotes
// essentially its whole contents into gen1 rather than reclaiming it,
// which fills gen1 at gen0's own allocation rate and escalates into
// repeated full gen2 collections that also find nothing dead.
//
// Measured on a real 737MB/4.29M-event capture (osx-arm64, Server GC),
// suppressing GC for the read phase only:
//   - total run:  2745-3656ms -> 2543-2558ms
//   - GC pause:   167-419ms   -> 0.0ms
//   - collections: [4,3,3]    -> [1,1,1]  (gen0,gen1,gen2)
//   - peak RSS:   2.31GB      -> 1.82GB   (LOWER - no promotion copying)
//   - JSON output: byte-for-byte identical
// It also removes a large run-to-run bimodality (the baseline alternated
// between ~2750ms and ~3650ms depending on whether the GC happened to
// escalate to 3 full collections or 1); the suppressed runs were stable to
// within ~15ms of each other.
//
// CRITICAL - budget sizing is load-bearing, and undersizing is actively
// WORSE than not doing this at all. A NoGCRegion whose budget is exhausted
// mid-read forces an induced collection and exits the region: measured on
// the same capture, a 128MB budget produced 3743ms and MORE collections
// ([5,4,4]) than the 3561ms/[4,3,3] baseline. ComputeBudgetBytes therefore
// declines entirely (returns 0) rather than ever requesting a budget it
// isn't confident covers the whole read.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Runtime;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ReadPhaseGcSuppression
{
    // Below this, the read phase is short enough that its collections cost
    // little in absolute terms, and pre-committing hundreds of MB to save a
    // few ms is a bad trade for a user opening a small capture. The 9MB
    // fixture in this repo reads in ~40ms total, for scale.
    private const long MinInputFileSizeBytes = 64L * 1024 * 1024;

    // Measured minimum viable budget as a multiple of input file size: a
    // 737MB capture's read survived on 512MB (0.69x) but not 256MB, and a
    // 1,115MB capture survived on 1024MB (0.92x) but not 512MB. 1.2x sits
    // above the highest observed requirement with margin - deliberately
    // generous, since undersizing is the one failure mode that makes things
    // worse rather than merely not-better (see this file's header comment).
    private const double BudgetToInputFileSizeRatio = 1.2;

    // Refuse to request more than this outright, regardless of input size -
    // an enormous capture should fall back to ordinary GC behavior rather
    // than demand a multi-GB up-front commitment.
    private const long MaxBudgetBytes = 4L * 1024 * 1024 * 1024;

    // Never ask for more than this share of what the GC reports as
    // available - leaves room for the rest of the process (and the rest of
    // the machine) rather than pre-committing everything to the read phase.
    private const double MaxFractionOfAvailableMemory = 0.5;

    // Returns the NoGCRegion budget to request, or 0 meaning "don't attempt
    // this at all". totalAvailableMemoryBytes comes from
    // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; pass 0 when unknown to
    // skip the affordability check.
    //
    // Note the two distinct "no" answers this deliberately collapses into
    // 0: too-small an input (not worth it) and can't-afford-a-full-budget
    // (dangerous). The second is the important one - when the machine can't
    // back the whole read, this declines rather than starting a region that
    // would blow out partway through and leave things slower than if it had
    // never been attempted.
    public static long ComputeBudgetBytes(long inputFileSizeBytes, long totalAvailableMemoryBytes)
    {
        if (inputFileSizeBytes < MinInputFileSizeBytes)
        {
            return 0;
        }

        long requiredBudgetBytes = (long)(inputFileSizeBytes * BudgetToInputFileSizeRatio);

        if (requiredBudgetBytes > MaxBudgetBytes)
        {
            return 0;
        }

        if (totalAvailableMemoryBytes > 0)
        {
            long affordableBudgetBytes = (long)(totalAvailableMemoryBytes * MaxFractionOfAvailableMemory);
            if (requiredBudgetBytes > affordableBudgetBytes)
            {
                return 0;
            }
        }

        return requiredBudgetBytes;
    }

    // True only if a region was actually entered (the caller must then pair
    // this with End()). Every failure mode is treated as "just don't
    // suppress" - a refused or unavailable NoGCRegion is a missed
    // optimization, never a correctness problem, so none of them should
    // stop a file from opening.
    public static bool TryStart(long budgetBytes)
    {
        if (budgetBytes <= 0)
        {
            return false;
        }

        try
        {
            return GC.TryStartNoGCRegion(budgetBytes);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Budget exceeds what this runtime's ephemeral segment can
            // back. Caught rather than pre-checked because the real limit
            // depends on GC flavor/segment sizing this code has no
            // supported way to query up front.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Already inside a NoGCRegion - nothing in this codebase starts
            // one anywhere else, so this is purely defensive.
            return false;
        }
    }

    // Only call after TryStart returned true. The region may have already
    // ended on its own (budget exhausted mid-read), which is exactly why
    // this re-checks LatencyMode instead of calling EndNoGCRegion
    // unconditionally - doing so outside a region throws.
    public static void End()
    {
        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
        {
            GC.EndNoGCRegion();
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
