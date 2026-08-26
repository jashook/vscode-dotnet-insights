////////////////////////////////////////////////////////////////////////////////
// Module: SamplePeriodEstimator.cs
//
// Notes:
// Recovers "how much time does one CPU sample stand for" from the capture
// itself, which is what turns a sample COUNT into CPU TIME.
//
// This only means anything on a capture whose sampler is driven by CPU time.
// `dotnet-trace collect-linux` is: it samples through Linux perf_events on
// cpu-clock, which only advances while a thread is actually on a core, so a
// sample is a fixed quantum of CPU time and N samples is N x period of CPU
// time. Microsoft's own documentation states the default as "once every
// millisecond (per processor)" and says to "read each sample as roughly 1 ms
// of CPU time".
//
// The .NET runtime's own sampler (a v5 EventPipe capture) is NOT: it walks
// every managed thread on a wall clock whether or not that thread holds a
// core, so a sample count there is a thread-time count and no multiplication
// turns it into CPU time. Callers must not ask for a period on a v5 capture -
// see Estimate's own guard.
//
// WHY IT IS MEASURED RATHER THAN HARDCODED TO 1ms: the rate is a capture-time
// choice, and a capture taken at a non-default rate would otherwise report CPU
// time wrong by exactly that factor while looking entirely plausible. Deriving
// it also fails loudly (returns false) on a capture that does not have a
// stable period at all, which is the honest outcome for input this cannot
// interpret.
//
// HOW: histogram the gap between consecutive samples ON THE SAME THREAD, and
// take the mode. A thread that is running continuously is sampled exactly one
// period apart; a thread that gets descheduled produces longer, arbitrary
// gaps. The short-gap mode is therefore the period, and the long tail is
// scheduling. Verified on a real 764MB collect-linux capture: 102,518 gaps
// land in the two 25us buckets straddling 1000us, against ~7,000 in each
// bucket of the flat tail beyond 1300us - an unmistakable spike. The same
// histogram over a v5 capture has NO spike at all, just a broad hump around
// 1450us, which is the wall-clock sampler's own walk cadence smeared by
// jitter - and is exactly why this must not be run on one.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class SamplePeriodEstimator
{
    // Gap histogram resolution. 25us is fine enough to separate a 1ms period
    // from a 1.25ms one and coarse enough that timestamp jitter still lands
    // neighbouring samples in the same bucket or its neighbour.
    private const double BucketWidthMSec = 0.025;

    // Gaps longer than this are a descheduled thread, not a sampling period,
    // and only add noise to the histogram.
    private const double MaxConsideredGapMSec = 50.0;

    // Periods outside this range are not a plausible sampling rate (40kHz to
    // 20Hz) and indicate the mode found something else.
    private const double MinPlausiblePeriodMSec = 0.025;
    private const double MaxPlausiblePeriodMSec = 50.0;

    // The mode has to actually be a spike. A wall-clock capture's histogram is
    // a broad smear with no bucket standing out, and accepting its highest
    // bucket would produce a confident, wrong CPU-time figure - the exact
    // failure this whole file exists to avoid. Measured on real captures: the
    // collect-linux spike is ~7.5x its own tail, the v5 hump's peak is ~1.1x
    // its neighbours.
    private const double MinModeToMedianRatio = 3.0;

    // Too few gaps to trust a mode.
    private const int MinGapCount = 1000;

    // Estimates the per-sample CPU-time quantum in milliseconds.
    //
    // Returns false - and callers must then report no CPU time at all rather
    // than substituting a default - when the capture has too few samples, or
    // when its inter-sample gaps have no clear mode, which is what a
    // wall-clock-sampled capture looks like.
    public static bool TryEstimatePeriodMSec(ReadOnlySpan<SampleEvent> sampleEvents, out double periodMSec, out int gapCount)
    {
        periodMSec = 0;
        gapCount = 0;

        if (sampleEvents.Length < MinGapCount)
        {
            return false;
        }

        Dictionary<long, double> lastSeenMSecByThread = new Dictionary<long, double>();
        Dictionary<int, int> countByBucket = new Dictionary<int, int>();

        for (int sampleIndex = 0; sampleIndex < sampleEvents.Length; ++sampleIndex)
        {
            ref readonly SampleEvent sampleEvent = ref sampleEvents[sampleIndex];

            double previousMSec;

            if (lastSeenMSecByThread.TryGetValue(sampleEvent.ThreadId, out previousMSec))
            {
                double gapMSec = sampleEvent.RelativeMSec - previousMSec;

                if (gapMSec > 0 && gapMSec < MaxConsideredGapMSec)
                {
                    int bucketIndex = (int)(gapMSec / BucketWidthMSec);

                    int existingCount;
                    countByBucket.TryGetValue(bucketIndex, out existingCount);
                    countByBucket[bucketIndex] = existingCount + 1;
                    ++gapCount;
                }
            }

            lastSeenMSecByThread[sampleEvent.ThreadId] = sampleEvent.RelativeMSec;
        }

        if (gapCount < MinGapCount || countByBucket.Count == 0)
        {
            return false;
        }

        int modeBucketIndex = -1;
        int modeCount = 0;

        List<int> allCounts = new List<int>(countByBucket.Count);

        foreach (KeyValuePair<int, int> bucket in countByBucket)
        {
            allCounts.Add(bucket.Value);

            if (bucket.Value > modeCount)
            {
                modeCount = bucket.Value;
                modeBucketIndex = bucket.Key;
            }
        }

        // A period this close to a bucket boundary splits across two adjacent
        // buckets (1ms landed 52,424/50,094 across the 975us and 1000us
        // buckets on the reference capture), so the neighbour is folded in
        // before the spike test - otherwise a real spike can look half as tall
        // as it is purely because of where the boundary fell.
        int lowerCount = 0;
        int upperCount = 0;
        countByBucket.TryGetValue(modeBucketIndex - 1, out lowerCount);
        countByBucket.TryGetValue(modeBucketIndex + 1, out upperCount);

        int neighbourCount;
        int foldedNeighbourIndex;

        if (lowerCount >= upperCount && lowerCount > 0)
        {
            neighbourCount = lowerCount;
            foldedNeighbourIndex = modeBucketIndex - 1;
        }
        else
        {
            neighbourCount = upperCount;
            foldedNeighbourIndex = modeBucketIndex + 1;
        }

        int foldedModeCount = modeCount + neighbourCount;

        allCounts.Sort();
        int medianCount = allCounts[allCounts.Count / 2];

        if (medianCount <= 0)
        {
            medianCount = 1;
        }

        if (foldedModeCount < medianCount * MinModeToMedianRatio)
        {
            // No spike: a smeared histogram, i.e. not a CPU-time sampler.
            return false;
        }

        // Weighted centre of the mode and the neighbour it split across, taken
        // at each bucket's midpoint.
        double modeCentreMSec = (modeBucketIndex + 0.5) * BucketWidthMSec;
        double neighbourCentreMSec = (foldedNeighbourIndex + 0.5) * BucketWidthMSec;

        periodMSec = neighbourCount > 0
            ? ((modeCentreMSec * modeCount) + (neighbourCentreMSec * neighbourCount)) / foldedModeCount
            : modeCentreMSec;

        if (periodMSec < MinPlausiblePeriodMSec || periodMSec > MaxPlausiblePeriodMSec)
        {
            periodMSec = 0;
            return false;
        }

        return true;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)
