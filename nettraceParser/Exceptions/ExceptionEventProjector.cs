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
    // Index into the capture's StackTable (see StackTable.cs), copied from
    // the owning EventRecord.StackIndex - resolved at parse time, see
    // EventBlock.cs. StackTable.EmptyStackIndex when this throw wasn't
    // stack-walked; that index resolves to an empty array, never null.
    public readonly int StackIndex;

    public ExceptionEvent(DateTime timestamp, double relativeMSec, string exceptionType, string exceptionMessage, int hResult, ClrExceptionFlags flags, long threadId, int stackIndex)
    {
        this.Timestamp = timestamp;
        this.RelativeMSec = relativeMSec;
        this.ExceptionType = exceptionType;
        this.ExceptionMessage = exceptionMessage;
        this.HResult = hResult;
        this.Flags = flags;
        this.ThreadId = threadId;
        this.StackIndex = stackIndex;
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
        // One canonical string per distinct type name / message for the whole
        // capture - see Utf16StringPool. A real 3.23GB capture throws
        // 1,443,601 exceptions, and decoding each one's type and message into
        // its own fresh string was over half of this phase.
        Utf16StringPool exceptionTypePool = new Utf16StringPool();
        Utf16StringPool exceptionMessagePool = new Utf16StringPool();

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
            ClrExceptionThrown thrown = ClrExceptionThrown.Decode(reader, record.Version, exceptionTypePool, exceptionMessagePool);

            DateTime timestamp = default;
            double relativeMSec = default;

            if (qpcFrequency > 0)
            {
                long qpcDelta = record.TimeStampRelativeQPC - referenceQpc;
                timestamp = referenceUtc.AddSeconds(qpcDelta / (double)qpcFrequency);
                relativeMSec = qpcDelta * 1000.0 / qpcFrequency;
            }

            result.Add(new ExceptionEvent(timestamp, relativeMSec, thrown.ExceptionType, thrown.ExceptionMessage, thrown.ExceptionHRESULT, thrown.ExceptionFlags, record.ThreadId, record.StackIndex));
        }

        return result;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Exceptions)
