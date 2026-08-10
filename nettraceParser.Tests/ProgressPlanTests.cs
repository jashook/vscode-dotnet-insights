////////////////////////////////////////////////////////////////////////////////
// Module: ProgressPlanTests.cs
//
// Notes:
// Covers ProgressPlan's own contiguity/coverage/ordering guarantees - see
// that file's own header comment for why monotonicity is meant to be
// structural (each stage only ever subdivides a range the previous stage
// hasn't consumed yet) rather than something enforced by ProgressReporter's
// own clamp. The important case is zero-count phases (contentionProject was
// genuinely 0 contentions on the real reference capture this file's own
// weight constants were measured against) not producing NaN/divide-by-zero.
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Progress;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ProgressPlanTests
{
    [Fact]
    public void PlanRead_StartsAtZero()
    {
        ProgressRange range = ProgressPlan.PlanRead();

        Assert.Equal(0.0, range.Start);
        Assert.True(range.End > range.Start);
        Assert.True(range.End < 100.0);
    }

    [Fact]
    public void PlanJsonExport_EndsAtOneHundred()
    {
        ProgressRange range = ProgressPlan.PlanJsonExport();

        Assert.Equal(100.0, range.End);
    }

    [Fact]
    public void PlanRead_PlanProjectorsCombined_PlanJsonExport_AreContiguous()
    {
        ProgressRange read = ProgressPlan.PlanRead();
        ProgressRange projectors = ProgressPlan.PlanProjectorsCombined();
        ProgressRange export = ProgressPlan.PlanJsonExport();

        Assert.Equal(read.End, projectors.Start);
        Assert.Equal(projectors.End, export.Start);
        Assert.Equal(100.0, export.End);
    }

    [Fact]
    public void PlanProjectorPhases_ReturnsSevenContiguousRangesCoveringTheirCombinedRange()
    {
        ProgressRange combined = ProgressPlan.PlanProjectorsCombined();
        ProgressRange[] phases = ProgressPlan.PlanProjectorPhases();

        Assert.Equal(7, phases.Length);
        Assert.Equal(combined.Start, phases[0].Start);
        Assert.Equal(combined.End, phases[phases.Length - 1].End);

        for (int phaseIndex = 1; phaseIndex < phases.Length; ++phaseIndex)
        {
            Assert.Equal(phases[phaseIndex - 1].End, phases[phaseIndex].Start);
        }
    }

    [Fact]
    public void PlanProjectorPhases_EveryPhaseHasNonNegativeWidth()
    {
        ProgressRange[] phases = ProgressPlan.PlanProjectorPhases();

        for (int phaseIndex = 0; phaseIndex < phases.Length; ++phaseIndex)
        {
            Assert.True(phases[phaseIndex].End >= phases[phaseIndex].Start, $"phase {phaseIndex} has negative width");
        }
    }

    [Fact]
    public void PlanJsonExportSubWriters_FiveRangesAreContiguousAndCoverTheWholeExportRange()
    {
        ProgressRange exportRange = ProgressPlan.PlanJsonExport();
        ExportSubWriterRanges ranges = ProgressPlan.PlanJsonExportSubWriters(gcCount: 36, allocationCount: 1926758, exceptionCount: 31733, sampleCount: 26132500, contentionCount: 0);

        Assert.Equal(exportRange.Start, ranges.Allocation.Start);
        Assert.Equal(ranges.Allocation.End, ranges.Exception.Start);
        Assert.Equal(ranges.Exception.End, ranges.Cpu.Start);
        Assert.Equal(ranges.Cpu.End, ranges.Contention.Start);
        Assert.Equal(ranges.Contention.End, ranges.Gc.Start);
        Assert.Equal(exportRange.End, ranges.Gc.End);
    }

    // The reference capture this file's own constants were measured
    // against had genuinely zero contentions - a real, not hypothetical,
    // all-inputs-zero-for-one-writer case.
    [Fact]
    public void PlanJsonExportSubWriters_ZeroCountWriterGetsZeroWidthNotNaN()
    {
        ExportSubWriterRanges ranges = ProgressPlan.PlanJsonExportSubWriters(gcCount: 36, allocationCount: 1926758, exceptionCount: 31733, sampleCount: 26132500, contentionCount: 0);

        Assert.Equal(ranges.Contention.Start, ranges.Contention.End);
        Assert.False(double.IsNaN(ranges.Contention.Start));
        Assert.False(double.IsNaN(ranges.Contention.End));
    }

    // Every count zero - the empty-capture case (e.g. a trace with only
    // JIT/thread events, none of the 5 sub-writer-relevant kinds) - must
    // not divide by zero across the board.
    [Fact]
    public void PlanJsonExportSubWriters_AllCountsZeroProducesNoNaNs()
    {
        ProgressRange exportRange = ProgressPlan.PlanJsonExport();
        ExportSubWriterRanges ranges = ProgressPlan.PlanJsonExportSubWriters(gcCount: 0, allocationCount: 0, exceptionCount: 0, sampleCount: 0, contentionCount: 0);

        Assert.False(double.IsNaN(ranges.Allocation.Start));
        Assert.False(double.IsNaN(ranges.Gc.End));
        Assert.Equal(exportRange.Start, ranges.Allocation.Start);
        Assert.Equal(exportRange.End, ranges.Gc.End);
    }

    [Fact]
    public void PlanJsonExportSubWriters_LargerCountGetsProportionallyMoreWidth()
    {
        // Same total scale, but overwhelmingly CPU-sample-dominated (the
        // reference capture's own real shape) - the Cpu range should be by
        // far the widest of the five.
        ExportSubWriterRanges ranges = ProgressPlan.PlanJsonExportSubWriters(gcCount: 36, allocationCount: 1926758, exceptionCount: 31733, sampleCount: 26132500, contentionCount: 0);

        double allocationWidth = ranges.Allocation.End - ranges.Allocation.Start;
        double cpuWidth = ranges.Cpu.End - ranges.Cpu.Start;

        Assert.True(cpuWidth > allocationWidth, $"cpuWidth ({cpuWidth}) should exceed allocationWidth ({allocationWidth}) given 26.1M samples vs 1.9M ticks");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
