////////////////////////////////////////////////////////////////////////////////
// Module: GcEventProjector.cs
//
// Notes:
// The first concrete consumer of the generic EventRecord stream: filters the
// CLR runtime provider's GC events and correlates GCStart/GCEnd/GCHeapStats/
// GCGlobalHeapHistory (matched by their shared Count/Id field, exactly how
// gcEventListener's EventPipeBasedListener.PublishClient correlates the same
// events from TraceEvent's live callbacks) into one GcEvent per completed
// collection.
//
// Adding a future event type (JIT, a different provider, whatever comes
// next) means writing a new sibling projector against the same EventRecord
// stream - nothing here or upstream needs to change for that.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Gc {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class GcEvent
{
    public int Id;
    public int Generation;
    public GCReason Reason;
    public GCType Type;
    public double PauseDurationMSec;
    public double PauseStartRelativeMSec;
    public double PauseEndRelativeMSec;

    // Wall-clock time the collection started, derived from the trace's
    // SyncTimeUTC/SyncTimeQPC anchor (NettraceHeader) plus this GC's own QPC
    // timestamp - not just "relative ms into the capture".
    public DateTime Timestamp;
    public long TotalHeapSize;
    public long TotalPromoted;
    public long GenerationSize0;
    public long GenerationSize1;
    public long GenerationSize2;
    public long GenerationSize3;
    public long TotalPromotedSize0;
    public long TotalPromotedSize1;
    public long TotalPromotedSize2;
    public long TotalPromotedSize3;
    public int NumHeaps;
    public long FinalYoungestDesired;
    public List<ClrGcHeap> Heaps = new List<ClrGcHeap>();

    public bool HasEnd;
    public bool HasHeapStats;
    public bool HasGlobalHeapHistory;
}

public static class GcEventProjector
{
    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";

