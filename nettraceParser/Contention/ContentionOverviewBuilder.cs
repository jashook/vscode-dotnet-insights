////////////////////////////////////////////////////////////////////////////////
// Module: ContentionOverviewBuilder.cs
//
// Notes:
// Backs the Contention view's Overview tab - the lock-side counterpart to the
// GC view's Charts tab. Answers three questions the ranked-sites table and the
// per-lock Gantt cannot:
//
//   1. How much of the wall clock was spent blocked, over time.
//   2. What a wait actually costs at the tail (p50 vs p99 vs max), over time -
//      a mean hides exactly the stalls worth chasing, and on real captures the
//      two differ by three orders of magnitude.
//   3. Whether lock stalls line up with CPU spikes, which is the hypothesis
//      this view was built to test.
//
// TWO DIFFERENT "TIME BLOCKED" NUMBERS ARE EMITTED, and conflating them is the
// mistake Overview/TimeBreakdownBuilder.cs already had to correct once (it
// rendered "Contending Locks 426.1%"):
//
//   - blockedWallClockMSecByBucket is a UNION: wall-clock ms in that bucket
//     during which AT LEAST ONE thread was blocked. Bounded by the bucket's own
//     duration, so it is a genuine percentage of wall clock and is the honest
//     analogue of a GC pause bar.
//   - blockedThreadMSecByBucket is a SUM across blocked threads, so it is
//     unbounded and encodes concurrency instead. Divided by the bucket
//     duration it reads as "average threads blocked", the same figure
//     TimeBreakdown.AverageThreadsBlocked reports for the whole capture.
//
// Both come out of ONE sweep over the blocked intervals (see BuildSweep),
// which also yields peak concurrency per bucket - the number that shows a
// convoy forming.
//
// WAIT DURATION IS ATTRIBUTED TWO WAYS ON PURPOSE. The percentile series
// (p50/p99/max per bucket) attributes a wait wholly to the bucket it STARTED
// in, because "what did a wait cost if it began here" is the question a
// percentile answers. The blocked-time series spread a wait across every
// bucket it OVERLAPS, because a 4-second wait did not stop the clock for 4
// seconds inside one 3-second bucket - attributing it at its start point is
// what makes a long-lock chart spike in the wrong place, which is precisely
// the misreading this view exists to avoid.
//
// THE CPU SERIES COUNTS CPU-BOUND SAMPLES, NOT SAMPLES. .NET's sample profiler
// is wall-clock per thread, so a parked thread still produces samples and a raw
// per-bucket sample count tracks how many threads exist far more than it tracks
// CPU. CpuProfileJsonExporter.SampleTimeline.CpuBoundSamplesByBucket carries
// the non-idle subset, classified by the same CpuIdleWaitClassifier the
// Overview page's CPU-Bound tile uses. When that is unavailable the CPU series
// is omitted entirely rather than substituting the raw count - a plausible
// wrong overlay on the exact question the user is asking is worse than none.
//
// The GC pause series rides along for the same reason: a CPU spike that lands
// on a gen2 pause is a GC story, not a locking one, and separating those two by
// eye is the first thing anyone does with this chart.
//
// GRID ALIGNMENT: when the capture has CPU samples, the bucket grid IS the CPU
// sample timeline's grid, reused verbatim. Resampling either series onto the
// other's boundaries would put a smoothing artifact directly into the
// correlation this view reports.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Contention {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Gc;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Plain field-bearing class rather than a struct: it is built once per capture,
// holds a dozen arrays, and is handed straight to a JSON writer - none of the
// reasons the per-event types in this codebase are structs apply.
public sealed class ContentionOverview
{
    public bool HasData;

    public int ContentionCount;
    public double TotalWaitMSec;
    public double MeanWaitMSec;

    // Percentiles of INDIVIDUAL wait durations across the whole capture,
    // nearest-rank (no interpolation - an interpolated p99 is a duration no
    // thread actually waited, and every one of these is meant to be findable
    // in the Longest Waits list).
    public double P50WaitMSec;
    public double P75WaitMSec;
    public double P90WaitMSec;
    public double P95WaitMSec;
    public double P99WaitMSec;
    public double P999WaitMSec;
    public double MaxWaitMSec;
    public double MaxWaitStartMSec;
    public long MaxWaitThreadId;

    // Whole-capture wall clock during which at least one thread was blocked,
    // and its share of the capture. Union, not sum - see this file's header.
    public double BlockedWallClockMSec;
    public bool HasCaptureDuration;
    public double CaptureDurationMSec;
    public double BlockedWallClockPercent;
    // Summed wait over capture duration. Same definition as
    // TimeBreakdown.AverageThreadsBlocked so the two views cannot disagree.
    public double AverageThreadsBlocked;
    public int PeakThreadsBlocked;
    public double PeakThreadsBlockedAtMSec;

    // The bucket grid every series below shares.
    public double MinRelativeMSec;
    public double BucketDurationMSec;
    public int BucketCount;
    // "cpu" when the grid was taken from the CPU sample timeline, "contention"
    // when derived from the contention events alone. Surfaced so the view can
    // say whether the CPU overlay is aligned by construction or absent.
    public string GridSource;

