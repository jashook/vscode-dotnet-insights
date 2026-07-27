////////////////////////////////////////////////////////////////////////////////
// Module: GcEventProjector.cs
//
// Notes:
// The first concrete consumer of the generic EventRecord stream: filters the
// CLR runtime provider's GC events and correlates GCStart/GCEnd/GCHeapStats/
// GCPerHeapHistory/GCGlobalHeapHistory into one GcEvent per completed
// collection. Only GCStart/GCEnd carry an explicit correlation id (Count) -
// GCHeapStats/GCPerHeapHistory/GCGlobalHeapHistory don't, and are instead
// resolved via a per-generation pending queue keyed by GCGlobalHeapHistory's
// own CondemnedGeneration field. See Project's comment for why (verified
// against a real 8-heap Server GC capture: neither "most recently started
// GC" nor a single shared FIFO queue disambiguate a slow background gen2
// GC's bookkeeping from overlapping foreground gen0/1 GCs correctly).
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
using System.Runtime.InteropServices;

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
    // POH (Pinned Object Heap) - only present when ClrGcHeapStats.Decode saw
    // version >= 2 and a long enough payload; 0 otherwise (older runtimes
    // that predate POH).
    public long GenerationSize4;
    public long TotalPromotedSize0;
    public long TotalPromotedSize1;
    public long TotalPromotedSize2;
    public long TotalPromotedSize3;
    public long TotalPromotedSize4;
    public int NumHeaps;
    public long FinalYoungestDesired;
    public int PinnedObjectCount;
    public GCGlobalMechanisms GlobalMechanisms;
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
    // How many concurrently in-flight GCs of one generation (started, no
    // GlobalHeapHistory seen yet) a per-generation pending queue tolerates
    // before assuming the oldest one is never going to get one (a dropped
    // event) and evicting it rather than blocking that generation's future
    // heap history behind it forever. Purely a "never block forever" safety
    // valve - a queue of pending ints is negligible memory regardless of
    // size, so this stays generous.
    private const int MaxPendingHeapHistoryGcs = 1000;

    public static List<GcEvent> Project(List<EventRecord> events, int pointerSize, long qpcFrequency, DateTime referenceUtc, long referenceQpc)
    {
        Dictionary<int, GcEvent> gcsById = new Dictionary<int, GcEvent>();
        Dictionary<int, long> startTimeStampById = new Dictionary<int, long>();

        // Events are NOT guaranteed to arrive in this list in true
        // chronological (QPC) order - verified against a real Server GC
        // capture (8 heaps) where a GC's own GCStart appeared *after*
        // GCEnd/GCHeapStats events for a later-numbered GC with an earlier
        // true relative QPC. EventPipe writes per-thread/per-buffer event
        // blocks that get interleaved in the file by flush order, not
        // global time order - AllocationJsonExporter.cs's WriteTicks
        // already discovered the same thing and sorts defensively before
        // writing. Every correlation heuristic below assumes true
        // chronological order, so the GC-relevant subset is sorted here
        // first (a stable sort - ties broken by original position - since
        // same-QPC events should keep their relative wire order).
        //
        // Filtered to just the 5 GC-relevant event IDs before sorting
        // (not all ~15M events in a busy capture, most of which are
        // AllocationTick) so the sort itself stays cheap - a real 5-minute
        // capture with millions of allocation ticks had only ~109,000
        // GC-relevant events.
        // (EventRecord, int) is a ~78-byte value tuple - without a capacity
        // hint this list regrows via doubling as matching events are added,
        // and each doubling now copies a much larger element than when
        // EventRecord was an 8-byte class reference. events.Count / 128 is a
        // rough estimate matching this file's own documented ratio above
        // (~109,000 GC-relevant events out of millions of total events on a
        // real 5-minute capture) - not exact for every capture, but close
        // enough to skip most of the early resizes.
        List<(EventRecord Record, int OriginalIndex)> gcRelevantEvents = new List<(EventRecord, int)>(events.Count / 128);

        // EventRecord is a struct (~70 bytes) - events is always the whole
        // capture's event list (14.8M+ for a real 5-minute capture), so this
        // scan is iterated as a Span over the List<T>'s backing array (no
        // per-element copy through a boxed/virtual IEnumerable<T> enumerator,
        // which measurably regressed once EventRecord stopped being a cheap
        // 8-byte class reference) rather than a plain `foreach`.
        Span<EventRecord> eventsSpan = CollectionsMarshal.AsSpan(events);
        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            if (record.ProviderName != ClrProviderName)
            {
                continue;
            }

            if (record.EventId == ClrGcEventIds.GCStart || record.EventId == ClrGcEventIds.GCEnd ||
                record.EventId == ClrGcEventIds.GCHeapStats || record.EventId == ClrGcEventIds.GCPerHeapHistory ||
                record.EventId == ClrGcEventIds.GCGlobalHeapHistory)
            {
                gcRelevantEvents.Add((record, eventIndex));
            }
        }

        gcRelevantEvents.Sort((left, right) =>
        {
            int comparison = left.Record.TimeStampRelativeQPC.CompareTo(right.Record.TimeStampRelativeQPC);
            return comparison != 0 ? comparison : left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        // GCHeapStats/GCGlobalHeapHistory/GCPerHeapHistory don't carry the GC's
        // own Count/Id, so they must be correlated by inference. Two earlier
        // approaches failed against a real 8-heap Server GC capture:
        //   1. A single "most recently started GC" global - breaks the
        //      instant a background/gen2 GC's bookkeeping is still pending
        //      when any other GC starts.
        //   2. A single global FIFO of pending GCs (oldest-pending gets the
        //      next heap-history event) - better, but still wrong: tracing
        //      the actual event order showed a GC's own GlobalHeapHistory/
        //      HeapStats can arrive attributed to the *previous* GC's still-
        //      open slot, because GCEnd does not reliably close out a GC's
        //      bookkeeping window - only a fundamentally different signal
        //      does (see below). A single FIFO has no way to tell "this
        //      batch is for a different GC" apart from "this batch is late".
        //
        // Fix: GCGlobalHeapHistory's own payload carries CondemnedGeneration
        // - which generation this specific accounting batch belongs to.
        // Tracking pending GCs *per generation* (keyed by GCStart's Depth)
        // and using CondemnedGeneration to pick the right generation's
        // queue when GlobalHeapHistory arrives resolves the ambiguity a
        // single shared queue/pointer can't: a slow background gen2 GC and
        // fast foreground gen0/1 GCs no longer compete for the same slot.
        // GlobalHeapHistory reliably precedes that GC's own PerHeapHistory/
        // HeapStats in the wire order (verified against the real capture),
        // so resolving + dequeuing on GlobalHeapHistory and caching the
        // result as "the current batch's target" correctly routes the
        // PerHeapHistory/HeapStats events that immediately follow it.
        Dictionary<int, Queue<int>> pendingByGeneration = new Dictionary<int, Queue<int>>();
        int currentBatchGcId = -1;

        foreach ((EventRecord record, int _) in gcRelevantEvents)
        {
            PayloadReader reader = new PayloadReader(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength, pointerSize);

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

                Queue<int> generationQueue;
                if (!pendingByGeneration.TryGetValue(start.Depth, out generationQueue))
                {
                    generationQueue = new Queue<int>();
                    pendingByGeneration[start.Depth] = generationQueue;
                }

                generationQueue.Enqueue(start.Count);
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
                if (currentBatchGcId >= 0 && gcsById.TryGetValue(currentBatchGcId, out gcEvent))
                {
                    gcEvent.HasHeapStats = true;
                    gcEvent.TotalHeapSize = heapStats.TotalHeapSize;
                    gcEvent.TotalPromoted = heapStats.TotalPromoted;
                    gcEvent.GenerationSize0 = heapStats.GenerationSize0;
                    gcEvent.GenerationSize1 = heapStats.GenerationSize1;
                    gcEvent.GenerationSize2 = heapStats.GenerationSize2;
                    gcEvent.GenerationSize3 = heapStats.GenerationSize3;
                    gcEvent.GenerationSize4 = heapStats.GenerationSize4;
                    gcEvent.TotalPromotedSize0 = heapStats.TotalPromotedSize0;
                    gcEvent.TotalPromotedSize1 = heapStats.TotalPromotedSize1;
                    gcEvent.TotalPromotedSize2 = heapStats.TotalPromotedSize2;
                    gcEvent.TotalPromotedSize3 = heapStats.TotalPromotedSize3;
                    gcEvent.TotalPromotedSize4 = heapStats.TotalPromotedSize4;
                    gcEvent.PinnedObjectCount = heapStats.PinnedObjectCount;
                }
            }
            else if (record.EventId == ClrGcEventIds.GCPerHeapHistory)
            {
                ClrGcHeap heap = ClrGcHeap.Decode(reader, record.Version);

                GcEvent gcEvent;
                if (heap != null && currentBatchGcId >= 0 && gcsById.TryGetValue(currentBatchGcId, out gcEvent))
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

                GcEvent gcEvent = ResolveNextInGeneration(pendingByGeneration, gcsById, globalHistory.CondemnedGeneration);
                currentBatchGcId = gcEvent != null ? gcEvent.Id : -1;

                if (gcEvent != null)
                {
                    gcEvent.HasGlobalHeapHistory = true;
                    gcEvent.NumHeaps = globalHistory.NumHeaps;
                    gcEvent.FinalYoungestDesired = globalHistory.FinalYoungestDesired;

                    // GCGlobalHeapHistory's own Reason is the more reliable one (GCStart's
                    // Reason can be superseded by the time collection actually begins).
                    gcEvent.Reason = globalHistory.Reason;
                    gcEvent.GlobalMechanisms = globalHistory.GlobalMechanisms;
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

    // Resolves and immediately dequeues the oldest pending GC of the given
    // generation (evicting stale front entries first - an id enqueued at
    // GCStart that either never resolves in gcsById, which shouldn't
    // happen, or has sat at the front of its generation's queue longer
    // than MaxPendingHeapHistoryGcs allows, implying its own
    // GlobalHeapHistory was dropped rather than merely delayed). Called
    // once per GCGlobalHeapHistory event - GCPerHeapHistory/GCHeapStats
    // don't carry a generation of their own, so they aren't resolved
    // independently; they route to whatever this call most recently
    // returned (see currentBatchGcId in Project).
    private static GcEvent ResolveNextInGeneration(Dictionary<int, Queue<int>> pendingByGeneration, Dictionary<int, GcEvent> gcsById, int generation)
    {
        Queue<int> generationQueue;
        if (!pendingByGeneration.TryGetValue(generation, out generationQueue))
        {
            return null;
        }

        while (generationQueue.Count > MaxPendingHeapHistoryGcs)
        {
            generationQueue.Dequeue();
        }

        while (generationQueue.Count > 0)
        {
            int candidateId = generationQueue.Dequeue();

            GcEvent candidate;
            if (gcsById.TryGetValue(candidateId, out candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
