////////////////////////////////////////////////////////////////////////////////
// Module: SampleProfileEventProjector.cs
//
// Notes:
// Sibling projector to Gc/AllocationEventProjector.cs, against the same
// EventRecord stream - filters for the Microsoft-DotNETCore-SampleProfiler
// provider's one real event (EventId 0, no EventName - self-describing
// metadata with zero declared fields, the same "manifest-based, empty
// EventName" shape the CLR runtime provider has - see EventRecord.cs's own
// header comment). Confirmed against a real capture
// (testApps/CpuLoadGenerator/example-cpu-sample.nettrace, captured via
// `dotnet-trace collect -p <pid>` with no --profile override - its default
// profile set includes Microsoft-DotNETCore-SampleProfiler): every decoded
// event on this provider was EventId=0, Version=0, with a non-empty
// EventRecord.Stack already resolved (see EventBlock.cs/StackBlock.cs -
// stack resolution isn't provider-specific, so this "just worked" with no
// changes to that layer). Fires at a fixed sampling interval (~100 Hz by
// default) on every managed thread the runtime is actively running - no
// per-event payload fields are needed here, only the event's own resolved
// Stack/ThreadId/timestamp.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A readonly struct, not a class - mirrors AllocationEvent's own reasoning
// (see that type's header comment): SampleProfileEventProjector.Project can
// construct many thousands of these for a real capture, all held in one
// List<SampleEvent> for the rest of the program's life.
public readonly struct SampleEvent
{
    public readonly double RelativeMSec;
    public readonly long ThreadId;
    // Copied directly from the owning EventRecord.Stack, already resolved at
    // parse time (see EventBlock.cs/EventRecord.cs) - leaf-first (index 0 is
    // the innermost/currently-executing frame), the same order every other
    // Stack in this codebase carries. Empty (Array.Empty<long>()), never
    // null, on the rare sample that couldn't be stack-walked.
    public readonly long[] Stack;

    public SampleEvent(double relativeMSec, long threadId, long[] stack)
    {
        this.RelativeMSec = relativeMSec;
        this.ThreadId = threadId;
        this.Stack = stack;
    }
}

public static class SampleProfileEventProjector
{
    private const string SampleProfilerProviderName = "Microsoft-DotNETCore-SampleProfiler";
    private const int ThreadSampleEventId = 0;

    // Same (events, qpcFrequency, referenceQpc) shape as
    // GcEventProjector.Project/AllocationEventProjector.Project - see those
    // for why referenceQpc (NettraceHeader.SyncTimeQPC) is the correct
    // anchor. No pointerSize/referenceUtc parameters here (unlike those two)
    // - a sample carries no pointer-sized payload fields to decode, and
    // every consumer of SampleEvent only needs capture-relative time, not an
    // absolute DateTime.
    public static List<SampleEvent> Project(List<EventRecord> events, long qpcFrequency, long referenceQpc, Action<double> onProgress = null)
    {
        List<SampleEvent> result = new List<SampleEvent>();

        // EventRecord is a struct (~70 bytes) - events is the whole
        // capture's event list, so this is iterated as a Span over the
        // List<T>'s backing array rather than a plain `foreach`, matching
        // GcEventProjector.Project/AllocationEventProjector.Project's own
        // reasoning.
        Span<EventRecord> eventsSpan = CollectionsMarshal.AsSpan(events);
        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            if (onProgress != null && (eventIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress((double)eventIndex / eventsSpan.Length);
            }

            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            if (record.ProviderName != SampleProfilerProviderName)
            {
                continue;
            }

            if (record.EventId != ThreadSampleEventId)
            {
                continue;
            }

            double relativeMSec = default;
            if (qpcFrequency > 0)
            {
                long qpcDelta = record.TimeStampRelativeQPC - referenceQpc;
                relativeMSec = qpcDelta * 1000.0 / qpcFrequency;
            }

            result.Add(new SampleEvent(relativeMSec, record.ThreadId, record.Stack));
        }

        return result;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
