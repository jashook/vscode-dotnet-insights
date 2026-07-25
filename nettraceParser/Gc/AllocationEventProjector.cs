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

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class AllocationEvent
{
    public DateTime Timestamp;
    public double RelativeMSec;
    public long AllocationAmount;
    public GCAllocationKind AllocationKind;
    public string TypeName;
    public int HeapIndex;
}

public static class AllocationEventProjector
{
    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";

    // Same (events, pointerSize, qpcFrequency, referenceUtc, referenceQpc)
    // shape as GcEventProjector.Project - same reasoning applies (the
    // trace's own first event is the QPC anchor, not NettraceHeader's
    // SyncTimeQPC; see Program.cs's referenceQpc comment).
    public static List<AllocationEvent> Project(IEnumerable<EventRecord> events, int pointerSize, long qpcFrequency, DateTime referenceUtc, long referenceQpc)
    {
        List<AllocationEvent> result = new List<AllocationEvent>();

        foreach (EventRecord record in events)
        {
            if (record.ProviderName != ClrProviderName)
            {
                continue;
            }

            if (record.EventId != ClrGcEventIds.GCAllocationTick || record.Version < 2)
            {
                continue;
            }

            PayloadReader reader = new PayloadReader(record.PayloadBytes, pointerSize);
            ClrGcAllocationTick tick = ClrGcAllocationTick.Decode(reader, record.Version);

            if (tick == null)
            {
                continue;
            }

            AllocationEvent allocationEvent = new AllocationEvent();
            allocationEvent.AllocationAmount = tick.AllocationAmount64;
            allocationEvent.AllocationKind = tick.AllocationKind;
            allocationEvent.TypeName = tick.TypeName;
            allocationEvent.HeapIndex = tick.HeapIndex;

            if (qpcFrequency > 0)
            {
                long qpcDelta = record.TimeStampRelativeQPC - referenceQpc;
                allocationEvent.Timestamp = referenceUtc.AddSeconds(qpcDelta / (double)qpcFrequency);
                allocationEvent.RelativeMSec = qpcDelta * 1000.0 / qpcFrequency;
            }

            result.Add(allocationEvent);
        }

        return result;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
