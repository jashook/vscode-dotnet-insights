////////////////////////////////////////////////////////////////////////////////
// Module: CaptureDiffBuilder.cs
//
// Notes:
// Joins two CaptureProfiles into ranked delta rows.
//
// The single most important thing this file does is normalize. Two captures
// almost never cover the same wall-clock span - a 60s capture and a 300s one
// of the same healthy service differ by 5x on every raw count, which would
// read as a catastrophic regression. Every row therefore carries BOTH the raw
// values and a per-second rate, each side divided by ITS OWN duration before
// subtracting. The UI shows whichever the user asks for; the payload never
// forces the choice.
//
// Rows present on only one side are kept, not dropped: a type that stopped
// allocating entirely, or a lock that only appears after a change, is a
// result. They are marked Added/Removed so the renderer can say so rather
// than showing a delta against a fabricated zero baseline.
//
// Ranking is by ABSOLUTE delta magnitude, so the largest regressions and the
// largest improvements both surface at the top - a diff sorted by signed
// delta buries every improvement at the far end of a long list.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Diff {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public enum DiffRowKind
{
    // Present in both captures.
    Matched = 0,
    // Only in the comparison capture.
    Added = 1,
    // Only in the baseline capture.
    Removed = 2
}

public sealed class DiffRow
{
    public string Name;
    public DiffRowKind Kind;

    public long BaselineCount;
    public long ComparisonCount;
    public double BaselineAmount;
    public double ComparisonAmount;

    // Raw differences (comparison - baseline).
    public long DeltaCount;
    public double DeltaAmount;

    // Duration-normalized differences - each side divided by its own
    // capture's duration first. This is the number that is actually
    // comparable when the two captures ran for different lengths of time.
    public double BaselineAmountPerSecond;
    public double ComparisonAmountPerSecond;
    public double DeltaAmountPerSecond;

    // Percent change of Amount relative to the baseline. double.NaN when
    // there is no baseline to be relative TO (an Added row) - deliberately
    // not 0 or 100, both of which would render as a real measurement.
    public double PercentChange;
}

public sealed class CaptureDiff
{
    public CaptureProfile Baseline;
    public CaptureProfile Comparison;

    public List<DiffRow> EventTypes = new List<DiffRow>();
    public List<DiffRow> AllocationTypes = new List<DiffRow>();
    public List<DiffRow> ExceptionTypes = new List<DiffRow>();
    public List<DiffRow> CpuMethods = new List<DiffRow>();
    public List<DiffRow> ContentionSites = new List<DiffRow>();
    public List<DiffRow> Locks = new List<DiffRow>();
}

public static class CaptureDiffBuilder
{
    // Per-dimension cap on emitted rows, applied AFTER ranking by absolute
    // delta - the tail of a diff is rows that barely moved, which is exactly
    // what nobody is looking for. Mirrors the ranked-table limits the
    // single-capture exporters already use.
    private const int MaxRowsPerDimension = 200;

    public static CaptureDiff Build(CaptureProfile baseline, CaptureProfile comparison)
    {
        CaptureDiff diff = new CaptureDiff();
        diff.Baseline = baseline;
        diff.Comparison = comparison;

        double baselineSeconds = baseline.CaptureDurationMSec / 1000.0;
        double comparisonSeconds = comparison.CaptureDurationMSec / 1000.0;

        diff.EventTypes = JoinDimension(baseline.EventTypes, comparison.EventTypes, baselineSeconds, comparisonSeconds);
        diff.AllocationTypes = JoinDimension(baseline.AllocationTypes, comparison.AllocationTypes, baselineSeconds, comparisonSeconds);
        diff.ExceptionTypes = JoinDimension(baseline.ExceptionTypes, comparison.ExceptionTypes, baselineSeconds, comparisonSeconds);
        diff.CpuMethods = JoinDimension(baseline.CpuMethods, comparison.CpuMethods, baselineSeconds, comparisonSeconds);
        diff.ContentionSites = JoinDimension(baseline.ContentionSites, comparison.ContentionSites, baselineSeconds, comparisonSeconds);
        diff.Locks = JoinDimension(baseline.Locks, comparison.Locks, baselineSeconds, comparisonSeconds);

        return diff;
    }

