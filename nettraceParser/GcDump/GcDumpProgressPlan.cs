////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpProgressPlan.cs
//
// Notes:
// Each --gcdump phase's slice of the 0-100 progress bar, in the same shape
// Progress/ProgressPlan.cs defines for the .nettrace path (read that file's
// header for why these are measured proportions of WALL-CLOCK time rather
// than a formula over item counts - the reasoning applies unchanged here).
//
// The shares below are calibrated against real captures; see
// GcDumpCommand.cs's Timing: line, which permanently breaks a run down as
// read/analysis(census,dominators,roots,refs)/export, so recalibrating
// against another capture is a single CLI run rather than scaffolding that
// has to be re-added.
//
// The split is deliberately NOT proportional to the obvious unit counts. The
// read phase touches every byte of the file and every edge twice; the
// dominator phase touches every edge several times more (once per convergence
// sweep) but out of arrays already in memory. Bytes and edges are not
// interchangeable costs, which is exactly the trap ProgressPlan.cs documents.
//
// One difference from the .nettrace plan worth stating: the dominator phase's
// iteration count is not knowable in advance (it runs until the sweep makes no
// change), so DominatorTreeBuilder reports an asymptotic fraction within its
// own slice rather than a linear one. A phase that cannot honestly predict its
// own length gets a bar that approaches its end without claiming to reach it,
// which is better than one that hits 99% and then sits there.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpProgressPlan
{
    // Decoding the file: FastSerialization over the whole stream, then two
    // passes over the node blob to build CSR adjacency.
    public const double ReadSharePercent = 34.0;

    // Reverse postorder, breadth-first parents, the reverse edge index, the
    // convergence sweeps, and the retained-size rollup. The most expensive
    // single phase on every capture measured.
    public const double DominatorSharePercent = 38.0;

    // One linear pass over flat arrays plus a sort of a few thousand rows.
    public const double CensusSharePercent = 6.0;

    // One pass over the nodes plus a bounded walk per sampled instance.
    public const double RootPathSharePercent = 8.0;

    // One pass over every edge, with a dictionary probe on each.
    public const double ReferenceGraphSharePercent = 12.0;

    // The remaining 2% is export. Like the .nettrace path's own small
    // sub-writers it gets a start/complete snap rather than internal
    // tracking - the payload is aggregated to type level by construction, so
    // it is a few thousand rows however large the heap was.

    public static ProgressRange PlanRead()
    {
        return new ProgressRange(0.0, ReadSharePercent);
    }

    public static ProgressRange PlanDominators()
    {
        double start = ReadSharePercent;
        return new ProgressRange(start, start + DominatorSharePercent);
    }

    public static ProgressRange PlanCensus()
    {
        double start = ReadSharePercent + DominatorSharePercent;
        return new ProgressRange(start, start + CensusSharePercent);
    }

    public static ProgressRange PlanRootPaths()
    {
        double start = ReadSharePercent + DominatorSharePercent + CensusSharePercent;
        return new ProgressRange(start, start + RootPathSharePercent);
    }

    public static ProgressRange PlanReferenceGraph()
    {
        double start = ReadSharePercent + DominatorSharePercent + CensusSharePercent + RootPathSharePercent;
        return new ProgressRange(start, start + ReferenceGraphSharePercent);
    }

    public static ProgressRange PlanExport()
    {
        double start = ReadSharePercent + DominatorSharePercent + CensusSharePercent + RootPathSharePercent + ReferenceGraphSharePercent;
        return new ProgressRange(start, 100.0);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
