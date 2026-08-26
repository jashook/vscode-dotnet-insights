////////////////////////////////////////////////////////////////////////////////
// Module: ContentionOverviewTests.cs
//
// Notes:
// Pins the arithmetic behind the Contention view's Overview tab, which is all
// of the kind that stays plausible when it goes wrong - a wrong percentile
// still returns a duration, a wrong attribution still draws a chart with a
// spike in it. Each test here corresponds to a decision recorded in
// ContentionOverviewBuilder.cs's header:
//
//   - Union vs sum. Two threads blocked over the same second are one second
//     of blocked wall clock and two thread-seconds of blocked thread time.
//     Reporting the sum as the wall clock is the bug
//     Overview/TimeBreakdownBuilder.cs already had to fix once.
//   - Long waits are SPREAD across the buckets they span, not banked at their
//     start. This is the whole reason the tab exists ("very long locks"), and
//     the failure mode is a spike drawn in the wrong place.
//   - Percentiles are nearest-rank over waits attributed to the bucket they
//     STARTED in, which is the opposite attribution from the blocked-time
//     series on purpose.
//   - The CPU series is only ever the CPU-BOUND subset of samples, and is
//     absent rather than substituted when that is unavailable.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Gc;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ContentionOverviewTests
{
    private static ContentionEvent MakeEvent(double relativeMSec, double durationMSec, long waiterThreadId = 100)
    {
        return new ContentionEvent(relativeMSec, durationMSec, ClrContentionFlags.Managed, waiterThreadId, StackTable.EmptyStackIndex);
    }

    // A grid with known, round boundaries, so a test asserts against numbers
    // it states rather than against whatever the fallback bucketing derived.
    private static CpuProfileJsonExporter.SampleTimeline MakeGrid(double minRelativeMSec, double bucketDurationMSec, int bucketCount, int[] cpuBoundSamplesByBucket = null)
    {
        CpuProfileJsonExporter.SampleTimeline timeline = new CpuProfileJsonExporter.SampleTimeline();
        timeline.MinRelativeMSec = minRelativeMSec;
        timeline.BucketDurationMSec = bucketDurationMSec;
        timeline.BucketCount = bucketCount;
        timeline.TotalDurationMSec = bucketDurationMSec * bucketCount;
        timeline.SamplesByBucket = new int[bucketCount];
        timeline.CpuBoundSamplesByBucket = cpuBoundSamplesByBucket;
        return timeline;
    }

    // A timeline carrying all three CPU definitions, so the cross-definition
    // agreement logic can be exercised.
    private static CpuProfileJsonExporter.SampleTimeline MakeGridWithSampleTypes(double minRelativeMSec, double bucketDurationMSec, int bucketCount, int[] allSamples, int[] cpuBound, int[] managed, int[] managedRunning)
    {
        CpuProfileJsonExporter.SampleTimeline timeline = MakeGrid(minRelativeMSec, bucketDurationMSec, bucketCount, cpuBound);
        timeline.SamplesByBucket = allSamples;
        timeline.ManagedSamplesByBucket = managed;
        timeline.ManagedRunningSamplesByBucket = managedRunning;
        timeline.HasSampleTypeData = true;
        return timeline;
    }

    private static ContentionOverview Build(List<ContentionEvent> contentionEvents, CpuProfileJsonExporter.SampleTimeline timeline = null, List<GcEvent> gcEvents = null, double captureDurationMSec = 0, bool samplingIsCpuTime = false)
    {
        return ContentionOverviewBuilder.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(contentionEvents), gcEvents, timeline, captureDurationMSec, samplingIsCpuTime);
    }

    private static CpuCorrelationSeries SeriesById(ContentionOverview overview, string id)
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

    [Fact]
    public void Build_NoContentionEvents_ReportsNoData()
    {
        ContentionOverview overview = Build(new List<ContentionEvent>());

        Assert.False(overview.HasData);
    }

    // Two threads blocked across the same window. The union is the window; the
    // sum is twice it. Conflating them is what produced "Contending Locks
    // 426.1%" on a real capture (see TimeBreakdownBuilder).
    [Fact]
    public void Build_OverlappingWaits_UnionIsWallClockAndSumIsThreadTime()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            MakeEvent(relativeMSec: 0, durationMSec: 100, waiterThreadId: 1),
            MakeEvent(relativeMSec: 0, durationMSec: 100, waiterThreadId: 2),
        };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, 1, new int[1]), captureDurationMSec: 1000);

        Assert.Equal(100.0, overview.BlockedWallClockMSec, 6);
        Assert.Equal(100.0, overview.BlockedWallClockMSecByBucket[0], 6);
        Assert.Equal(200.0, overview.TotalWaitMSec, 6);
        Assert.Equal(200.0, overview.BlockedThreadMSecByBucket[0], 6);
        Assert.Equal(2, overview.PeakThreadsBlocked);
        Assert.Equal(2, overview.PeakThreadsBlockedByBucket[0]);
    }

    // Disjoint waits must NOT be merged - the union is their total, not the
    // span from the first start to the last end.
    [Fact]
    public void Build_DisjointWaits_UnionSumsRatherThanSpanning()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            MakeEvent(relativeMSec: 0, durationMSec: 10, waiterThreadId: 1),
            MakeEvent(relativeMSec: 900, durationMSec: 10, waiterThreadId: 2),
        };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 1000, 1, new int[1]), captureDurationMSec: 1000);

        Assert.Equal(20.0, overview.BlockedWallClockMSec, 6);
        Assert.Equal(1, overview.PeakThreadsBlocked);
    }

    // The headline behaviour of this whole tab: a wait far longer than one
    // bucket contributes to every bucket it covers, in proportion. Banking it
    // at its start bucket draws the stall in the wrong place, which is
    // precisely the misreading this view is meant to prevent.
    [Fact]
    public void Build_WaitLongerThanABucket_SpreadsAcrossEveryBucketItCovers()
    {
        // One 250ms wait starting halfway through bucket 0 of a 100ms grid:
        // 50ms in bucket 0, 100ms in bucket 1, 100ms in bucket 2, none after.
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            MakeEvent(relativeMSec: 50, durationMSec: 250),
        };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, 5, new int[5]), captureDurationMSec: 500);

        Assert.Equal(50.0, overview.BlockedWallClockMSecByBucket[0], 6);
        Assert.Equal(100.0, overview.BlockedWallClockMSecByBucket[1], 6);
        Assert.Equal(100.0, overview.BlockedWallClockMSecByBucket[2], 6);
        Assert.Equal(0.0, overview.BlockedWallClockMSecByBucket[3], 6);
        Assert.Equal(250.0, overview.BlockedWallClockMSec, 6);

        // ...while the wait's own DURATION percentile is attributed wholly to
        // the bucket it STARTED in - the opposite attribution, on purpose.
        Assert.Equal(1, overview.ContentionCountByBucket[0]);
        Assert.Equal(0, overview.ContentionCountByBucket[1]);
        Assert.Equal(250.0, overview.MaxWaitMSecByBucket[0], 6);
        Assert.Equal(0.0, overview.MaxWaitMSecByBucket[1], 6);
    }

    // A wait running past the end of the grid is clipped, not clamped into the
    // final bucket - clamping would pile the whole remainder onto the last
    // bucket and invent a spike there.
    [Fact]
    public void Build_WaitRunningPastGridEnd_IsClippedNotPiledOntoLastBucket()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            MakeEvent(relativeMSec: 150, durationMSec: 10000),
        };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, 2, new int[2]), captureDurationMSec: 200);

        Assert.Equal(50.0, overview.BlockedWallClockMSecByBucket[1], 6);
    }

    // Nearest-rank, so every reported percentile is a duration some thread
    // really waited. With 100 waits of 1..100ms, p50 is the 50th value and p99
    // the 99th - an interpolated definition would return 50.5 / 99.5, which is
    // a wait nobody had.
    [Fact]
    public void Build_Percentiles_AreNearestRankAndSoAreRealObservedWaits()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>();
        for (int waitIndex = 1; waitIndex <= 100; ++waitIndex)
        {
            contentionEvents.Add(MakeEvent(relativeMSec: waitIndex, durationMSec: waitIndex));
        }

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 1000, 1, new int[1]), captureDurationMSec: 1000);

        Assert.Equal(50.0, overview.P50WaitMSec, 6);
        Assert.Equal(90.0, overview.P90WaitMSec, 6);
        Assert.Equal(99.0, overview.P99WaitMSec, 6);
        Assert.Equal(100.0, overview.MaxWaitMSec, 6);
    }

    // The tail is the point: a mean dominated by thousands of trivial waits
    // says nothing about the one that stalled a request. Pinned so a
    // refactor that quietly averages instead fails here.
    [Fact]
    public void Build_HeavyTail_P99AndMaxDivergeSharplyFromTheMean()
    {
        // 980 trivial waits and 20 five-second ones. The split is 98/2 rather
        // than 99/1 on purpose: at exactly 99% the nearest-rank p99 lands on
        // the LAST trivial value (rank ceil(0.99 x 1000) - 1 = 989, still
        // inside the trivial run), which is correct but makes the test read
        // as though the tail were being missed.
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>();
        for (int waitIndex = 0; waitIndex < 980; ++waitIndex)
        {
            contentionEvents.Add(MakeEvent(relativeMSec: waitIndex, durationMSec: 0.1));
        }

        for (int waitIndex = 0; waitIndex < 20; ++waitIndex)
        {
            contentionEvents.Add(MakeEvent(relativeMSec: 980 + waitIndex, durationMSec: 5000));
        }

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 10000, 1, new int[1]), captureDurationMSec: 10000);

        Assert.Equal(0.1, overview.P50WaitMSec, 6);
        Assert.Equal(5000.0, overview.P99WaitMSec, 6);
        Assert.Equal(5000.0, overview.MaxWaitMSec, 6);
        // The mean describes neither population: three orders of magnitude
        // above the typical wait and more than an order below the tail. That
        // gap is the reason this tab reports percentiles at all.
        Assert.True(overview.MeanWaitMSec > overview.P50WaitMSec * 100, "mean was not far above the typical wait, was " + overview.MeanWaitMSec);
        Assert.True(overview.MeanWaitMSec < overview.P99WaitMSec / 20, "mean was not far below the tail, was " + overview.MeanWaitMSec);
    }

    [Fact]
    public void Build_MaxWait_CarriesItsOwnStartTimeAndThread()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            MakeEvent(relativeMSec: 10, durationMSec: 5, waiterThreadId: 1),
            MakeEvent(relativeMSec: 700, durationMSec: 250, waiterThreadId: 42),
            MakeEvent(relativeMSec: 900, durationMSec: 5, waiterThreadId: 3),
        };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 1000, 1, new int[1]), captureDurationMSec: 1000);

        Assert.Equal(250.0, overview.MaxWaitMSec, 6);
        Assert.Equal(700.0, overview.MaxWaitStartMSec, 6);
        Assert.Equal(42, overview.MaxWaitThreadId);
    }

    // AverageThreadsBlocked must match TimeBreakdown's own definition (summed
    // wait over capture duration) so the Overview page and this tab cannot
    // report two different numbers for the same concept.
    [Fact]
    public void Build_AverageThreadsBlocked_IsSummedWaitOverCaptureDuration()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            MakeEvent(relativeMSec: 0, durationMSec: 400, waiterThreadId: 1),
            MakeEvent(relativeMSec: 0, durationMSec: 400, waiterThreadId: 2),
            MakeEvent(relativeMSec: 0, durationMSec: 400, waiterThreadId: 3),
        };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 1000, 1, new int[1]), captureDurationMSec: 1000);

        Assert.Equal(1.2, overview.AverageThreadsBlocked, 6);
        Assert.Equal(40.0, overview.BlockedWallClockPercent, 6);
    }

    // The grid is the CPU timeline's own, reused verbatim - resampling either
    // series onto the other's boundaries would put a smoothing artifact
    // straight into the correlation this tab reports.
    [Fact]
    public void Build_WithCpuTimeline_ReusesItsGridExactly()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent> { MakeEvent(relativeMSec: 500, durationMSec: 10) };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(minRelativeMSec: 123.5, bucketDurationMSec: 250, bucketCount: 8, cpuBoundSamplesByBucket: new int[8]), captureDurationMSec: 2000);

        Assert.Equal("cpu", overview.GridSource);
        Assert.Equal(123.5, overview.MinRelativeMSec, 6);
        Assert.Equal(250.0, overview.BucketDurationMSec, 6);
        Assert.Equal(8, overview.BucketCount);
    }

    // No CPU-bound histogram means NO CPU series - never the raw sample count
    // standing in for it. .NET's profiler is wall-clock per thread, so raw
    // samples track thread count, not CPU, and a plausible wrong overlay on
    // the exact question being asked is worse than an absent one.
    //
    // The GRID is still taken from the sample timeline though: the two
    // decisions are independent, and coupling them silently moved every other
    // series onto the fallback grid whenever the histogram was missing.
    [Fact]
    public void Build_WithoutCpuBoundHistogram_OmitsTheCpuSeriesButKeepsTheGrid()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent> { MakeEvent(relativeMSec: 5, durationMSec: 10) };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, 4, cpuBoundSamplesByBucket: null), captureDurationMSec: 400);

        Assert.False(overview.HasCpuSamples);
        Assert.Null(overview.CpuBoundSamplesByBucket);
        Assert.Empty(overview.CpuSeries);
        Assert.Equal("cpu", overview.GridSource);
        Assert.Equal(4, overview.BucketCount);
    }

    // With no sample timeline at all, the grid falls back to the contention
    // events' own span.
    [Fact]
    public void Build_WithoutASampleTimeline_DerivesItsOwnGrid()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            MakeEvent(relativeMSec: 100, durationMSec: 10),
            MakeEvent(relativeMSec: 300, durationMSec: 10),
        };

        ContentionOverview overview = Build(contentionEvents, timeline: null, captureDurationMSec: 400);

        Assert.Equal("contention", overview.GridSource);
        Assert.Equal(100.0, overview.MinRelativeMSec, 6);
        Assert.Equal(2, overview.BucketCount);
        Assert.Equal(105.0, overview.BucketDurationMSec, 6);
    }

    // GC pauses ride along so a CPU spike landing on a gen2 pause can be told
    // apart from one landing on a lock convoy by eye. Spread across the
    // buckets they span, same rule as lock waits.
    [Fact]
    public void Build_GcPauses_AreSpreadAcrossTheBucketsTheySpan()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent> { MakeEvent(relativeMSec: 5, durationMSec: 1) };

        GcEvent gcEvent = new GcEvent();
        gcEvent.PauseStartRelativeMSec = 50;
        gcEvent.PauseDurationMSec = 120;

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, 4, cpuBoundSamplesByBucket: new int[4]), new List<GcEvent> { gcEvent }, captureDurationMSec: 400);

        Assert.True(overview.HasGcPauses);
        Assert.Equal(50.0, overview.GcPauseMSecByBucket[0], 6);
        Assert.Equal(70.0, overview.GcPauseMSecByBucket[1], 6);
        Assert.Equal(0.0, overview.GcPauseMSecByBucket[2], 6);
    }

    [Fact]
    public void Build_NoGcPauses_OmitsTheGcSeries()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent> { MakeEvent(relativeMSec: 5, durationMSec: 10) };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, 4, cpuBoundSamplesByBucket: new int[4]), new List<GcEvent>(), captureDurationMSec: 400);

        Assert.False(overview.HasGcPauses);
        Assert.Null(overview.GcPauseMSecByBucket);
    }

    // A series that rises exactly where blocked time rises correlates at ~1;
    // one that falls correlates at ~-1. Pins the sign, which is the only part
    // of a correlation anybody reads off first.
    [Fact]
    public void Build_Correlation_TracksSignOfTheRelationship()
    {
        int bucketCount = 20;
        int[] cpuRising = new int[bucketCount];
        int[] cpuFalling = new int[bucketCount];

        List<ContentionEvent> contentionEvents = new List<ContentionEvent>();

        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            contentionEvents.Add(MakeEvent(relativeMSec: bucketIndex * 100, durationMSec: bucketIndex + 1, waiterThreadId: bucketIndex));
            cpuRising[bucketIndex] = bucketIndex + 1;
            cpuFalling[bucketIndex] = bucketCount - bucketIndex;
        }

        ContentionOverview rising = Build(contentionEvents, MakeGrid(0, 100, bucketCount, cpuRising), captureDurationMSec: 2000);
        ContentionOverview falling = Build(contentionEvents, MakeGrid(0, 100, bucketCount, cpuFalling), captureDurationMSec: 2000);

        CpuCorrelationSeries risingSeries = SeriesById(rising, "notBlockingPrimitive");
        CpuCorrelationSeries fallingSeries = SeriesById(falling, "notBlockingPrimitive");

        Assert.True(risingSeries.HasCorrelation);
        Assert.True(risingSeries.Correlation > 0.9, "rising CPU against rising lock wait should correlate strongly positive, was " + risingSeries.Correlation);

        Assert.True(fallingSeries.HasCorrelation);
        Assert.True(fallingSeries.Correlation < -0.9, "falling CPU against rising lock wait should correlate strongly negative, was " + fallingSeries.Correlation);
    }

    // The lag scan is the part that speaks to "long locks LEAD TO cpu spikes":
    // a CPU series that is the blocked series shifted later must score best at
    // a positive lag, not at zero.
    [Fact]
    public void Build_Correlation_FindsAPositiveLagWhenCpuFollowsBlocking()
    {
        int bucketCount = 40;
        const int shiftBuckets = 3;

        int[] cpuBoundSamples = new int[bucketCount];
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>();

        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            bool isBurst = bucketIndex >= 10 && bucketIndex < 14;

            if (isBurst)
            {
                contentionEvents.Add(MakeEvent(relativeMSec: (bucketIndex * 100) + 1, durationMSec: 90, waiterThreadId: bucketIndex));
            }
            else
            {
                contentionEvents.Add(MakeEvent(relativeMSec: (bucketIndex * 100) + 1, durationMSec: 0.5, waiterThreadId: bucketIndex));
            }

            bool isCpuBurst = bucketIndex >= 10 + shiftBuckets && bucketIndex < 14 + shiftBuckets;
            cpuBoundSamples[bucketIndex] = isCpuBurst ? 1000 : 10;
        }

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, bucketCount, cpuBoundSamples), captureDurationMSec: 4000);
        CpuCorrelationSeries series = SeriesById(overview, "notBlockingPrimitive");

        Assert.True(series.HasCorrelation);
        Assert.Equal(shiftBuckets, series.BestLagBuckets);
        Assert.Equal(shiftBuckets * 100.0, series.BestLagMSec, 6);
        Assert.True(series.BestLagCorrelation > series.Correlation, "the shifted fit must beat the zero-lag fit");
    }

    // A constant CPU series has zero variance, so r is undefined there.
    // Reporting 0 would read as "measured, no relationship" rather than "could
    // not be measured".
    [Fact]
    public void Build_Correlation_AbsentWhenASeriesIsConstant()
    {
        int bucketCount = 20;
        int[] cpuBoundSamples = new int[bucketCount];
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>();

        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            contentionEvents.Add(MakeEvent(relativeMSec: bucketIndex * 100, durationMSec: bucketIndex + 1));
            cpuBoundSamples[bucketIndex] = 500;
        }

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, bucketCount, cpuBoundSamples), captureDurationMSec: 2000);
        CpuCorrelationSeries series = SeriesById(overview, "notBlockingPrimitive");

        Assert.False(series.HasCorrelation);
    }

    // THE POINT OF HAVING MORE THAN ONE DEFINITION. A v5 capture carries no
    // native stacks, so "was this thread working" is a judgement call, and on a
    // real 3.0GB capture the defensible answers produce r from -0.598 to
    // +0.248 - opposite signs - against the same blocked-time series. When that
    // happens the builder must say "disagree" rather than let a caller pick
    // whichever number it read first.
    [Fact]
    public void Build_Correlation_ReportsDisagreementWhenDefinitionsConflictInSign()
    {
        int bucketCount = 20;
        int[] allSamples = new int[bucketCount];
        int[] cpuBound = new int[bucketCount];
        int[] managed = new int[bucketCount];
        int[] managedRunning = new int[bucketCount];

        List<ContentionEvent> contentionEvents = new List<ContentionEvent>();

        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            contentionEvents.Add(MakeEvent(relativeMSec: bucketIndex * 100, durationMSec: bucketIndex + 1, waiterThreadId: bucketIndex));

            // The broad definitions FALL as blocking rises (parked threads
            // dominate them); the narrow managed ones RISE.
            allSamples[bucketIndex] = bucketCount - bucketIndex;
            cpuBound[bucketIndex] = bucketCount - bucketIndex;
            managed[bucketIndex] = bucketIndex + 1;
            managedRunning[bucketIndex] = bucketIndex + 1;
        }

        ContentionOverview overview = Build(contentionEvents, MakeGridWithSampleTypes(0, 100, bucketCount, allSamples, cpuBound, managed, managedRunning), captureDurationMSec: 2000);

        Assert.Equal("disagree", overview.CorrelationAgreement);
        Assert.True(overview.MinCorrelation < 0, "expected a negative extreme");
        Assert.True(overview.MaxCorrelation > 0, "expected a positive extreme");
        Assert.Equal(4, overview.CpuSeries.Count);
    }

    [Fact]
    public void Build_Correlation_ReportsAgreementWhenEveryDefinitionSharesASign()
    {
        int bucketCount = 20;
        int[] rising = new int[bucketCount];
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>();

        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            contentionEvents.Add(MakeEvent(relativeMSec: bucketIndex * 100, durationMSec: bucketIndex + 1, waiterThreadId: bucketIndex));
            rising[bucketIndex] = bucketIndex + 1;
        }

        ContentionOverview overview = Build(contentionEvents, MakeGridWithSampleTypes(0, 100, bucketCount, rising, rising, rising, rising), captureDurationMSec: 2000);

        Assert.Equal("agree", overview.CorrelationAgreement);
    }

    // All one sign, but every |r| under 0.3 - a real "we measured and there is
    // nothing here", distinct from both agreement and disagreement.
    [Fact]
    public void Build_Correlation_ReportsInconclusiveWhenEveryFitIsWeak()
    {
        int bucketCount = 40;
        int[] noisy = new int[bucketCount];
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>();

        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            contentionEvents.Add(MakeEvent(relativeMSec: bucketIndex * 100, durationMSec: (bucketIndex % 7) + 1, waiterThreadId: bucketIndex));
            // Weakly related to the sawtooth above, deliberately out of phase.
            noisy[bucketIndex] = 500 + ((bucketIndex % 11) * 3);
        }

        ContentionOverview overview = Build(contentionEvents, MakeGridWithSampleTypes(0, 100, bucketCount, noisy, noisy, noisy, noisy), captureDurationMSec: 4000);

        Assert.Equal("inconclusive", overview.CorrelationAgreement);
        Assert.True(Math.Abs(overview.MinCorrelation) < 0.3);
        Assert.True(Math.Abs(overview.MaxCorrelation) < 0.3);
    }

    // Without ThreadSampleType there are only two definitions and neither can
    // see a native park, so the extra managed rows must be absent rather than
    // silently duplicated from the blocking-primitive filter.
    [Fact]
    public void Build_WithoutSampleTypeData_OffersOnlyTheNameBasedDefinitions()
    {
        int bucketCount = 20;
        int[] rising = new int[bucketCount];
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>();

        for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
        {
            contentionEvents.Add(MakeEvent(relativeMSec: bucketIndex * 100, durationMSec: bucketIndex + 1, waiterThreadId: bucketIndex));
            rising[bucketIndex] = bucketIndex + 1;
        }

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, bucketCount, rising), captureDurationMSec: 2000);

        Assert.False(overview.HasSampleTypeData);
        Assert.Null(SeriesById(overview, "managed"));
        Assert.Null(SeriesById(overview, "managedRunning"));
        Assert.NotNull(SeriesById(overview, "notBlockingPrimitive"));
    }

    // The sampling semantics decide whether ANY of these series can be read as
    // CPU utilisation, so the flag has to survive into the model.
    [Fact]
    public void Build_SamplingSemantics_DistinguishesPerfFromWallClock()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent> { MakeEvent(relativeMSec: 5, durationMSec: 10) };

        ContentionOverview wallClock = Build(contentionEvents, MakeGrid(0, 100, 4, new int[4]), captureDurationMSec: 400);
        ContentionOverview cpuTime = Build(contentionEvents, MakeGrid(0, 100, 4, new int[4]), captureDurationMSec: 400, samplingIsCpuTime: true);

        Assert.Equal("wallClock", wallClock.SamplingSemantics);
        Assert.Equal("cpuTime", cpuTime.SamplingSemantics);

        // ...and the "all samples" row's own bias text flips with it, since on
        // a perf capture that row is exact rather than an overcount.
        Assert.Equal("overcounts", SeriesById(wallClock, "allSamples").Bias);
        Assert.Equal("exact", SeriesById(cpuTime, "allSamples").Bias);
    }

    // Non-positive durations occupy no wall clock and would invert an interval
    // the sweep cannot merge - same guard TimeBreakdownBuilder applies.
    [Fact]
    public void Build_NonPositiveDurations_ContributeNoBlockedTime()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            MakeEvent(relativeMSec: 10, durationMSec: 0),
            MakeEvent(relativeMSec: 20, durationMSec: -5),
        };

        ContentionOverview overview = Build(contentionEvents, MakeGrid(0, 100, 4, cpuBoundSamplesByBucket: new int[4]), captureDurationMSec: 400);

        Assert.True(overview.HasData);
        Assert.Equal(0.0, overview.BlockedWallClockMSec, 6);
        Assert.Equal(0, overview.PeakThreadsBlocked);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
