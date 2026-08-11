////////////////////////////////////////////////////////////////////////////////
// Module: CaptureDiffBuilderTests.cs
//
// Notes:
// Pins the rules of a two-capture diff that would otherwise regress silently -
// a diff produces plausible-looking numbers no matter how wrong the join is,
// so nothing here is checkable by eye on real data.
//
// The rules that matter:
//   - Normalization divides each side by ITS OWN capture duration. Two
//     captures almost never span the same wall-clock time, and comparing raw
//     counts across a 60s and a 300s capture of the same healthy service
//     reports a 5x regression that does not exist.
//   - Rows on one side only survive as added/removed. A type that stopped
//     allocating is a result, not an absence.
//   - Ranking is by ABSOLUTE delta, so improvements surface as prominently as
//     regressions rather than being buried at the far end of the list.
//   - A degenerate (zero-duration) capture must not put Infinity into the
//     payload; System.Text.Json refuses to serialize it and the whole export
//     would fail.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Diff;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class CaptureDiffBuilderTests
{
    private static CaptureProfile MakeProfile(double durationMSec)
    {
        CaptureProfile profile = new CaptureProfile();
        profile.FilePath = "capture.nettrace";
        profile.ProcessName = "capture";
        profile.CaptureDurationMSec = durationMSec;
        return profile;
    }

    private static void AddAllocationType(CaptureProfile profile, string typeName, long count, double bytes)
    {
        NamedMetric metric = new NamedMetric(typeName);
        metric.Count = count;
        metric.Amount = bytes;
        profile.AllocationTypes[typeName] = metric;
    }

    private static DiffRow FindRow(List<DiffRow> rows, string name)
    {
        for (int rowIndex = 0; rowIndex < rows.Count; ++rowIndex)
        {
            if (rows[rowIndex].Name == name)
            {
                return rows[rowIndex];
            }
        }

        return null;
    }

    [Fact]
    public void Build_NormalizesEachSideByItsOwnDuration()
    {
        // The same steady allocation rate, captured for very different
        // lengths of time. Raw bytes differ 5x; the rate is identical, and
        // the rate is the only honest comparison.
        CaptureProfile baseline = MakeProfile(60_000.0);
        AddAllocationType(baseline, "System.String", count: 100, bytes: 6_000.0);

        CaptureProfile comparison = MakeProfile(300_000.0);
        AddAllocationType(comparison, "System.String", count: 500, bytes: 30_000.0);

        CaptureDiff diff = CaptureDiffBuilder.Build(baseline, comparison);
        DiffRow row = FindRow(diff.AllocationTypes, "System.String");

        Assert.Equal(DiffRowKind.Matched, row.Kind);
        Assert.Equal(24_000.0, row.DeltaAmount, 3);
        Assert.Equal(100.0, row.BaselineAmountPerSecond, 3);
        Assert.Equal(100.0, row.ComparisonAmountPerSecond, 3);
        Assert.Equal(0.0, row.DeltaAmountPerSecond, 6);
    }

    [Fact]
    public void Build_RowOnlyInComparisonIsAddedNotADeltaAgainstZero()
    {
        CaptureProfile baseline = MakeProfile(1_000.0);
        CaptureProfile comparison = MakeProfile(1_000.0);
        AddAllocationType(comparison, "MyApp.NewType", count: 10, bytes: 500.0);

        CaptureDiff diff = CaptureDiffBuilder.Build(baseline, comparison);
        DiffRow row = FindRow(diff.AllocationTypes, "MyApp.NewType");

        Assert.Equal(DiffRowKind.Added, row.Kind);
        Assert.Equal(0, row.BaselineAmount);
        Assert.Equal(500.0, row.ComparisonAmount, 3);
        // No baseline to be relative to - NaN, deliberately not 0 or 100,
        // both of which would render as a real measurement.
        Assert.True(double.IsNaN(row.PercentChange));
    }

    [Fact]
    public void Build_RowOnlyInBaselineSurvivesAsRemoved()
    {
        // A type that stopped allocating entirely is exactly the result
        // someone diffing after a fix is looking for - dropping it would
        // hide the win.
        CaptureProfile baseline = MakeProfile(1_000.0);
        AddAllocationType(baseline, "MyApp.GoneType", count: 10, bytes: 500.0);
        CaptureProfile comparison = MakeProfile(1_000.0);

        CaptureDiff diff = CaptureDiffBuilder.Build(baseline, comparison);
        DiffRow row = FindRow(diff.AllocationTypes, "MyApp.GoneType");

        Assert.Equal(DiffRowKind.Removed, row.Kind);
        Assert.Equal(500.0, row.BaselineAmount, 3);
        Assert.Equal(0, row.ComparisonAmount);
        Assert.Equal(-500.0, row.DeltaAmount, 3);
        Assert.Equal(-100.0, row.PercentChange, 3);
    }

    [Fact]
    public void Build_RanksByAbsoluteDeltaSoImprovementsAreNotBuried()
    {
        CaptureProfile baseline = MakeProfile(1_000.0);
        AddAllocationType(baseline, "BigImprovement", count: 1, bytes: 10_000.0);
        AddAllocationType(baseline, "SmallRegression", count: 1, bytes: 100.0);

        CaptureProfile comparison = MakeProfile(1_000.0);
        AddAllocationType(comparison, "BigImprovement", count: 1, bytes: 1_000.0);
        AddAllocationType(comparison, "SmallRegression", count: 1, bytes: 400.0);

        CaptureDiff diff = CaptureDiffBuilder.Build(baseline, comparison);

        // -9000 outranks +300 despite being an improvement.
        Assert.Equal("BigImprovement", diff.AllocationTypes[0].Name);
        Assert.True(diff.AllocationTypes[0].DeltaAmount < 0);
        Assert.Equal("SmallRegression", diff.AllocationTypes[1].Name);
    }

    [Fact]
    public void Build_ZeroDurationCaptureDoesNotProduceInfinityOrNaNRates()
    {
        // A capture whose events all share one timestamp has a zero span.
        // Dividing by it would put Infinity in the payload, which
        // System.Text.Json refuses to write at all - the export would fail
        // outright rather than degrade.
        CaptureProfile baseline = MakeProfile(0.0);
        AddAllocationType(baseline, "System.String", count: 1, bytes: 100.0);
        CaptureProfile comparison = MakeProfile(0.0);
        AddAllocationType(comparison, "System.String", count: 1, bytes: 200.0);

        CaptureDiff diff = CaptureDiffBuilder.Build(baseline, comparison);
        DiffRow row = FindRow(diff.AllocationTypes, "System.String");

        Assert.False(double.IsInfinity(row.BaselineAmountPerSecond));
        Assert.False(double.IsInfinity(row.ComparisonAmountPerSecond));
        Assert.False(double.IsInfinity(row.DeltaAmountPerSecond));
        Assert.False(double.IsNaN(row.DeltaAmountPerSecond));
        // The raw delta is still meaningful even when the rate is not.
        Assert.Equal(100.0, row.DeltaAmount, 3);
    }

    [Fact]
    public void Build_IdenticalProfilesProduceOnlyMatchedRowsWithZeroDeltas()
    {
        // The cheapest possible check that the join keys are stable: a
        // capture diffed against itself must show no change anywhere.
        CaptureProfile baseline = MakeProfile(5_000.0);
        AddAllocationType(baseline, "System.String", count: 10, bytes: 1_000.0);
        AddAllocationType(baseline, "System.Byte[]", count: 5, bytes: 2_000.0);

        CaptureProfile comparison = MakeProfile(5_000.0);
        AddAllocationType(comparison, "System.String", count: 10, bytes: 1_000.0);
        AddAllocationType(comparison, "System.Byte[]", count: 5, bytes: 2_000.0);

        CaptureDiff diff = CaptureDiffBuilder.Build(baseline, comparison);

        Assert.Equal(2, diff.AllocationTypes.Count);
        foreach (DiffRow row in diff.AllocationTypes)
        {
            Assert.Equal(DiffRowKind.Matched, row.Kind);
            Assert.Equal(0, row.DeltaAmount);
            Assert.Equal(0, row.DeltaCount);
            Assert.Equal(0.0, row.DeltaAmountPerSecond, 9);
        }
    }

    [Fact]
    public void Build_DeltaCountIsTrackedSeparatelyFromDeltaAmount()
    {
        // Count and amount move independently and both matter: the same
        // bytes across far more (smaller) allocations is a different problem
        // from the same allocations getting bigger.
        CaptureProfile baseline = MakeProfile(1_000.0);
        AddAllocationType(baseline, "System.String", count: 10, bytes: 1_000.0);

        CaptureProfile comparison = MakeProfile(1_000.0);
        AddAllocationType(comparison, "System.String", count: 100, bytes: 1_000.0);

        CaptureDiff diff = CaptureDiffBuilder.Build(baseline, comparison);
        DiffRow row = FindRow(diff.AllocationTypes, "System.String");

        Assert.Equal(90, row.DeltaCount);
        Assert.Equal(0, row.DeltaAmount);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