    // referenceUtc/referenceQpc anchor the wall-clock conversion: referenceQpc
    // is whatever QPC tick value corresponds to referenceUtc. NettraceHeader's
    // own SyncTimeQPC looks like it should be this anchor, but empirically
    // (verified against a real capture's file mtime) its numeric relationship
    // to the per-event QPC stream doesn't hold up on this platform - so
    // callers pass the trace's own first event's QPC value paired with
    // SyncTimeUtc instead, which is self-consistent by construction (both
    // come from data already proven correct: SyncTimeUtc matched the real
    // capture time to the second, and per-event QPC deltas are internally
    // consistent - only the header's own SyncTimeQPC field is suspect).
    public static List<GcEvent> Project(IEnumerable<EventRecord> events, int pointerSize, long qpcFrequency, DateTime referenceUtc, long referenceQpc)
    {
        Dictionary<int, GcEvent> gcsById = new Dictionary<int, GcEvent>();
        Dictionary<int, long> startTimeStampById = new Dictionary<int, long>();

        // GCHeapStats/GCGlobalHeapHistory/GCPerHeapHistory don't carry the GC's own
        // Count/Id - empirically (verified against a real capture) they're emitted
        // as part of finishing up a collection's bookkeeping, which can land either
        // side of GCEnd on the wire. So "most recently started GC" is the reliable
        // correlation key, not "most recently started and not yet ended".
        int mostRecentlyStartedGcId = -1;

        foreach (EventRecord record in events)
        {
            if (record.ProviderName != ClrProviderName)
            {
                continue;
            }

            PayloadReader reader = new PayloadReader(record.PayloadBytes, pointerSize);

            if (record.EventId == ClrGcEventIds.GCStart)
            {
                ClrGcStart start = ClrGcStart.Decode(reader, record.Version);

                GcEvent gcEvent = new GcEvent();
                gcEvent.Id = start.Count;
                gcEvent.Generation = start.Depth;
                gcEvent.Reason = start.Reason;
                gcEvent.Type = start.Type;

                if (qpcFrequency > 0)
                {
                    long qpcDelta = record.TimeStampRelativeQPC - referenceQpc;
                    gcEvent.Timestamp = referenceUtc.AddSeconds(qpcDelta / (double)qpcFrequency);
                }

                gcsById[start.Count] = gcEvent;
                startTimeStampById[start.Count] = record.TimeStampRelativeQPC;
                mostRecentlyStartedGcId = start.Count;
            }
            else if (record.EventId == ClrGcEventIds.GCEnd)
            {
                ClrGcEnd end = ClrGcEnd.Decode(reader, record.Version);

                GcEvent gcEvent;
                if (gcsById.TryGetValue(end.Count, out gcEvent))
                {
                    gcEvent.HasEnd = true;

                    long startTimeStamp;
                    if (startTimeStampById.TryGetValue(end.Count, out startTimeStamp) && qpcFrequency > 0)
                    {
                        long deltaTicks = record.TimeStampRelativeQPC - startTimeStamp;
                        gcEvent.PauseDurationMSec = deltaTicks * 1000.0 / qpcFrequency;
                        // Relative to the trace's own first event (referenceQpc), not
                        // the raw QPC counter - record.TimeStampRelativeQPC is a raw
                        // hardware tick value despite its name, so without subtracting
                        // referenceQpc here these would be huge absolute-ish numbers
                        // (e.g. "6 days elapsed" for a 1-second capture) instead of
                        // genuinely relative-to-capture-start, which is what every
                        // consumer of this field (including the .gcinfo/XML path,
                        // where Perfview computes it as truly relative) expects.
                        gcEvent.PauseStartRelativeMSec = (startTimeStamp - referenceQpc) * 1000.0 / qpcFrequency;
                        gcEvent.PauseEndRelativeMSec = (record.TimeStampRelativeQPC - referenceQpc) * 1000.0 / qpcFrequency;
                    }
                }
            }
            else if (record.EventId == ClrGcEventIds.GCHeapStats)
            {
                ClrGcHeapStats heapStats = ClrGcHeapStats.Decode(reader, record.Version);

                GcEvent gcEvent;
                if (gcsById.TryGetValue(mostRecentlyStartedGcId, out gcEvent))
                {
                    gcEvent.HasHeapStats = true;
                    gcEvent.TotalHeapSize = heapStats.TotalHeapSize;
                    gcEvent.TotalPromoted = heapStats.TotalPromoted;
                    gcEvent.GenerationSize0 = heapStats.GenerationSize0;
                    gcEvent.GenerationSize1 = heapStats.GenerationSize1;
                    gcEvent.GenerationSize2 = heapStats.GenerationSize2;
                    gcEvent.GenerationSize3 = heapStats.GenerationSize3;
                    gcEvent.TotalPromotedSize0 = heapStats.TotalPromotedSize0;
                    gcEvent.TotalPromotedSize1 = heapStats.TotalPromotedSize1;
                    gcEvent.TotalPromotedSize2 = heapStats.TotalPromotedSize2;
                    gcEvent.TotalPromotedSize3 = heapStats.TotalPromotedSize3;
                }
            }
            else if (record.EventId == ClrGcEventIds.GCPerHeapHistory)
            {
                ClrGcHeap heap = ClrGcHeap.Decode(reader, record.Version);

                GcEvent gcEvent;
                if (heap != null && gcsById.TryGetValue(mostRecentlyStartedGcId, out gcEvent))
                {
                    for (int heapIndex = gcEvent.Heaps.Count - 1; heapIndex >= 0; --heapIndex)
                    {
                        if (gcEvent.Heaps[heapIndex].HeapIndex == heap.HeapIndex)
                        {
                            gcEvent.Heaps.RemoveAt(heapIndex);
                        }
                    }

                    gcEvent.Heaps.Add(heap);
                }
            }
            else if (record.EventId == ClrGcEventIds.GCGlobalHeapHistory)
            {
                ClrGcGlobalHeapHistory globalHistory = ClrGcGlobalHeapHistory.Decode(reader, record.Version);

                GcEvent gcEvent;
                if (gcsById.TryGetValue(mostRecentlyStartedGcId, out gcEvent))
                {
                    gcEvent.HasGlobalHeapHistory = true;
                    gcEvent.NumHeaps = globalHistory.NumHeaps;
                    gcEvent.FinalYoungestDesired = globalHistory.FinalYoungestDesired;

                    // GCGlobalHeapHistory's own Reason is the more reliable one (GCStart's
                    // Reason can be superseded by the time collection actually begins).
                    gcEvent.Reason = globalHistory.Reason;
                }
            }
        }

        List<GcEvent> completed = new List<GcEvent>();
        foreach (KeyValuePair<int, GcEvent> entry in gcsById)
        {
            if (entry.Value.HasEnd)
            {
                completed.Add(entry.Value);
            }
        }

        completed.Sort((left, right) => left.Id.CompareTo(right.Id));
        return completed;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
