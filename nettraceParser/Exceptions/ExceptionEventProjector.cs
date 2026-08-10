////////////////////////////////////////////////////////////////////////////////
// Module: ExceptionEventProjector.cs
//
// Notes:
// Sibling projector to Gc/AllocationEventProjector.cs, against the same
// EventRecord stream - decodes ExceptionThrown_V1 events
// (ClrExceptionThrown.Decode, ClrExceptionTypes.cs). Only Version >= 1
// payloads are decoded, matching every real capture checked so far (see
// ClrExceptionTypes.cs's own header comment) and this codebase's existing
// convention of only supporting what current .NET actually emits.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Exceptions {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A readonly struct, not a class - ExceptionEventProjector.Project constructs
// one per thrown exception and holds them all in one List<ExceptionEvent>
// for the rest of the program's life, mirroring AllocationEvent's own
// reasoning (Gc/AllocationEventProjector.cs) for why a struct beats a class
// here despite being over the struct-passing convention's 16-byte
// threshold: no call site relies on reference semantics, and one
// contiguous array beats one heap object per exception.
public readonly struct ExceptionEvent
{
    public readonly DateTime Timestamp;
    public readonly double RelativeMSec;
    public readonly string ExceptionType;
    public readonly string ExceptionMessage;
    public readonly int HResult;
    public readonly ClrExceptionFlags Flags;
    public readonly long ThreadId;
    // Copied directly from the owning EventRecord.Stack, already resolved at
    // parse time (see EventBlock.cs/EventRecord.cs) - empty
    // (Array.Empty<long>()), never null, when this throw wasn't stack-walked.
    public readonly long[] Stack;

    public ExceptionEvent(DateTime timestamp, double relativeMSec, string exceptionType, string exceptionMessage, int hResult, ClrExceptionFlags flags, long threadId, long[] stack)
    {
        this.Timestamp = timestamp;
        this.RelativeMSec = relativeMSec;
        this.ExceptionType = exceptionType;
        this.ExceptionMessage = exceptionMessage;
        this.HResult = hResult;
        this.Flags = flags;
        this.ThreadId = threadId;
        this.Stack = stack;
    }
}

public static class ExceptionEventProjector
{
    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";

    // Same (events, pointerSize, qpcFrequency, referenceUtc, referenceQpc)
    // shape as GcEventProjector.Project/AllocationEventProjector.Project -
    // same reasoning applies (callers pass NettraceHeader's own
    // SyncTimeQPC/SyncTimeUtc).
    public static List<ExceptionEvent> Project(List<EventRecord> events, int pointerSize, long qpcFrequency, DateTime referenceUtc, long referenceQpc, Action<double> onProgress = null)
    {
        List<ExceptionEvent> result = new List<ExceptionEvent>();

        // events is the whole capture's event list, iterated as a Span over
        // the List<T>'s backing array rather than a plain `foreach` -
        // matches GcEventProjector.Project/AllocationEventProjector.Project's
        // own reasoning (a boxed/virtual IEnumerable<T> enumerator copying a
        // large struct per element measurably regressed once EventRecord
        // stopped being a cheap 8-byte class reference).
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

            if (record.EventId != ClrExceptionEventIds.ExceptionThrown || record.Version < 1)
            {
                continue;
            }

            PayloadReader reader = new PayloadReader(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength, pointerSize);
            ClrExceptionThrown thrown = ClrExceptionThrown.Decode(reader, record.Version);

            DateTime timestamp = default;
            double relativeMSec = default;

            if (qpcFrequency > 0)
            {
                long qpcDelta = record.TimeStampRelativeQPC - referenceQpc;
                timestamp = referenceUtc.AddSeconds(qpcDelta / (double)qpcFrequency);
                relativeMSec = qpcDelta * 1000.0 / qpcFrequency;
            }

            result.Add(new ExceptionEvent(timestamp, relativeMSec, thrown.ExceptionType, thrown.ExceptionMessage, thrown.ExceptionHRESULT, thrown.ExceptionFlags, record.ThreadId, record.Stack));
        }

        return result;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Exceptions)
