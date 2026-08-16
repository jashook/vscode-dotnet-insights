////////////////////////////////////////////////////////////////////////////////
// Module: TimeBreakdownBuilder.cs
//
// Notes:
// Backs the Overview page's four summary tiles: % time spent contending
// locks, % time in GC, and two sample-based ESTIMATES (% time idle, % time
// CPU-bound). The first two are exact (measured per-event durations divided
// by the capture's wall-clock span - captureDurationMSec, computed once in
// Program.cs from the raw event list before it's discarded), not sampled,
// and both are bounded by 100%, so all four tiles read on the same scale.
//
// Contention gets there via a UNION of blocked windows, not a sum: threads
// block concurrently, so summing their waits counts the same instant once
// per blocked thread and is not a percentage of anything. That sum over
// capture duration is what this used to report, and it rendered as
// "Contending Locks 426.1%" on a real capture - see
// ComputeContendedWallClockMSec for the full account. The concurrency that
// number really encoded is still reported, as AverageThreadsBlocked, which
// is explicitly not a percentage.
//
// GC needs no such treatment: GcEventProjector only ever records one global
// suspend/restart window at a time, so its pauses cannot overlap - see
// Gc/GcEventProjector.cs's own PauseDurationMSec comments.
//
// Idle/CPU-bound are a genuinely different kind of number: CpuIdleWaitClassifier
// classifies each CPU sample's own leaf (currently-executing) frame as a
// known blocking primitive or not, and the two percentages are just that
// count's own share of totalSampleCount - i.e. the same "self % by sample
// count" methodology PerfView/dotnet-trace's own CPU views already use, not
// a derived time value. This sidesteps ever needing to convert a sample
// count into milliseconds (no verified per-capture sampling-interval
// constant exists in this codebase - see NettraceHeader.ExpectedCPUSamplingRate's
// own comment) and means idle% + cpuBound% always sum to exactly 100% between
// themselves, unlike the GC/contention pair above.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Overview {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public readonly struct TimeBreakdown
{
    // False for a degenerate capture (zero/near-zero wall-clock span) -
    // GcPercent/ContentionPercent are meaningless (division by ~0) and
    // should not be rendered, same "null when there's nothing real to show"
    // convention as every other JSON-null-gated section in this codebase.
    public readonly bool HasCaptureDuration;
    public readonly double CaptureDurationMSec;
    public readonly double GcPercent;
    // Total stop-the-world GC pause across the capture. Exposed alongside the
    // percentage so the UI can show the absolute cost, not just the ratio - a
    // small percentage of a long capture can still be a lot of seconds.
    public readonly double GcPauseMSec;
    // Percentage of wall-clock time during which at least one thread was
    // blocked on a lock - a union, so it is genuinely bounded by 100% and
    // directly comparable to GcPercent/IdlePercent/CpuBoundPercent beside it.
    // See ComputeContendedWallClockMSec for what this used to be and why that
    // was wrong.
    public readonly double ContentionPercent;
    // SUMMED lock wait across every blocked thread. This is the number that
    // divided by capture duration used to be (wrongly) rendered as
    // ContentionPercent - it is real and worth showing, it just is not a
    // percentage. Exceeding CaptureDurationMSec is the signal that threads
    // were piling up on locks rather than merely waiting occasionally.
    public readonly double ContentionWaitMSec;
    // Average number of threads blocked on a lock at any instant (summed wait
    // over capture duration). Unbounded by nature - 4.26 on a real capture -
    // and deliberately NOT a percentage.
    public readonly double AverageThreadsBlocked;

    // False when the capture has no CPU sample events at all (e.g. captured
    // without the SampleProfiler provider) - IdlePercent/CpuBoundPercent are
    // meaningless (0/0) and should not be rendered.
    public readonly bool HasCpuSampleBreakdown;
    public readonly double IdlePercent;
    public readonly double CpuBoundPercent;

    public TimeBreakdown(bool hasCaptureDuration, double captureDurationMSec, double gcPercent, double gcPauseMSec, double contentionPercent, double contentionWaitMSec, double averageThreadsBlocked, bool hasCpuSampleBreakdown, double idlePercent, double cpuBoundPercent)
    {
        this.HasCaptureDuration = hasCaptureDuration;
        this.CaptureDurationMSec = captureDurationMSec;
        this.GcPercent = gcPercent;
        this.GcPauseMSec = gcPauseMSec;
        this.ContentionPercent = contentionPercent;
        this.ContentionWaitMSec = contentionWaitMSec;
        this.AverageThreadsBlocked = averageThreadsBlocked;
        this.HasCpuSampleBreakdown = hasCpuSampleBreakdown;
        this.IdlePercent = idlePercent;
        this.CpuBoundPercent = cpuBoundPercent;
    }
}

