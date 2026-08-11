////////////////////////////////////////////////////////////////////////////////
// Module: TimeBreakdownBuilder.cs
//
// Notes:
// Backs the Overview page's four summary tiles: % time spent contending
// locks, % time in GC, and two sample-based ESTIMATES (% time idle, % time
// CPU-bound). The first two are computed the same way - sum a real, exactly-
// measured per-event duration and divide by the whole capture's wall-clock
// span (captureDurationMSec, computed once in Program.cs from the raw event
// list before it's discarded - see that file's own comment) - so they are
// exact, not sampled, and can each independently exceed 100% on a heavily
// multi-threaded capture (e.g. contending-lock wait time summed across many
// concurrently-blocked threads can add up to more wall-clock-equivalent time
// than the capture itself spans; GC pause time cannot, since GCEventProjector
// only ever records one global suspend/restart window at a time - see
// Gc/GcEventProjector.cs's own PauseDurationMSec comments). Callers should
// present all four as independent tiles, not slices of one 100% total.
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
    public readonly double ContentionPercent;

    // False when the capture has no CPU sample events at all (e.g. captured
    // without the SampleProfiler provider) - IdlePercent/CpuBoundPercent are
    // meaningless (0/0) and should not be rendered.
    public readonly bool HasCpuSampleBreakdown;
    public readonly double IdlePercent;
    public readonly double CpuBoundPercent;

    public TimeBreakdown(bool hasCaptureDuration, double captureDurationMSec, double gcPercent, double contentionPercent, bool hasCpuSampleBreakdown, double idlePercent, double cpuBoundPercent)
    {
        this.HasCaptureDuration = hasCaptureDuration;
        this.CaptureDurationMSec = captureDurationMSec;
        this.GcPercent = gcPercent;
        this.ContentionPercent = contentionPercent;
        this.HasCpuSampleBreakdown = hasCpuSampleBreakdown;
        this.IdlePercent = idlePercent;
        this.CpuBoundPercent = cpuBoundPercent;
    }
}

public static class TimeBreakdownBuilder
{
    public static TimeBreakdown Build(List<GcEvent> gcEvents, List<ContentionEvent> contentionEvents, List<SampleEvent> sampleEvents, MethodSymbolTable symbolTable, double captureDurationMSec)
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

        double gcPercent = hasCaptureDuration ? gcPauseMSec * 100.0 / captureDurationMSec : 0;
        double contentionPercent = hasCaptureDuration ? contentionWaitMSec * 100.0 / captureDurationMSec : 0;

        bool hasCpuSampleBreakdown = sampleEvents.Count > 0;
        double idlePercent = 0;
        double cpuBoundPercent = 0;

        if (hasCpuSampleBreakdown)
        {
            int idleSampleCount = 0;
            Span<SampleEvent> sampleSpan = CollectionsMarshal.AsSpan(sampleEvents);

            for (int sampleIndex = 0; sampleIndex < sampleSpan.Length; ++sampleIndex)
            {
                ref readonly SampleEvent sampleEvent = ref sampleSpan[sampleIndex];

                if (sampleEvent.Stack.Length == 0)
                {
                    continue;
                }

                int leafFrameId = symbolTable.ResolveId(sampleEvent.Stack[0], sampleEvent.RelativeMSec);
                string leafFrameName = symbolTable.NameForId(leafFrameId);

                if (CpuIdleWaitClassifier.IsKnownIdleWaitLeafMethodName(leafFrameName))
                {
                    ++idleSampleCount;
                }
            }

            idlePercent = idleSampleCount * 100.0 / sampleEvents.Count;
            cpuBoundPercent = 100.0 - idlePercent;
        }

        return new TimeBreakdown(hasCaptureDuration, captureDurationMSec, gcPercent, contentionPercent, hasCpuSampleBreakdown, idlePercent, cpuBoundPercent);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Overview)
