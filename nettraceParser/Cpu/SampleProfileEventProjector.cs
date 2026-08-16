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
// stack already resolved (see EventBlock.cs/StackBlock.cs -
// stack resolution isn't provider-specific, so this "just worked" with no
// changes to that layer). Fires at a fixed sampling interval (~100 Hz by
// default) on every managed thread the runtime is actively running - no
// per-event payload fields are needed here, only the event's own resolved
// stack index/ThreadId/timestamp.
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
    // Index into the capture's StackTable (see StackTable.cs), copied from
    // the owning EventRecord.StackIndex - resolved at parse time, see
    // EventBlock.cs. Resolve it with StackTable.FramesAt, which returns
    // frames leaf-first (index 0 is the innermost/currently-executing frame),
    // the same order every stack in this codebase carries.
    // StackTable.EmptyStackIndex on the rare sample that couldn't be
    // stack-walked - that index resolves to an empty array, never null.
    public readonly int StackIndex;

    public SampleEvent(double relativeMSec, long threadId, int stackIndex)
    {
        this.RelativeMSec = relativeMSec;
        this.ThreadId = threadId;
        this.StackIndex = stackIndex;
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
    // The provider/event id this projector filters for, exposed so a caller
    // holding an EventOverview can ask it for this capture's exact sample
    // count and pass it as expectedSampleCount below.
    public static string ProviderName => SampleProfilerProviderName;
    public static int EventId => ThreadSampleEventId;

    // expectedSampleCount presizes the result list. A real capture can hold
    // 16.24M samples (3.23GB assets-registry capture), and growing there from
    // empty measured 33% of this whole phase - every doubling copies every
    // SampleEvent already added. 0 (the default) just means "unknown", and
    // behaves exactly as before.
    public static List<SampleEvent> Project(List<EventRecord> events, long qpcFrequency, long referenceQpc, Action<double> onProgress = null, int expectedSampleCount = 0)
    {
        List<SampleEvent> result = expectedSampleCount > 0 ? new List<SampleEvent>(expectedSampleCount) : new List<SampleEvent>();

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

            result.Add(new SampleEvent(relativeMSec, record.ThreadId, record.StackIndex));
        }

        return result;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