    public double[] BlockedWallClockMSecByBucket;
    public double[] BlockedThreadMSecByBucket;
    public int[] PeakThreadsBlockedByBucket;
    public int[] ContentionCountByBucket;
    public double[] P50WaitMSecByBucket;
    public double[] P99WaitMSecByBucket;
    public double[] MaxWaitMSecByBucket;

    public bool HasCpuSamples;
    public int[] CpuBoundSamplesByBucket;
    // Every candidate CPU definition, each carrying its own correlation. See
    // CpuCorrelationSeries and BuildCpuSeries for why there is more than one.
    public List<CpuCorrelationSeries> CpuSeries = new List<CpuCorrelationSeries>();
    // "cpuTime" when each sample means a thread actually held a core (a
    // perf-based collect-linux capture), "wallClock" when the runtime sampled
    // every thread regardless (a v5 EventPipe capture). This single fact
    // decides whether any of the series below can be read as CPU UTILISATION
    // or only as thread-state counts.
    public string SamplingSemantics;
    public bool HasSampleTypeData;
    // Present only when the sampler is CPU-time driven AND a stable period was
    // recovered from the capture. When true, a sample count times
    // SamplePeriodMSec IS milliseconds of CPU time, and dividing that by
    // wall-clock time gives cores busy - the figure this whole question was
    // really about.
    public bool HasCpuTime;
    public double SamplePeriodMSec;
    public double TotalCpuMSec;
    public double AverageCoresBusy;

    public bool HasGcPauses;
    public double[] GcPauseMSecByBucket;

    // Cross-definition verdict. See ClassifyAgreement: "agree", "disagree",
    // "inconclusive", "single" or "none". The whole point of computing several
    // correlations is to be able to say "disagree" out loud rather than
    // reporting whichever one was picked first.
    public string CorrelationAgreement;
    public double MinCorrelation;
    public double MaxCorrelation;
    public int CorrelationBucketCount;
}

// One candidate answer to "was this thread doing work", with its own
// correlation against blocked thread-time.
//
// There is more than one because a v5 EventPipe capture cannot answer the
// question outright: it carries no native stacks, so a thread parked in a
// syscall and a thread running an AVX loop are both just "not managed". Each
// definition below resolves that ambiguity differently, and on a real capture
// they can disagree in SIGN - measured on a 3.0GB production capture, r ran
// from -0.598 (all samples) to +0.248 (managed and not parked) against the
// same blocked-time series. Reporting any single one of those as "the"
// correlation would have been a confident answer to a question the data does
// not settle.
public sealed class CpuCorrelationSeries
{
    public string Id;
    public string Label;
    // Rendered verbatim next to the number, so a reader can disagree with the
    // definition rather than only with the conclusion.
    public string Definition;
    // Which way this definition is known to be wrong, stated up front:
    // "overcounts", "undercounts" or "exact".
    public string Bias;
    public int[] SamplesByBucket;
    public long TotalSamples;
    // TotalSamples x the capture's sample period. Zero on a wall-clock
    // capture, where the product would be a number with no referent.
    public double TotalCpuMSec;
    public bool HasCorrelation;
    public double Correlation;
    public double BestLagCorrelation;
    public int BestLagBuckets;
    public double BestLagMSec;
}

public static class ContentionOverviewBuilder
{
    // The grid is 100 buckets to match the CPU sample timeline it aligns to
    // (CpuProfileJsonExporter's own timelineBucketCount) and the existing
    // contention timeline beside it. Finer buckets were tempting for the
    // percentile series, but every series on this tab shares one x-axis and a
    // second grid would have to be resampled onto the first to overlay - which
    // is the one thing the correlation number must not be built on.
    private const int FallbackBucketCount = 100;

    // How far the lag scan reaches, as a fraction of the grid. A lag longer
    // than a quarter of the capture is comparing a series against a shrinking
    // fraction of itself, where a high r means nothing.
    private const double MaxLagFraction = 0.25;
    private const int MaxLagBucketsCap = 20;

    // Below this many overlapping buckets a correlation is not worth
    // reporting - r over a handful of points is noise wearing a number.
    private const int MinCorrelationBucketCount = 8;

    // |r| at or below this is treated as having no sign at all, so a series
    // sitting at +0.003 is not counted as "agreeing positively" with one at
    // +0.5.
    private const double SignDeadband = 0.05;

    // The band below which a coefficient is reported as no meaningful linear
    // relationship. Stated here and printed by the UI so a reader can
    // disagree with the threshold rather than only with the conclusion.
    private const double MeaningfulCorrelation = 0.3;

    // One thread's blocked window.
    private readonly struct BlockedInterval
    {
        public readonly double StartMSec;
        public readonly double EndMSec;

        public BlockedInterval(double startMSec, double endMSec)
        {
            this.StartMSec = startMSec;
            this.EndMSec = endMSec;
        }
    }

