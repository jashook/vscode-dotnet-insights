////////////////////////////////////////////////////////////////////////////////
// Module: SamplePeriodEstimatorTests.cs
//
// Notes:
// The estimator turns a sample COUNT into CPU TIME, so its failure mode is a
// confident wrong number of seconds rather than a crash - and the number it
// would be wrong by is exactly the ratio between the real period and the
// guessed one.
//
// The tests that matter most are the REFUSALS. A wall-clock capture has no
// per-sample CPU quantum at all, and the estimator must decline rather than
// return its histogram's tallest bucket: the shapes are genuinely different
// (a collect-linux capture spikes ~1500x over its own median, a v5 capture's
// hump is barely above its neighbours), and that difference is the only thing
// standing between "1091 CPU-seconds" and a fabrication.
//
// The synthetic fixtures below reproduce both shapes: a CPU-time sampler emits
// a thread's samples exactly one period apart while it runs and leaves ragged
// gaps when it is descheduled; a wall-clock sampler emits every thread on a
// jittered cadence with no sharp mode anywhere.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Cpu;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class SamplePeriodEstimatorTests
{
    private static SampleEvent MakeSample(double relativeMSec, long threadId)
    {
        return new SampleEvent(relativeMSec, threadId, StackTable.EmptyStackIndex, ThreadSampleType.Unknown);
    }

    // A CPU-time sampler: while a thread is on a core it is sampled exactly
    // one period apart. Every so often it is descheduled, which produces one
    // long ragged gap - the tail this estimator has to see past.
    private static List<SampleEvent> MakeCpuTimeSamples(double periodMSec, int threadCount, int samplesPerThread)
    {
        List<SampleEvent> samples = new List<SampleEvent>();
        Random random = new Random(20260825);

        for (long threadId = 1; threadId <= threadCount; ++threadId)
        {
            double currentMSec = threadId * 0.37;

            for (int sampleIndex = 0; sampleIndex < samplesPerThread; ++sampleIndex)
            {
                samples.Add(MakeSample(currentMSec, threadId));

                if (sampleIndex % 20 == 19)
                {
                    // Descheduled: an arbitrary gap, uniformly spread so it
                    // cannot itself form a competing mode.
                    currentMSec += 3.0 + (random.NextDouble() * 20.0);
                }
                else
                {
                    currentMSec += periodMSec;
                }
            }
        }

        samples.Sort((SampleEvent left, SampleEvent right) => left.RelativeMSec.CompareTo(right.RelativeMSec));

        return samples;
    }

    // A wall-clock sampler: every thread is walked on a nominal cadence, but
    // the walk itself drifts, so the gaps smear across a wide band with no
    // sharp mode. This is the shape a real v5 capture has.
    private static List<SampleEvent> MakeWallClockSamples(double nominalMSec, int threadCount, int samplesPerThread)
    {
        List<SampleEvent> samples = new List<SampleEvent>();
        Random random = new Random(20260826);

        for (long threadId = 1; threadId <= threadCount; ++threadId)
        {
            double currentMSec = threadId * 0.11;

            for (int sampleIndex = 0; sampleIndex < samplesPerThread; ++sampleIndex)
            {
                samples.Add(MakeSample(currentMSec, threadId));

                // +/- 40% jitter, which is wide enough that no 25us bucket
                // accumulates a spike.
                currentMSec += nominalMSec * (0.6 + (random.NextDouble() * 0.8));
            }
        }

        samples.Sort((SampleEvent left, SampleEvent right) => left.RelativeMSec.CompareTo(right.RelativeMSec));

        return samples;
    }

    // The headline case: 1ms is what dotnet-trace collect-linux uses by
    // default, and what a real capture measured at 1.0003ms.
    [Fact]
    public void TryEstimate_CpuTimeSampler_RecoversTheOneMillisecondPeriod()
    {
        List<SampleEvent> samples = MakeCpuTimeSamples(periodMSec: 1.0, threadCount: 8, samplesPerThread: 400);

        double periodMSec;
        int gapCount;
        bool estimated = SamplePeriodEstimator.TryEstimatePeriodMSec(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples), out periodMSec, out gapCount);

        Assert.True(estimated, "the estimator declined a clean CPU-time capture");
        Assert.InRange(periodMSec, 0.95, 1.05);
    }

    // A capture taken at a non-default rate must be measured, not assumed -
    // assuming 1ms here would report CPU time 4x too high.
    [Fact]
    public void TryEstimate_NonDefaultRate_IsMeasuredRatherThanAssumed()
    {
        List<SampleEvent> samples = MakeCpuTimeSamples(periodMSec: 4.0, threadCount: 8, samplesPerThread: 400);

        double periodMSec;
        int gapCount;
        bool estimated = SamplePeriodEstimator.TryEstimatePeriodMSec(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples), out periodMSec, out gapCount);

        Assert.True(estimated);
        Assert.InRange(periodMSec, 3.9, 4.1);
    }

    // THE MOST IMPORTANT TEST HERE. A wall-clock capture has no CPU-time
    // quantum, and returning its tallest histogram bucket would turn a thread
    // count into a fabricated seconds figure.
    [Fact]
    public void TryEstimate_WallClockSampler_Declines()
    {
        List<SampleEvent> samples = MakeWallClockSamples(nominalMSec: 1.45, threadCount: 40, samplesPerThread: 300);

        double periodMSec;
        int gapCount;
        bool estimated = SamplePeriodEstimator.TryEstimatePeriodMSec(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples), out periodMSec, out gapCount);

        Assert.False(estimated, "a wall-clock capture was given a CPU-time period, which would fabricate CPU seconds");
        Assert.Equal(0.0, periodMSec);
    }

    // Too little data to find a mode at all.
    [Fact]
    public void TryEstimate_TooFewSamples_Declines()
    {
        List<SampleEvent> samples = MakeCpuTimeSamples(periodMSec: 1.0, threadCount: 1, samplesPerThread: 50);

        double periodMSec;
        int gapCount;
        bool estimated = SamplePeriodEstimator.TryEstimatePeriodMSec(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples), out periodMSec, out gapCount);

        Assert.False(estimated);
    }

    // Every sample landing on one thread microseconds apart is what a capture
    // with broken thread attribution looks like - observed on a real
    // collect-linux capture where all 38,791,404 samples carried one thread id.
    // The implied "period" is then a few microseconds, which is not a sampling
    // rate, and the plausibility floor is what catches it.
    [Fact]
    public void TryEstimate_CollapsedThreadAttribution_Declines()
    {
        List<SampleEvent> samples = new List<SampleEvent>();
        double currentMSec = 0;

        for (int sampleIndex = 0; sampleIndex < 20000; ++sampleIndex)
        {
            samples.Add(MakeSample(currentMSec, threadId: 1));
            currentMSec += 0.012;
        }

        double periodMSec;
        int gapCount;
        bool estimated = SamplePeriodEstimator.TryEstimatePeriodMSec(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples), out periodMSec, out gapCount);

        Assert.False(estimated, "a sub-25us implied period is not a sampling rate and must be refused");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
