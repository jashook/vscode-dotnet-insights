////////////////////////////////////////////////////////////////////////////////
// Module: EventOverviewBuilder.cs
//
// Notes:
// Counts every EventRecord in a capture by (ProviderName, EventId) - EventId
// is the reliable dispatch key, not EventName: every CLR-provider event
// (Microsoft-Windows-DotNETRuntime, Microsoft-Windows-DotNETRuntimeRundown)
// is manifest-based and has an empty EventName (see EventBlock.cs's own
// comment, and Exceptions/ClrExceptionTypes.cs's header comment). For those,
// DisplayName comes from ClrEventNames.cs's full generated EventId -> name
// tables (both providers, every event TraceEvent knows about - see that
// file), falling back to a plain "EventID {n}" only for an id genuinely
// outside them.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Overview {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public readonly struct EventTypeCount
{
    public readonly string ProviderName;
    public readonly string DisplayName;
    public readonly int EventId;
    public readonly int Count;

    public EventTypeCount(string providerName, string displayName, int eventId, int count)
    {
        this.ProviderName = providerName;
        this.DisplayName = displayName;
        this.EventId = eventId;
        this.Count = count;
    }
}

public readonly struct EventOverview
{
    public readonly int TotalEventCount;
    public readonly List<EventTypeCount> EventTypes;

    public EventOverview(int totalEventCount, List<EventTypeCount> eventTypes)
    {
        this.TotalEventCount = totalEventCount;
        this.EventTypes = eventTypes;
    }

    // Exact count of one (provider, eventId) pair in the whole capture, or 0
    // if the capture has none. This overview is built by a full pass over
    // every event, so a later projector for that same event type can presize
    // its own result list from this instead of growing it from empty - on a
    // real 3.23GB capture, growing List<SampleEvent> to 16.24M entries was
    // 33% of the entire CPU-sample projection phase, all of it
    // AddWithResize -> _BulkMoveWithWriteBarrier copying.
    //
    // A linear scan on purpose: EventTypes holds one entry per DISTINCT event
    // type (56 on that capture), and this is called a handful of times per
    // run, not per event.
    public int CountForEvent(string providerName, int eventId)
    {
        for (int typeIndex = 0; typeIndex < this.EventTypes.Count; ++typeIndex)
        {
            EventTypeCount eventType = this.EventTypes[typeIndex];
            if (eventType.EventId == eventId && eventType.ProviderName == providerName)
            {
                return eventType.Count;
            }
        }

        return 0;
    }
}

public static class EventOverviewBuilder
{
    // Holds one distinct key's running Count and first-seen EventName -
    // merges what used to be two separate Dictionary<(string,int), _>
    // lookups (countsByKey/eventNameByKey) into one, halving the per-event
    // hashing/lookup work on the fallback path below. A class (not a
    // struct): stored as the dictionary's own reused value so a hit just
    // mutates its fields in place, no re-insert.
    private sealed class EventTypeAccumulator
    {
        public int Count;
        public string EventName;

        // Carried on the accumulator itself now that the dictionary key that
        // used to hold them is gone (see Build) - the final output loop walks
        // the accumulators directly.
        public string ProviderName;
        public int EventId;
    }

    // All accumulators for one provider, indexed directly by event id. Event
    // ids are a ushort on the wire, and a real capture uses a few dozen of
    // them per provider, so a plain array indexed by id replaces hashing a
    // (string, int) tuple key on every event.
    //
    // A holder class rather than a bare array so that two ProviderSlots
    // holding two different string INSTANCES of the same provider name (see
    // Build's alias handling) share one set of accumulators even after the
    // array is grown and replaced.
    private sealed class ProviderAccumulators
    {
        public EventTypeAccumulator[] ByEventId = new EventTypeAccumulator[64];
    }

    // One (provider name instance -> accumulators) association. Matched by
    // REFERENCE, which is exact rather than approximate here: EventBlock.cs
    // passes the same metadata.ProviderName instance into every EventRecord
    // sharing that metadata entry. A second instance carrying the same name
    // (a different MetadataId re-declaring the same provider, or a
    // hand-built EventRecord in a test) just gets its own slot pointing at
    // the SAME ProviderAccumulators, so counts still merge correctly.
    private sealed class ProviderSlot
    {
        public string ProviderName;
        public ProviderAccumulators Accumulators;
    }

