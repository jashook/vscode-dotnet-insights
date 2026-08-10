////////////////////////////////////////////////////////////////////////////////
// Module: AllocationEventProjector.cs
//
// Notes:
// Sibling projector to GcEventProjector.cs, against the same EventRecord
// stream - decodes GC/AllocationTick events (ClrGcAllocationTick.Decode,
// already implemented in ClrGcTypes.cs but previously unused by anything).
// AllocationTick fires roughly once per ~100KB allocated and carries the
// type of the most recently allocated object - the standard sampling
// mechanism profiling tools use to rank "what's allocating the most"
// without a full heap snapshot (which this parser doesn't capture at all).
//
// Only Version >= 2 payloads are decoded: Version < 2 has no TypeName,
// which makes the event useless for type-ranking - matches this codebase's
// existing convention of only supporting what current .NET actually emits
// (see GCPerHeapHistory's Version >= 3-only decision in ClrGcPerHeapHistory.cs).
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Gc {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A readonly struct, not a class: AllocationEventProjector.Project constructs
// one per allocation tick - 11.9M times for a real 5-minute capture, all held
// in one List<AllocationEvent> for the rest of the program's life (unlike
// PayloadReader/ClrGcAllocationTick, which are short-lived per-call values).
// At ~48 bytes (over the struct-passing convention's 16-byte threshold),
// AllocationJsonExporter.cs's several full passes over that list should
// index it directly (list[i], or a `foreach` binding a `ref readonly` where
// the compiler allows it) rather than copying elements out into locals
// repeatedly. No call site relies on reference semantics (no null checks,
// no identity comparisons), so this is a drop-in change that trades 11.9M+
// heap allocations for one contiguous array's worth of stack-sized values.
public readonly struct AllocationEvent
{
    public readonly DateTime Timestamp;
    public readonly double RelativeMSec;
    public readonly long AllocationAmount;
    public readonly GCAllocationKind AllocationKind;
    public readonly string TypeName;
    public readonly int HeapIndex;
    // Copied directly from the owning EventRecord.Stack, already resolved at
    // parse time (see EventBlock.cs/EventRecord.cs) - empty
    // (Array.Empty<long>()), never null, when this tick wasn't stack-walked.
    public readonly long[] Stack;

    public AllocationEvent(DateTime timestamp, double relativeMSec, long allocationAmount, GCAllocationKind allocationKind, string typeName, int heapIndex, long[] stack)
    {
        this.Timestamp = timestamp;
        this.RelativeMSec = relativeMSec;
        this.AllocationAmount = allocationAmount;
        this.AllocationKind = allocationKind;
        this.TypeName = typeName;
        this.HeapIndex = heapIndex;
        this.Stack = stack;
    }
}

public static class AllocationEventProjector
{
    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";

    // Same (events, pointerSize, qpcFrequency, referenceUtc, referenceQpc)
    // shape as GcEventProjector.Project - same reasoning applies (callers
    // pass NettraceHeader's own SyncTimeQPC/SyncTimeUtc; see
    // GcEventProjector.Project's referenceQpc comment for why that's now
    // correct).
    public static List<AllocationEvent> Project(List<EventRecord> events, int pointerSize, long qpcFrequency, DateTime referenceUtc, long referenceQpc, Action<double> onProgress = null)
    {
        // AllocationEvent is a struct (~48 bytes) - without a capacity hint
        // this list regrows via doubling as ticks are decoded (11.9M of
        // 14.8M total events on a real 5-minute capture are allocation
        // ticks - a much higher fraction than GC-relevant events), and each
        // doubling copies a much larger element than the old 8-byte class
        // reference, plus a write-barrier-tracked copy since AllocationEvent
        // carries a TypeName string reference. events.Count / 2 is a
        // conservative estimate (real ratio measured at ~80%) that still
        // avoids most of the early resizes without grossly over-allocating
        // for captures with fewer ticks.
        List<AllocationEvent> result = new List<AllocationEvent>(events.Count / 2);

        // Shared across every tick decoded below - see ClrGcAllocationTick.
        // Decode's own comment: a real capture's ticks typically span only
        // a handful of distinct types, so this turns millions of redundant
        // Encoding.Unicode.GetString decodes into a handful.
        Dictionary<long, string> typeNameCache = new Dictionary<long, string>();

        // EventRecord is a struct (~70 bytes) - events is the whole capture's
        // event list (14.8M+ for a real 5-minute capture), so this is
        // iterated as a Span over the List<T>'s backing array rather than a
        // plain `foreach`, matching GcEventProjector.Project's own reasoning
        // (a boxed/virtual IEnumerable<T> enumerator copying a large struct
        // per element measurably regressed once EventRecord stopped being a
        // cheap 8-byte class reference).
        Span<EventRecord> eventsSpan = CollectionsMarshal.AsSpan(events);
        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            if (onProgress != null && (eventIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress((double)eventIndex / eventsSpan.Length);
            }

            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            if (record.ProviderName != ClrProviderName)
            {
                continue;
            }

            if (record.EventId != ClrGcEventIds.GCAllocationTick || record.Version < 2)
            {
                continue;
            }

            PayloadReader reader = new PayloadReader(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength, pointerSize);
            ClrGcAllocationTick tick = ClrGcAllocationTick.Decode(reader, record.Version, typeNameCache);

            DateTime timestamp = default;
            double relativeMSec = default;

            if (qpcFrequency > 0)
            {
                long qpcDelta = record.TimeStampRelativeQPC - referenceQpc;
                timestamp = referenceUtc.AddSeconds(qpcDelta / (double)qpcFrequency);
                relativeMSec = qpcDelta * 1000.0 / qpcFrequency;
            }

            result.Add(new AllocationEvent(timestamp, relativeMSec, tick.AllocationAmount64, tick.AllocationKind, tick.TypeName, tick.HeapIndex, record.Stack));
        }

        return result;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