public static class TimeBreakdownBuilder
{
    public static TimeBreakdown Build(List<GcEvent> gcEvents, List<ContentionEvent> contentionEvents, List<SampleEvent> sampleEvents, StackTable stackTable, MethodSymbolTable symbolTable, double captureDurationMSec)
    {
        bool hasCaptureDuration = captureDurationMSec > 0;

        double gcPauseMSec = 0;
        for (int gcIndex = 0; gcIndex < gcEvents.Count; ++gcIndex)
        {
            gcPauseMSec += gcEvents[gcIndex].PauseDurationMSec;
        }

        double contentionWaitMSec = 0;
        Span<ContentionEvent> contentionSpan = CollectionsMarshal.AsSpan(contentionEvents);
        for (int contentionIndex = 0; contentionIndex < contentionSpan.Length; ++contentionIndex)
        {
            contentionWaitMSec += contentionSpan[contentionIndex].DurationMSec;
        }

        double contendedWallClockMSec = ComputeContendedWallClockMSec(contentionSpan);

        double gcPercent = hasCaptureDuration ? gcPauseMSec * 100.0 / captureDurationMSec : 0;
        double contentionPercent = hasCaptureDuration ? contendedWallClockMSec * 100.0 / captureDurationMSec : 0;
        double averageThreadsBlocked = hasCaptureDuration ? contentionWaitMSec / captureDurationMSec : 0;

        bool hasCpuSampleBreakdown = sampleEvents.Count > 0;
        double idlePercent = 0;
        double cpuBoundPercent = 0;

        if (hasCpuSampleBreakdown)
        {
            int idleSampleCount = 0;
            Span<SampleEvent> sampleSpan = CollectionsMarshal.AsSpan(sampleEvents);

            // One classification per DISTINCT leaf method, not one per sample -
            // see Cpu/IdleWaitFrameCache.cs. This loop runs once per CPU sample
            // in the whole capture (16.24M of them on a real 3.23GB capture),
            // and the classifier it used to call directly walks 18 string
            // comparisons to reach the same answer for the same few thousand
            // methods every time.
            IdleWaitFrameCache idleWaitCache = new IdleWaitFrameCache(symbolTable);

            for (int sampleIndex = 0; sampleIndex < sampleSpan.Length; ++sampleIndex)
            {
                ref readonly SampleEvent sampleEvent = ref sampleSpan[sampleIndex];

                long[] stackFrames = stackTable.FramesAt(sampleEvent.StackIndex);
                if (stackFrames.Length == 0)
                {
                    continue;
                }

                int leafFrameId = symbolTable.ResolveId(stackFrames[0], sampleEvent.RelativeMSec);

                if (idleWaitCache.IsIdleWaitFrame(leafFrameId))
                {
                    ++idleSampleCount;
                }
            }

            idlePercent = idleSampleCount * 100.0 / sampleEvents.Count;
            cpuBoundPercent = 100.0 - idlePercent;
        }

        return new TimeBreakdown(hasCaptureDuration, captureDurationMSec, gcPercent, gcPauseMSec, contentionPercent, contentionWaitMSec, averageThreadsBlocked, hasCpuSampleBreakdown, idlePercent, cpuBoundPercent);
    }

    // One contention event's blocked window on the wall clock.
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

    // A named static method rather than a lambda at the Array.Sort call site,
    // per this project's delegate convention.
    private static int CompareByStartAscending(BlockedInterval left, BlockedInterval right)
    {
        return left.StartMSec.CompareTo(right.StartMSec);
    }

    // Wall-clock milliseconds during which AT LEAST ONE thread was blocked on
    // a lock - the union of every contention event's blocked window, not their
    // sum.
    //
    // The sum is what this used to divide by capture duration, and it is not a
    // percentage of anything: threads block concurrently, so summing their
    // waits counts the same instant once per blocked thread. On a real
    // 3.01GB capture that produced "Contending Locks 426.1%" on the Overview's
    // Time Breakdown tile, sitting next to GC/Idle/CPU-Bound which are all
    // genuinely bounded - so it read as a broken number AND implied it was a
    // slice of the same 100%. The underlying data was fine (744,411ms of wait
    // over a 174,688ms capture, one lock alone having 152 distinct waiter
    // threads); only the metric was wrong.
    //
    // The concurrency that the old value actually encoded is not thrown away -
    // it is reported separately and correctly as AverageThreadsBlocked
    // (sum / duration, i.e. 4.26 threads blocked on average for that capture),
    // which is a meaningful figure in its own right, just not a percentage.
    private static double ComputeContendedWallClockMSec(Span<ContentionEvent> contentionEvents)
    {
        if (contentionEvents.Length == 0)
        {
            return 0;
        }

        BlockedInterval[] intervals = new BlockedInterval[contentionEvents.Length];
        int intervalCount = 0;

        for (int contentionIndex = 0; contentionIndex < contentionEvents.Length; ++contentionIndex)
        {
            ref readonly ContentionEvent contentionEvent = ref contentionEvents[contentionIndex];

            // A non-positive duration contributes no wall-clock time and would
            // otherwise create an inverted interval that breaks the merge below.
            if (contentionEvent.DurationMSec <= 0)
            {
                continue;
            }

            intervals[intervalCount] = new BlockedInterval(contentionEvent.RelativeMSec, contentionEvent.RelativeMSec + contentionEvent.DurationMSec);
            ++intervalCount;
        }

        if (intervalCount == 0)
        {
            return 0;
        }

        // Events arrive in stream order, which is only approximately time
        // order across blocks, so the merge below cannot assume sortedness.
        Array.Sort(intervals, 0, intervalCount, Comparer<BlockedInterval>.Create(CompareByStartAscending));

        double contendedMSec = 0;
        double currentStartMSec = intervals[0].StartMSec;
        double currentEndMSec = intervals[0].EndMSec;

        for (int intervalIndex = 1; intervalIndex < intervalCount; ++intervalIndex)
        {
            BlockedInterval interval = intervals[intervalIndex];

            if (interval.StartMSec > currentEndMSec)
            {
                contendedMSec += currentEndMSec - currentStartMSec;
                currentStartMSec = interval.StartMSec;
                currentEndMSec = interval.EndMSec;
            }
            else if (interval.EndMSec > currentEndMSec)
            {
                currentEndMSec = interval.EndMSec;
            }
        }

        contendedMSec += currentEndMSec - currentStartMSec;

        return contendedMSec;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Overview)
