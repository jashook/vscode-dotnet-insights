////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpAnalysisBuilder.cs
//
// Notes:
// Runs the four heap analyses in the one order their dependencies allow, and
// times each so the Timing: line can attribute a slow run to a phase.
//
// ORDER IS FORCED, not chosen:
//   1. Dominators - produces retained sizes AND the breadth-first parents.
//   2. Census     - needs retained sizes to fill its retained column.
//   3. Interesting types - ranked by retained bytes, so needs the census.
//   4. Root paths - only computed for the interesting types, and walks the
//                   parents from step 1.
//   5. Reference graph - independent of all of the above; last because it is
//                   the second most expensive and nothing waits on it.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Diagnostics;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpAnalysisBuilder
{
    public static GcDumpAnalysis Build(HeapGraph graph)
    {
        GcDumpAnalysis analysis = new GcDumpAnalysis();

        ProgressRange dominatorRange = GcDumpProgressPlan.PlanDominators();
        ProgressReporter.BeginPhase("Computing retained sizes", dominatorRange.Start, dominatorRange.End);

        Stopwatch dominatorStopwatch = Stopwatch.StartNew();
        DominatorResult dominators = DominatorTreeBuilder.Build(graph);
        dominatorStopwatch.Stop();
        analysis.DominatorMSec = dominatorStopwatch.ElapsedMilliseconds;

        ProgressReporter.CompletePhase();

        ProgressRange censusRange = GcDumpProgressPlan.PlanCensus();
        ProgressReporter.BeginPhase("Building type census", censusRange.Start, censusRange.End);

        Stopwatch censusStopwatch = Stopwatch.StartNew();
        TypeCensusBuilder.Build(graph, dominators, analysis);
        analysis.InterestingTypeIndices = TypeCensusBuilder.SelectInterestingTypes(analysis, GcDumpAnalysisLimits.InterestingTypeCount);
        censusStopwatch.Stop();
        analysis.CensusMSec = censusStopwatch.ElapsedMilliseconds;

        ProgressReporter.CompletePhase();

        ProgressRange rootPathRange = GcDumpProgressPlan.PlanRootPaths();
        ProgressReporter.BeginPhase("Tracing paths to roots", rootPathRange.Start, rootPathRange.End);

        Stopwatch rootPathStopwatch = Stopwatch.StartNew();
        RootPathBuilder.Build(graph, dominators, analysis);
        rootPathStopwatch.Stop();
        analysis.RootPathMSec = rootPathStopwatch.ElapsedMilliseconds;

        ProgressReporter.CompletePhase();

        ProgressRange referenceRange = GcDumpProgressPlan.PlanReferenceGraph();
        ProgressReporter.BeginPhase("Aggregating references", referenceRange.Start, referenceRange.End);

        Stopwatch referenceStopwatch = Stopwatch.StartNew();
        TypeReferenceGraphBuilder.Build(graph, analysis);
        referenceStopwatch.Stop();
        analysis.ReferenceGraphMSec = referenceStopwatch.ElapsedMilliseconds;

        ProgressReporter.CompletePhase();

        return analysis;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