    // Measured via dotnet-trace (dotnet-sampled-thread-time profile) against
    // a real 736MB/4.29M-event capture: this method was 9.62% of the whole
    // nettraceParser run's CPU time (~376ms of a ~4.3s run) despite producing
    // only a few dozen distinct output rows - Marvin.ComputeHash32/
    // String.GetNonRandomizedHashCode alone (string content hashing, not
    // cached by .NET strings - each call recomputes it) accounted for over
    // half of that. The (string ProviderName, int EventId) tuple key avoids
    // the interpolated-string-key anti-pattern (see this method's own
    // original comment), but a value tuple containing a string still
    // content-hashes ProviderName on every single lookup regardless.
    //
    // lastProviderName/lastEventId/lastAccumulator is a one-entry "sticky"
    // cache for a run of consecutive events sharing the same key - real
    // captures commonly emit long bursts of one event type back-to-back
    // (confirmed against the same real capture: GCAllocationTick alone was
    // 1.37M of 4.29M events). Matching via ReferenceEquals on ProviderName
    // is safe, not approximate: EventBlock.cs passes the SAME string
    // instance (metadata.ProviderName, looked up once per MetadataId) into
    // every EventRecord sharing that metadata entry, so two consecutive
    // records only pass this check when they are provably the same
    // dictionary key - this is purely a fast path that skips redundant work
    // already known to be correct, never a heuristic that trades away
    // correctness. Every genuinely new/different key still falls through to
    // the real Dictionary (content equality/hashing), so this remains
    // correct even in the pathological case of full interleaving where the
    // cache never hits.
    //
    // The dictionary is gone entirely as of the 2026-08-15 profile, which
    // showed the fallback path still dominating on a capture whose event
    // types interleave heavily enough that the sticky cache misses often:
    // Dictionary<(string,int),_>.FindValue was 72% of this phase's samples on
    // a real 3.23GB/35.08M-event capture, with Marvin.ComputeHash32 alone at
    // 24.4% self. The replacement hashes nothing at all - provider instances
    // are matched by reference against a handful of slots, then the event id
    // indexes an array directly (see ProviderSlot/ProviderAccumulators). The
    // sticky cache stays, since it still short-circuits even that.
    public static EventOverview Build(List<EventRecord> events, Action<double> onProgress = null)
    {
        // A real capture has a handful of providers (3-5), so this is scanned
        // linearly rather than hashed.
        List<ProviderSlot> providerSlots = new List<ProviderSlot>();
        List<EventTypeAccumulator> allAccumulators = new List<EventTypeAccumulator>();

        // Only ever allocated if a capture actually carries an event id
        // outside the wire format's own range - see the loop below.
        Dictionary<(string ProviderName, int EventId), EventTypeAccumulator> outOfRangeAccumulators = null;

        string lastProviderName = null;
        int lastEventId = -1;
        EventTypeAccumulator lastAccumulator = null;

        // events can be millions of EventRecord structs (~70 bytes each) for
        // a real capture - iterated as a Span over the List<T>'s backing
        // array rather than a plain `foreach`/indexer, matching every other
        // whole-capture pass in this codebase (Gc/AllocationEventProjector.cs,
        // Exceptions/ExceptionEventProjector.cs) for the same reason: a
        // boxed/virtual enumerator or indexer copying a large struct per
        // element is a measured, real cost at this volume.
        Span<EventRecord> eventsSpan = CollectionsMarshal.AsSpan(events);
        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            if (onProgress != null && (eventIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress((double)eventIndex / eventsSpan.Length);
            }

            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            EventTypeAccumulator accumulator;
            if (lastAccumulator != null && record.EventId == lastEventId && ReferenceEquals(record.ProviderName, lastProviderName))
            {
                accumulator = lastAccumulator;
            }
            else
            {
                if ((uint)record.EventId < MaxDirectIndexedEventId)
                {
                    ProviderAccumulators providerAccumulators = GetOrAddProviderAccumulators(providerSlots, record.ProviderName);

                    EventTypeAccumulator[] byEventId = providerAccumulators.ByEventId;
                    if (record.EventId >= byEventId.Length)
                    {
                        byEventId = GrowByEventId(byEventId, record.EventId);
                        providerAccumulators.ByEventId = byEventId;
                    }

                    accumulator = byEventId[record.EventId];
                    if (accumulator == null)
                    {
                        accumulator = CreateAccumulator(allAccumulators, record.ProviderName, record.EventId);
                        byEventId[record.EventId] = accumulator;
                    }
                }
                else
                {
                    // Out-of-range/corrupt event id - kept correct rather than
                    // fast, and above all kept from sizing an array off an
                    // arbitrary 32-bit number read out of the file.
                    if (outOfRangeAccumulators == null)
                    {
                        outOfRangeAccumulators = new Dictionary<(string, int), EventTypeAccumulator>();
                    }

                    (string, int) outOfRangeKey = (record.ProviderName, record.EventId);
                    if (!outOfRangeAccumulators.TryGetValue(outOfRangeKey, out accumulator))
                    {
                        accumulator = CreateAccumulator(allAccumulators, record.ProviderName, record.EventId);
                        outOfRangeAccumulators[outOfRangeKey] = accumulator;
                    }
                }

                lastProviderName = record.ProviderName;
                lastEventId = record.EventId;
                lastAccumulator = accumulator;
            }

            ++accumulator.Count;

            if (accumulator.EventName == null && !string.IsNullOrEmpty(record.EventName))
            {
                accumulator.EventName = record.EventName;
            }
        }

        List<EventTypeCount> eventTypes = new List<EventTypeCount>(allAccumulators.Count);
        for (int accumulatorIndex = 0; accumulatorIndex < allAccumulators.Count; ++accumulatorIndex)
        {
            EventTypeAccumulator accumulator = allAccumulators[accumulatorIndex];
            string displayName = ResolveDisplayName(accumulator.ProviderName, accumulator.EventId, accumulator.EventName);
            eventTypes.Add(new EventTypeCount(accumulator.ProviderName, displayName, accumulator.EventId, accumulator.Count));
        }

        eventTypes.Sort((left, right) => right.Count.CompareTo(left.Count));

        return new EventOverview(events.Count, eventTypes);
    }