    public static ContentionOverview Build(
        ReadOnlySpan<ContentionEvent> contentionEvents,
        List<GcEvent> gcEvents,
        CpuProfileJsonExporter.SampleTimeline cpuSampleTimeline,
        double captureDurationMSec,
        bool samplingIsCpuTime = false)
    {
        ContentionOverview overview = new ContentionOverview();

        if (contentionEvents.Length == 0)
        {
            return overview;
        }

        overview.HasData = true;
        overview.ContentionCount = contentionEvents.Length;
        overview.HasCaptureDuration = captureDurationMSec > 0;
        overview.CaptureDurationMSec = captureDurationMSec;

        // ------------------------------------------------------------------
        // Grid. Taken from the CPU sample timeline when there is one, so the
        // CPU overlay needs no resampling at all (see header).
        // ------------------------------------------------------------------
        double minRelativeMSec;
        double bucketDurationMSec;
        int bucketCount;

        // Deliberately two separate conditions. A capture can have a perfectly
        // good sample timeline whose CPU-BOUND histogram is unavailable (an
        // older parser build, a timeline rebuilt from the binary container);
        // that should still align the grid to the samples, and simply omit the
        // CPU series. Folding the two together silently moved every other
        // series onto the fallback grid as well.
        bool hasCpuGrid = cpuSampleTimeline != null
            && cpuSampleTimeline.BucketCount > 0
            && cpuSampleTimeline.BucketDurationMSec > 0;

        bool hasCpuSeries = hasCpuGrid
            && cpuSampleTimeline.CpuBoundSamplesByBucket != null
            && cpuSampleTimeline.CpuBoundSamplesByBucket.Length >= cpuSampleTimeline.BucketCount;

        if (hasCpuGrid)
        {
            minRelativeMSec = cpuSampleTimeline.MinRelativeMSec;
            bucketDurationMSec = cpuSampleTimeline.BucketDurationMSec;
            bucketCount = cpuSampleTimeline.BucketCount;
            overview.GridSource = "cpu";
        }
        else
        {
            double contentionMinMSec = double.MaxValue;
            double contentionMaxMSec = double.MinValue;

            for (int eventIndex = 0; eventIndex < contentionEvents.Length; ++eventIndex)
            {
                ref readonly ContentionEvent contentionEvent = ref contentionEvents[eventIndex];

                if (contentionEvent.RelativeMSec < contentionMinMSec)
                {
                    contentionMinMSec = contentionEvent.RelativeMSec;
                }

                double endMSec = contentionEvent.RelativeMSec + (contentionEvent.DurationMSec > 0 ? contentionEvent.DurationMSec : 0);

                if (endMSec > contentionMaxMSec)
                {
                    contentionMaxMSec = endMSec;
                }
            }

            double spanMSec = contentionMaxMSec - contentionMinMSec;

            if (spanMSec <= 0)
            {
                spanMSec = 1.0;
            }

            minRelativeMSec = contentionMinMSec;
            bucketCount = contentionEvents.Length < FallbackBucketCount ? contentionEvents.Length : FallbackBucketCount;

            if (bucketCount < 1)
            {
                bucketCount = 1;
            }

            bucketDurationMSec = spanMSec / bucketCount;
            overview.GridSource = "contention";
        }

        overview.MinRelativeMSec = minRelativeMSec;
        overview.BucketDurationMSec = bucketDurationMSec;
        overview.BucketCount = bucketCount;

        double gridEndMSec = minRelativeMSec + (bucketDurationMSec * bucketCount);

        // ------------------------------------------------------------------
        // Whole-capture percentiles, and the per-bucket start-attributed
        // series that share the same sort.
        // ------------------------------------------------------------------
        BuildDurationSeries(contentionEvents, overview, minRelativeMSec, bucketDurationMSec, bucketCount);

        // ------------------------------------------------------------------
        // Blocked-time sweep: union wall clock, summed thread time and peak
        // concurrency, all per bucket, from one pass over the intervals.
        // ------------------------------------------------------------------
        BuildBlockedTimeSeries(contentionEvents, overview, minRelativeMSec, bucketDurationMSec, bucketCount, gridEndMSec);

        overview.BlockedWallClockPercent = overview.HasCaptureDuration
            ? overview.BlockedWallClockMSec * 100.0 / captureDurationMSec
            : 0;
        overview.AverageThreadsBlocked = overview.HasCaptureDuration
            ? overview.TotalWaitMSec / captureDurationMSec
            : 0;

        // ------------------------------------------------------------------
        // Overlays.
        // ------------------------------------------------------------------
        overview.SamplingSemantics = samplingIsCpuTime ? "cpuTime" : "wallClock";

        if (hasCpuSeries)
        {
            overview.HasCpuSamples = true;
            overview.CpuBoundSamplesByBucket = cpuSampleTimeline.CpuBoundSamplesByBucket;
            overview.HasSampleTypeData = cpuSampleTimeline.HasSampleTypeData;
            overview.HasCpuTime = cpuSampleTimeline.HasCpuTime;
            overview.SamplePeriodMSec = cpuSampleTimeline.SamplePeriodMSec;
            BuildCpuSeries(overview, cpuSampleTimeline, samplingIsCpuTime);

            if (overview.HasCpuTime)
            {
                // "All samples" is the whole process's CPU on a perf capture,
                // which is what makes cores-busy meaningful; the narrower
                // definitions are subsets of it.
                CpuCorrelationSeries allSamples = FindSeries(overview, "allSamples");

                if (allSamples != null)
                {
                    overview.TotalCpuMSec = allSamples.TotalCpuMSec;
                    overview.AverageCoresBusy = captureDurationMSec > 0
                        ? allSamples.TotalCpuMSec / captureDurationMSec
                        : 0;
                }
            }
        }

        BuildGcPauseSeries(gcEvents, overview, minRelativeMSec, bucketDurationMSec, bucketCount, gridEndMSec);

        if (overview.HasCpuSamples)
        {
            ComputeCorrelations(overview);
        }

        return overview;
    }

