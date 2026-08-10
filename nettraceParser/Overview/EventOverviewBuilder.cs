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
    public static EventOverview Build(List<EventRecord> events, Action<double> onProgress = null)
    {
        Dictionary<(string ProviderName, int EventId), EventTypeAccumulator> accumulatorsByKey = new Dictionary<(string, int), EventTypeAccumulator>();

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
                (string, int) key = (record.ProviderName, record.EventId);
                if (!accumulatorsByKey.TryGetValue(key, out accumulator))
                {
                    accumulator = new EventTypeAccumulator();
                    accumulatorsByKey[key] = accumulator;
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

        List<EventTypeCount> eventTypes = new List<EventTypeCount>(accumulatorsByKey.Count);
        foreach (KeyValuePair<(string ProviderName, int EventId), EventTypeAccumulator> entry in accumulatorsByKey)
        {
            string displayName = ResolveDisplayName(entry.Key.ProviderName, entry.Key.EventId, entry.Value.EventName);
            eventTypes.Add(new EventTypeCount(entry.Key.ProviderName, displayName, entry.Key.EventId, entry.Value.Count));
        }

        eventTypes.Sort((left, right) => right.Count.CompareTo(left.Count));

        return new EventOverview(events.Count, eventTypes);
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