    // The wire format encodes an event id in 32 bits but real providers use a
    // small dense range (the CLR runtime provider's highest is in the low
    // hundreds). Anything at or above this goes down the dictionary path
    // instead of sizing an array from a number read out of the file - a
    // corrupt id would otherwise ask for gigabytes.
    private const uint MaxDirectIndexedEventId = 65536;

    private static EventTypeAccumulator CreateAccumulator(List<EventTypeAccumulator> allAccumulators, string providerName, int eventId)
    {
        EventTypeAccumulator accumulator = new EventTypeAccumulator();
        accumulator.ProviderName = providerName;
        accumulator.EventId = eventId;
        allAccumulators.Add(accumulator);
        return accumulator;
    }

    // Reference match first (the whole point - see ProviderSlot), then a
    // content match that registers the new instance as an alias onto the same
    // accumulators, so every later event carrying that instance takes the
    // reference path too.
    private static ProviderAccumulators GetOrAddProviderAccumulators(List<ProviderSlot> providerSlots, string providerName)
    {
        for (int slotIndex = 0; slotIndex < providerSlots.Count; ++slotIndex)
        {
            if (ReferenceEquals(providerSlots[slotIndex].ProviderName, providerName))
            {
                return providerSlots[slotIndex].Accumulators;
            }
        }

        ProviderAccumulators accumulators = null;
        for (int slotIndex = 0; slotIndex < providerSlots.Count; ++slotIndex)
        {
            if (providerSlots[slotIndex].ProviderName == providerName)
            {
                accumulators = providerSlots[slotIndex].Accumulators;
                break;
            }
        }

        if (accumulators == null)
        {
            accumulators = new ProviderAccumulators();
        }

        ProviderSlot slot = new ProviderSlot();
        slot.ProviderName = providerName;
        slot.Accumulators = accumulators;
        providerSlots.Add(slot);

        return accumulators;
    }

    private static EventTypeAccumulator[] GrowByEventId(EventTypeAccumulator[] byEventId, int requiredEventId)
    {
        int newLength = byEventId.Length;
        while (newLength <= requiredEventId)
        {
            newLength *= 2;
        }

        EventTypeAccumulator[] grown = new EventTypeAccumulator[newLength];
        Array.Copy(byEventId, grown, byEventId.Length);
        return grown;
    }

    // Precedence: the record's OWN EventName wins when it has one (a
    // self-describing provider like Microsoft-DotNETCore-EventPipe knows its
    // event's real name better than any table here could), then the
    // generated CLR tables, then an honest "EventID {n}" placeholder for
    // anything genuinely unknown.
    private static string ResolveDisplayName(string providerName, int eventId, string eventName)
    {
        if (!string.IsNullOrEmpty(eventName))
        {
            return eventName;
        }

        if (ClrEventNames.TryGetName(providerName, eventId, out string knownName))
        {
            return knownName;
        }

        return $"EventID {eventId}";
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Overview)
