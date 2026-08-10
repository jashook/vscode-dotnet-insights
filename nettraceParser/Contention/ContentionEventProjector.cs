////////////////////////////////////////////////////////////////////////////////
// Module: ContentionEventProjector.cs
//
// Notes:
// Sibling projector to Exceptions/ExceptionEventProjector.cs, against the
// same EventRecord stream - pairs Contention/Start (EventId 81) with its
// matching Contention/Stop (EventId 91) events by ThreadId to produce a
// List<ContentionEvent> with per-event lock wait durations.
//
// Start/Stop pairing is by ThreadId: a single thread can only be waiting on
// one lock at a time (the OS blocks until the lock is acquired), so a
// dictionary keyed by ThreadId is sufficient. Unpaired Stops (Stop without a
// preceding Start - can occur at the start of a capture if a contention
// started before tracing began) are silently dropped. Unpaired Starts (Start
// without a Stop - can occur if tracing ended during a wait) are also
// silently dropped by leaving them in pendingByThread.
//
// DurationMSec uses ContentionStop's own DurationNs field (the CLR's
// internal measurement, more accurate than a QPC delta) with a fallback to
// the QPC delta between Start and Stop when DurationNs is zero or missing.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Contention {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A readonly struct, not a class - same reasoning as ExceptionEvent (see
// Exceptions/ExceptionEventProjector.cs): no call site relies on reference
// semantics, one contiguous array beats one heap object per event, and
// contention volume is bounded (one event per lock-acquire boundary, not
// one per allocation tick).
public readonly struct ContentionEvent
{
    public readonly double RelativeMSec;
    public readonly double DurationMSec;
    public readonly ClrContentionFlags ContentionFlags;
    // The WAITING thread - the one blocked for DurationMSec. Distinct from
    // OwnerThreadId below, which is whoever it was blocked behind.
    public readonly long ThreadId;
    // Resolved at parse time (EventBlock.cs), same convention as
    // ExceptionEvent.Stack - empty (Array.Empty<long>()), never null, when
    // this contention event wasn't stack-walked.
    public readonly long[] Stack;
    // V2 ContentionStart fields (0 when the payload was V1 - see
    // ClrContentionStart.Decode). LockId is what the Lock Timeline view
    // groups rows by; OwnerThreadId is 0 when the runtime couldn't attribute
    // an owner and must be rendered as "unknown", never as thread 0.
    public readonly long LockId;
    public readonly long AssociatedObjectId;
    public readonly long OwnerThreadId;

    public ContentionEvent(double relativeMSec, double durationMSec, ClrContentionFlags contentionFlags, long threadId, long[] stack, long lockId = 0, long associatedObjectId = 0, long ownerThreadId = 0)
    {
        this.RelativeMSec = relativeMSec;
        this.DurationMSec = durationMSec;
        this.ContentionFlags = contentionFlags;
        this.ThreadId = threadId;
        this.Stack = stack;
        this.LockId = lockId;
        this.AssociatedObjectId = associatedObjectId;
        this.OwnerThreadId = ownerThreadId;
    }
}

public static class ContentionEventProjector
{
    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";

    // Transient state for one pending Contention/Start awaiting its
    // Contention/Stop - kept as a readonly struct (not a class) to avoid
    // one heap allocation per in-flight contention.
    private readonly struct PendingStart
    {
        public readonly double RelativeMSec;
        public readonly ClrContentionFlags ContentionFlags;
        public readonly long[] Stack;
        public readonly long LockId;
        public readonly long AssociatedObjectId;
        public readonly long OwnerThreadId;

        public PendingStart(double relativeMSec, ClrContentionFlags contentionFlags, long[] stack, long lockId, long associatedObjectId, long ownerThreadId)
        {
            this.RelativeMSec = relativeMSec;
            this.ContentionFlags = contentionFlags;
            this.Stack = stack;
            this.LockId = lockId;
            this.AssociatedObjectId = associatedObjectId;
            this.OwnerThreadId = ownerThreadId;
        }
    }

    public static List<ContentionEvent> Project(List<EventRecord> events, int pointerSize, long qpcFrequency, DateTime referenceUtc, long referenceQpc, Action<double> onProgress = null)
    {
        List<ContentionEvent> result = new List<ContentionEvent>();
        Dictionary<long, PendingStart> pendingByThread = new Dictionary<long, PendingStart>();

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

            if (record.EventId != ClrContentionEventIds.ContentionStart && record.EventId != ClrContentionEventIds.ContentionStop)
            {
                continue;
            }

            double relativeMSec = 0;

            if (qpcFrequency > 0)
            {
                long qpcDelta = record.TimeStampRelativeQPC - referenceQpc;
                relativeMSec = qpcDelta * 1000.0 / qpcFrequency;
            }

            if (record.EventId == ClrContentionEventIds.ContentionStart)
            {
                PayloadReader reader = new PayloadReader(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength, pointerSize);
                ClrContentionStart startEvent = ClrContentionStart.Decode(reader, record.Version);
                pendingByThread[record.ThreadId] = new PendingStart(relativeMSec, startEvent.ContentionFlags, record.Stack, startEvent.LockID, startEvent.AssociatedObjectID, startEvent.LockOwnerThreadID);
                continue;
            }

            // ContentionStop: look up the matching Start for this thread.
            PendingStart pending;

            if (!pendingByThread.TryGetValue(record.ThreadId, out pending))
            {
                continue;
            }

            pendingByThread.Remove(record.ThreadId);

            PayloadReader stopReader = new PayloadReader(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength, pointerSize);
            ClrContentionStop stopEvent = ClrContentionStop.Decode(stopReader);

            double durationMSec = stopEvent.DurationNs / 1e6;

            if (durationMSec <= 0 && qpcFrequency > 0)
            {
                // DurationNs not present or zero: fall back to QPC delta.
                durationMSec = relativeMSec - pending.RelativeMSec;
            }

            result.Add(new ContentionEvent(pending.RelativeMSec, durationMSec, pending.ContentionFlags, record.ThreadId, pending.Stack, pending.LockId, pending.AssociatedObjectId, pending.OwnerThreadId));
        }

        return result;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Contention)