    // Percentiles of individual wait durations, whole-capture and per bucket.
    //
    // Buckets by START time (see header) and sorts each bucket's durations in
    // place inside one shared array: a counting pass gives each bucket its
    // offset, a fill pass places every duration into its bucket's run, and
    // Array.Sort then runs per run. Two arrays of length n total, versus a
    // List<double> per bucket.
    private static void BuildDurationSeries(
        ReadOnlySpan<ContentionEvent> contentionEvents,
        ContentionOverview overview,
        double minRelativeMSec,
        double bucketDurationMSec,
        int bucketCount)
    {
        int eventCount = contentionEvents.Length;

        int[] countByBucket = new int[bucketCount];
        int[] bucketIndexByEvent = new int[eventCount];

        double totalWaitMSec = 0;
        double maxWaitMSec = double.MinValue;
        double maxWaitStartMSec = 0;
        long maxWaitThreadId = 0;

        for (int eventIndex = 0; eventIndex < eventCount; ++eventIndex)
        {
            ref readonly ContentionEvent contentionEvent = ref contentionEvents[eventIndex];

            totalWaitMSec += contentionEvent.DurationMSec;

            if (contentionEvent.DurationMSec > maxWaitMSec)
            {
                maxWaitMSec = contentionEvent.DurationMSec;
                maxWaitStartMSec = contentionEvent.RelativeMSec;
                maxWaitThreadId = contentionEvent.ThreadId;
            }

            int bucketIndex = ToBucketIndex(contentionEvent.RelativeMSec, minRelativeMSec, bucketDurationMSec, bucketCount);
            bucketIndexByEvent[eventIndex] = bucketIndex;
            ++countByBucket[bucketIndex];
        }

        overview.TotalWaitMSec = totalWaitMSec;
        overview.MeanWaitMSec = eventCount > 0 ? totalWaitMSec / eventCount : 0;
        overview.MaxWaitMSec = maxWaitMSec;
        overview.MaxWaitStartMSec = maxWaitStartMSec;
        overview.MaxWaitThreadId = maxWaitThreadId;
        overview.ContentionCountByBucket = countByBucket;

        int[] bucketOffsets = new int[bucketCount + 1];
        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            bucketOffsets[bucketIndex + 1] = bucketOffsets[bucketIndex] + countByBucket[bucketIndex];
        }

        double[] durationsByBucket = new double[eventCount];
        int[] fillCursor = new int[bucketCount];

        for (int eventIndex = 0; eventIndex < eventCount; ++eventIndex)
        {
            int bucketIndex = bucketIndexByEvent[eventIndex];
            durationsByBucket[bucketOffsets[bucketIndex] + fillCursor[bucketIndex]] = contentionEvents[eventIndex].DurationMSec;
            ++fillCursor[bucketIndex];
        }

        double[] p50ByBucket = new double[bucketCount];
        double[] p99ByBucket = new double[bucketCount];
        double[] maxByBucket = new double[bucketCount];

        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            int offset = bucketOffsets[bucketIndex];
            int count = countByBucket[bucketIndex];

            if (count == 0)
            {
                continue;
            }

            Array.Sort(durationsByBucket, offset, count);