    // A full outer join over the two name sets. Deliberately one generic
    // routine for every dimension: the join, the normalization and the
    // ranking are identical, and five copies would be five chances for them
    // to drift.
    private static List<DiffRow> JoinDimension(Dictionary<string, NamedMetric> baselineMetrics, Dictionary<string, NamedMetric> comparisonMetrics, double baselineSeconds, double comparisonSeconds)
    {
        List<DiffRow> rows = new List<DiffRow>();

        foreach (KeyValuePair<string, NamedMetric> baselineEntry in baselineMetrics)
        {
            NamedMetric comparisonMetric;
            comparisonMetrics.TryGetValue(baselineEntry.Key, out comparisonMetric);

            rows.Add(MakeRow(baselineEntry.Key, baselineEntry.Value, comparisonMetric, baselineSeconds, comparisonSeconds));
        }

        foreach (KeyValuePair<string, NamedMetric> comparisonEntry in comparisonMetrics)
        {
            if (baselineMetrics.ContainsKey(comparisonEntry.Key))
            {
                continue;
            }

            rows.Add(MakeRow(comparisonEntry.Key, null, comparisonEntry.Value, baselineSeconds, comparisonSeconds));
        }

        // Ranked by the magnitude of the normalized change, so a big
        // improvement is as prominent as a big regression.
        rows.Sort((DiffRow left, DiffRow right) =>
        {
            double leftMagnitude = Math.Abs(left.DeltaAmountPerSecond);
            double rightMagnitude = Math.Abs(right.DeltaAmountPerSecond);
            int comparison = rightMagnitude.CompareTo(leftMagnitude);

            if (comparison != 0)
            {
                return comparison;
            }

            return Math.Abs(right.DeltaAmount).CompareTo(Math.Abs(left.DeltaAmount));
        });

        if (rows.Count > MaxRowsPerDimension)
        {
            rows.RemoveRange(MaxRowsPerDimension, rows.Count - MaxRowsPerDimension);
        }

        return rows;
    }

    private static DiffRow MakeRow(string name, NamedMetric baselineMetric, NamedMetric comparisonMetric, double baselineSeconds, double comparisonSeconds)
    {
        DiffRow row = new DiffRow();
        row.Name = name;

        if (baselineMetric == null)
        {
            row.Kind = DiffRowKind.Added;
        }
        else if (comparisonMetric == null)
        {
            row.Kind = DiffRowKind.Removed;
        }
        else
        {
            row.Kind = DiffRowKind.Matched;
        }

        row.BaselineCount = baselineMetric != null ? baselineMetric.Count : 0;
        row.BaselineAmount = baselineMetric != null ? baselineMetric.Amount : 0;
        row.ComparisonCount = comparisonMetric != null ? comparisonMetric.Count : 0;
        row.ComparisonAmount = comparisonMetric != null ? comparisonMetric.Amount : 0;

        row.DeltaCount = row.ComparisonCount - row.BaselineCount;
        row.DeltaAmount = row.ComparisonAmount - row.BaselineAmount;

        // Guarded rather than assumed non-zero: a capture whose events all
        // share one timestamp has a zero span, and dividing by it would put
        // Infinity into the JSON, which System.Text.Json rejects outright.
        row.BaselineAmountPerSecond = baselineSeconds > 0 ? row.BaselineAmount / baselineSeconds : 0;
        row.ComparisonAmountPerSecond = comparisonSeconds > 0 ? row.ComparisonAmount / comparisonSeconds : 0;
        row.DeltaAmountPerSecond = row.ComparisonAmountPerSecond - row.BaselineAmountPerSecond;

        row.PercentChange = row.BaselineAmount != 0
            ? (row.DeltaAmount / Math.Abs(row.BaselineAmount)) * 100.0
            : double.NaN;

        return row;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Diff)