            p50ByBucket[bucketIndex] = NearestRank(durationsByBucket, offset, count, 0.50);
            p99ByBucket[bucketIndex] = NearestRank(durationsByBucket, offset, count, 0.99);
            maxByBucket[bucketIndex] = durationsByBucket[offset + count - 1];
        }

        overview.P50WaitMSecByBucket = p50ByBucket;
        overview.P99WaitMSecByBucket = p99ByBucket;
        overview.MaxWaitMSecByBucket = maxByBucket;

        // Whole-capture percentiles need a globally sorted view. The
        // bucket-ordered array above is already sorted WITHIN each run and the
        // runs are in time order, not value order, so it cannot be reused -
        // sort a copy rather than re-deriving from the events a third time.
        double[] allDurations = new double[eventCount];
        Array.Copy(durationsByBucket, allDurations, eventCount);
        Array.Sort(allDurations);

        overview.P50WaitMSec = NearestRank(allDurations, 0, eventCount, 0.50);
        overview.P75WaitMSec = NearestRank(allDurations, 0, eventCount, 0.75);
        overview.P90WaitMSec = NearestRank(allDurations, 0, eventCount, 0.90);
        overview.P95WaitMSec = NearestRank(allDurations, 0, eventCount, 0.95);
        overview.P99WaitMSec = NearestRank(allDurations, 0, eventCount, 0.99);
        overview.P999WaitMSec = NearestRank(allDurations, 0, eventCount, 0.999);
    }

    // Nearest-rank percentile over sorted[offset .. offset + count).
    // Deliberately not interpolated: every value this returns is a duration
    // some thread really waited, so it can be found in the Longest Waits list
    // rather than being a number that exists only in this function.
    private static double NearestRank(double[] sorted, int offset, int count, double fraction)
    {
        if (count <= 0)
        {
            return 0;
        }

        int rank = (int)Math.Ceiling(fraction * count) - 1;

        if (rank < 0)
        {
            rank = 0;
        }

        if (rank >= count)
        {
            rank = count - 1;
        }

        return sorted[offset + rank];
    }

    // One sweep over every blocked window produces all three time series.
    //
    // Starts and ends are sorted separately and merged, rather than building a
    // single tagged point array: two double[] sorts against one array of a
    // 2-field struct is both less memory and less work, and the merge is the
    // same walk either way.
    //
    // Between consecutive event points the number of blocked threads is
    // constant, so each such span contributes (a) its overlap with each bucket
    // to that bucket's union total whenever the count is above zero, (b)
    // count x overlap to the summed thread time, and (c) its count to each
    // overlapped bucket's running peak.
    private static void BuildBlockedTimeSeries(
        ReadOnlySpan<ContentionEvent> contentionEvents,
        ContentionOverview overview,
        double minRelativeMSec,
        double bucketDurationMSec,
        int bucketCount,
        double gridEndMSec)
    {
        double[] blockedWallClockByBucket = new double[bucketCount];
        double[] blockedThreadMSecByBucket = new double[bucketCount];
        int[] peakByBucket = new int[bucketCount];

        overview.BlockedWallClockMSecByBucket = blockedWallClockByBucket;
        overview.BlockedThreadMSecByBucket = blockedThreadMSecByBucket;
        overview.PeakThreadsBlockedByBucket = peakByBucket;

        int intervalCount = 0;
        double[] startMSecs = new double[contentionEvents.Length];
        double[] endMSecs = new double[contentionEvents.Length];

        for (int eventIndex = 0; eventIndex < contentionEvents.Length; ++eventIndex)
        {
            ref readonly ContentionEvent contentionEvent = ref contentionEvents[eventIndex];

            // A non-positive duration occupies no wall clock and would create
            // an inverted interval the sweep cannot merge - same guard
            // TimeBreakdownBuilder.ComputeContendedWallClockMSec applies.
            if (contentionEvent.DurationMSec <= 0)
            {
                continue;
            }

            startMSecs[intervalCount] = contentionEvent.RelativeMSec;
            endMSecs[intervalCount] = contentionEvent.RelativeMSec + contentionEvent.DurationMSec;
            ++intervalCount;
        }

        if (intervalCount == 0)
        {
            return;
        }

        Array.Sort(startMSecs, 0, intervalCount);
        Array.Sort(endMSecs, 0, intervalCount);

        int startIndex = 0;
        int endIndex = 0;
        int activeCount = 0;
        double previousMSec = startMSecs[0];
        double blockedWallClockMSec = 0;
        int peakThreadsBlocked = 0;
        double peakThreadsBlockedAtMSec = startMSecs[0];

        while (endIndex < intervalCount)
        {
            bool nextIsStart = startIndex < intervalCount && startMSecs[startIndex] <= endMSecs[endIndex];
            double pointMSec = nextIsStart ? startMSecs[startIndex] : endMSecs[endIndex];

            if (activeCount > 0 && pointMSec > previousMSec)
            {
                blockedWallClockMSec += pointMSec - previousMSec;
                DistributeAcrossBuckets(previousMSec, pointMSec, activeCount, blockedWallClockByBucket, blockedThreadMSecByBucket, peakByBucket, minRelativeMSec, bucketDurationMSec, bucketCount, gridEndMSec);
            }

            if (nextIsStart)
            {
                ++activeCount;
                ++startIndex;

                if (activeCount > peakThreadsBlocked)
                {
                    peakThreadsBlocked = activeCount;
                    peakThreadsBlockedAtMSec = pointMSec;
                }
            }
            else
            {
                --activeCount;
                ++endIndex;
            }

            previousMSec = pointMSec;
        }

        overview.BlockedWallClockMSec = blockedWallClockMSec;
        overview.PeakThreadsBlocked = peakThreadsBlocked;
        overview.PeakThreadsBlockedAtMSec = peakThreadsBlockedAtMSec;
    }

    // Adds one constant-concurrency span to every bucket it overlaps. The span
    // is clipped to the grid rather than clamped into the end buckets: a wait
    // running past the last sample would otherwise pile its whole remaining
    // duration onto the final bucket and invent a spike there.
    private static void DistributeAcrossBuckets(
        double spanStartMSec,
        double spanEndMSec,
        int activeCount,
        double[] blockedWallClockByBucket,
        double[] blockedThreadMSecByBucket,
        int[] peakByBucket,
        double minRelativeMSec,
        double bucketDurationMSec,
        int bucketCount,
        double gridEndMSec)
    {
        if (spanStartMSec < minRelativeMSec)
        {
            spanStartMSec = minRelativeMSec;
        }

        if (spanEndMSec > gridEndMSec)
        {
            spanEndMSec = gridEndMSec;
        }

        if (spanEndMSec <= spanStartMSec)
        {
            return;
        }

        int firstBucket = ToBucketIndex(spanStartMSec, minRelativeMSec, bucketDurationMSec, bucketCount);
        int lastBucket = ToBucketIndex(spanEndMSec, minRelativeMSec, bucketDurationMSec, bucketCount);

        for (int bucketIndex = firstBucket; bucketIndex <= lastBucket; ++bucketIndex)
        {
            double bucketStartMSec = minRelativeMSec + (bucketIndex * bucketDurationMSec);
            double bucketEndMSec = bucketStartMSec + bucketDurationMSec;

            double overlapStartMSec = spanStartMSec > bucketStartMSec ? spanStartMSec : bucketStartMSec;
            double overlapEndMSec = spanEndMSec < bucketEndMSec ? spanEndMSec : bucketEndMSec;
            double overlapMSec = overlapEndMSec - overlapStartMSec;

            if (overlapMSec <= 0)
            {
                continue;
            }

            blockedWallClockByBucket[bucketIndex] += overlapMSec;
            blockedThreadMSecByBucket[bucketIndex] += overlapMSec * activeCount;

            if (activeCount > peakByBucket[bucketIndex])
            {
                peakByBucket[bucketIndex] = activeCount;
            }
        }
    }

    // GC pause per bucket, spread across the buckets each pause overlaps for
    // the same reason lock waits are - a 300ms pause inside a 3s bucket is
    // 300ms of that bucket, and a pause straddling a boundary belongs to both.
    private static void BuildGcPauseSeries(
        List<GcEvent> gcEvents,
        ContentionOverview overview,
        double minRelativeMSec,
        double bucketDurationMSec,
        int bucketCount,
        double gridEndMSec)
    {
        if (gcEvents == null || gcEvents.Count == 0)
        {
            return;
        }

        double[] gcPauseByBucket = new double[bucketCount];
        bool sawPause = false;

        for (int gcIndex = 0; gcIndex < gcEvents.Count; ++gcIndex)
        {
            GcEvent gcEvent = gcEvents[gcIndex];

            if (gcEvent.PauseDurationMSec <= 0)
            {
                continue;
            }

            double pauseStartMSec = gcEvent.PauseStartRelativeMSec;
            double pauseEndMSec = pauseStartMSec + gcEvent.PauseDurationMSec;

            if (pauseStartMSec < minRelativeMSec)
            {
                pauseStartMSec = minRelativeMSec;
            }

            if (pauseEndMSec > gridEndMSec)
            {
                pauseEndMSec = gridEndMSec;
            }

            if (pauseEndMSec <= pauseStartMSec)
            {
                continue;
            }

            sawPause = true;

            int firstBucket = ToBucketIndex(pauseStartMSec, minRelativeMSec, bucketDurationMSec, bucketCount);
            int lastBucket = ToBucketIndex(pauseEndMSec, minRelativeMSec, bucketDurationMSec, bucketCount);

            for (int bucketIndex = firstBucket; bucketIndex <= lastBucket; ++bucketIndex)
            {
                double bucketStartMSec = minRelativeMSec + (bucketIndex * bucketDurationMSec);
                double bucketEndMSec = bucketStartMSec + bucketDurationMSec;

                double overlapStartMSec = pauseStartMSec > bucketStartMSec ? pauseStartMSec : bucketStartMSec;
                double overlapEndMSec = pauseEndMSec < bucketEndMSec ? pauseEndMSec : bucketEndMSec;

                if (overlapEndMSec > overlapStartMSec)
                {
                    gcPauseByBucket[bucketIndex] += overlapEndMSec - overlapStartMSec;
                }
            }
        }

        if (!sawPause)
        {
            return;
        }

        overview.HasGcPauses = true;
        overview.GcPauseMSecByBucket = gcPauseByBucket;
    }

    // Assembles the candidate CPU definitions, most-inclusive first.
    //
    // On a perf-based capture (collect-linux, v6) the ordering inverts in
    // meaning: perf's cpu-clock only fires on a thread that actually holds a
    // core, so "all samples" IS CPU utilisation there and the narrowing
    // definitions below merely subset it. On a v5 EventPipe capture the
    // runtime samples every thread on a wall clock, so "all samples" is close
    // to meaningless as a CPU measure and is offered only so a reader can see
    // that for themselves.
    private static void BuildCpuSeries(ContentionOverview overview, CpuProfileJsonExporter.SampleTimeline cpuSampleTimeline, bool samplingIsCpuTime)
    {
        AddCpuSeries(
            overview,
            "allSamples",
            "All samples",
            samplingIsCpuTime
                ? "Every CPU sample. This capture is perf-sampled, so a sample only fires on a thread that held a core - this IS CPU time."
                : "Every sample the runtime took. It samples every thread on a wall clock whether or not it held a core, so this tracks thread count more than CPU.",
            samplingIsCpuTime ? "exact" : "overcounts",
            cpuSampleTimeline.SamplesByBucket,
            overview.BucketCount);

        AddCpuSeries(
            overview,
            "notBlockingPrimitive",
            "Not parked in a known primitive",
            "Samples whose innermost frame is not a known BCL blocking primitive (Monitor, WaitHandle, semaphores, Thread.Sleep, Interop+Sys.Poll).",
            samplingIsCpuTime ? "exact" : "overcounts - a thread parked in a native call has a managed leaf frame that reads like running code",
            cpuSampleTimeline.CpuBoundSamplesByBucket,
            overview.BucketCount);

        if (!cpuSampleTimeline.HasSampleTypeData)
        {
            return;
        }

        AddCpuSeries(
            overview,
            "managed",
            "Executing managed code",
            "Samples the runtime itself flagged as Managed rather than External - it knows whether the thread was in managed code, in a P/Invoke, or in a syscall.",
            "undercounts - excludes all native CPU work (crypto, compression, the GC's own native code)",
            cpuSampleTimeline.ManagedSamplesByBucket,
            overview.BucketCount);

        AddCpuSeries(
            overview,
            "managedRunning",
            "Managed and not parked",
            "Both of the above at once: the runtime flagged the sample Managed AND its innermost frame is not a known blocking primitive.",
            "undercounts - the narrowest definition, but every sample in it is unambiguous",
            cpuSampleTimeline.ManagedRunningSamplesByBucket,
            overview.BucketCount);
    }

    private static CpuCorrelationSeries FindSeries(ContentionOverview overview, string id)
    {
        for (int seriesIndex = 0; seriesIndex < overview.CpuSeries.Count; ++seriesIndex)
        {
            if (overview.CpuSeries[seriesIndex].Id == id)
            {
                return overview.CpuSeries[seriesIndex];
            }
        }

        return null;
    }

    private static void AddCpuSeries(ContentionOverview overview, string id, string label, string definition, string bias, int[] samplesByBucket, int bucketCount)
    {
        if (samplesByBucket == null || samplesByBucket.Length < bucketCount)
        {
            return;
        }

        long totalSamples = 0;
        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            totalSamples += samplesByBucket[bucketIndex];
        }

        CpuCorrelationSeries series = new CpuCorrelationSeries();
        series.TotalCpuMSec = overview.HasCpuTime ? totalSamples * overview.SamplePeriodMSec : 0;
        series.Id = id;
        series.Label = label;
        series.Definition = definition;
        series.Bias = bias;
        series.SamplesByBucket = samplesByBucket;
        series.TotalSamples = totalSamples;

        overview.CpuSeries.Add(series);
    }

    // Pearson r between blocked thread-time and each CPU definition, per
    // bucket, plus a lag scan on each.
    //
    // WHAT THIS IS NOT: a significance test, and not evidence of causation.
    // Both series are heavily autocorrelated (a lock convoy and a CPU spike
    // each span many buckets), which inflates r far above what independent
    // samples of the same size would justify, and no p-value computed against
    // an independence assumption would be honest here.
    //
    // A positive lag means CPU rises AFTER lock blocking. Buckets, not
    // milliseconds, are the unit the scan works in; BestLagMSec converts it
    // once for display.
    private static void ComputeCorrelations(ContentionOverview overview)
    {
        double[] blockedThreadMSecByBucket = overview.BlockedThreadMSecByBucket;
        int bucketCount = overview.BucketCount;

        if (blockedThreadMSecByBucket == null || bucketCount < MinCorrelationBucketCount)
        {
            overview.CorrelationAgreement = "none";
            return;
        }

        overview.CorrelationBucketCount = bucketCount;

        int maxLag = (int)(bucketCount * MaxLagFraction);

        if (maxLag > MaxLagBucketsCap)
        {
            maxLag = MaxLagBucketsCap;
        }

        for (int seriesIndex = 0; seriesIndex < overview.CpuSeries.Count; ++seriesIndex)
        {
            CpuCorrelationSeries series = overview.CpuSeries[seriesIndex];

            double zeroLagCorrelation;

            if (!TryPearson(blockedThreadMSecByBucket, series.SamplesByBucket, bucketCount, 0, out zeroLagCorrelation))
            {
                continue;
            }

            series.HasCorrelation = true;
            series.Correlation = zeroLagCorrelation;
            series.BestLagCorrelation = zeroLagCorrelation;
            series.BestLagBuckets = 0;
            series.BestLagMSec = 0;

            for (int lag = -maxLag; lag <= maxLag; ++lag)
            {
                if (lag == 0)
                {
                    continue;
                }

                double laggedCorrelation;

                if (!TryPearson(blockedThreadMSecByBucket, series.SamplesByBucket, bucketCount, lag, out laggedCorrelation))
                {
                    continue;
                }

                if (laggedCorrelation > series.BestLagCorrelation)
                {
                    series.BestLagCorrelation = laggedCorrelation;
                    series.BestLagBuckets = lag;
                    series.BestLagMSec = lag * overview.BucketDurationMSec;
                }
            }
        }

        ClassifyAgreement(overview);
    }

    // The verdict that makes several correlations worth computing.
    //
    // Rules, with their thresholds stated because the UI prints them:
    //   - Fewer than two measurable definitions -> "single"/"none": nothing to
    //     cross-check against, so no claim of robustness is made.
    //   - Definitions whose |r| is at or below SignDeadband are treated as
    //     having no sign at all rather than as weakly agreeing with whatever
    //     side of zero they happen to land on.
    //   - Signed definitions pointing BOTH ways -> "disagree". No headline
    //     coefficient is reported, because the capture does not settle the
    //     question: which answer you get depends on how the ambiguous
    //     External samples are treated, and only native stacks resolve that.
    //   - All one way, but every |r| below MeaningfulCorrelation -> a weak
    //     "inconclusive".
    //   - Otherwise "agree".
    private static void ClassifyAgreement(ContentionOverview overview)
    {
        int measuredCount = 0;
        int positiveCount = 0;
        int negativeCount = 0;
        double minCorrelation = double.MaxValue;
        double maxCorrelation = double.MinValue;
        double strongestMagnitude = 0;

        for (int seriesIndex = 0; seriesIndex < overview.CpuSeries.Count; ++seriesIndex)
        {
            CpuCorrelationSeries series = overview.CpuSeries[seriesIndex];

            if (!series.HasCorrelation)
            {
                continue;
            }

            ++measuredCount;

            if (series.Correlation < minCorrelation)
            {
                minCorrelation = series.Correlation;
            }

            if (series.Correlation > maxCorrelation)
            {
                maxCorrelation = series.Correlation;
            }

            double magnitude = Math.Abs(series.Correlation);

            if (magnitude > strongestMagnitude)
            {
                strongestMagnitude = magnitude;
            }

            if (series.Correlation > SignDeadband)
            {
                ++positiveCount;
            }
            else if (series.Correlation < -SignDeadband)
            {
                ++negativeCount;
            }
        }

        if (measuredCount == 0)
        {
            overview.CorrelationAgreement = "none";
            return;
        }

        overview.MinCorrelation = minCorrelation;
        overview.MaxCorrelation = maxCorrelation;

        if (measuredCount == 1)
        {
            overview.CorrelationAgreement = "single";
            return;
        }

        if (positiveCount > 0 && negativeCount > 0)
        {
            overview.CorrelationAgreement = "disagree";
            return;
        }

        overview.CorrelationAgreement = strongestMagnitude < MeaningfulCorrelation ? "inconclusive" : "agree";
    }

    // Pearson r between blocked[i] and cpu[i + lag] over the overlapping
    // range. Returns false when either series is constant across that range -
    // r is undefined there (zero variance), and returning 0 would read as
    // "measured, no relationship" rather than "could not be measured".
    private static bool TryPearson(double[] blocked, int[] cpu, int bucketCount, int lag, out double correlation)
    {
        correlation = 0;

        int firstIndex = lag < 0 ? -lag : 0;
        int lastIndex = lag < 0 ? bucketCount - 1 : bucketCount - 1 - lag;
        int pairCount = lastIndex - firstIndex + 1;

        if (pairCount < MinCorrelationBucketCount)
        {
            return false;
        }

        double blockedSum = 0;
        double cpuSum = 0;

        for (int index = firstIndex; index <= lastIndex; ++index)
        {
            blockedSum += blocked[index];
            cpuSum += cpu[index + lag];
        }

        double blockedMean = blockedSum / pairCount;
        double cpuMean = cpuSum / pairCount;

        double covariance = 0;
        double blockedVariance = 0;
        double cpuVariance = 0;

        for (int index = firstIndex; index <= lastIndex; ++index)
        {
            double blockedDelta = blocked[index] - blockedMean;
            double cpuDelta = cpu[index + lag] - cpuMean;

            covariance += blockedDelta * cpuDelta;
            blockedVariance += blockedDelta * blockedDelta;
            cpuVariance += cpuDelta * cpuDelta;
        }

        if (blockedVariance <= 0 || cpuVariance <= 0)
        {
            return false;
        }

        correlation = covariance / Math.Sqrt(blockedVariance * cpuVariance);

        return true;
    }

    private static int ToBucketIndex(double relativeMSec, double minRelativeMSec, double bucketDurationMSec, int bucketCount)
    {
        if (bucketDurationMSec <= 0)
        {
            return 0;
        }

        int bucketIndex = (int)((relativeMSec - minRelativeMSec) / bucketDurationMSec);

        if (bucketIndex < 0)
        {
            bucketIndex = 0;
        }

        if (bucketIndex >= bucketCount)
        {
            bucketIndex = bucketCount - 1;
        }

        return bucketIndex;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Contention)
