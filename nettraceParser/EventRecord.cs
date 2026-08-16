////////////////////////////////////////////////////////////////////////////////
// Module: EventRecord.cs
//
// Notes:
// A single decoded event, generic across every provider/event name. This is
// the boundary between the nettrace-specific decoding layers (NettraceFile,
// MetadataBlock, EventBlock) and any consumer (e.g. Gc/GcEventProjector) that
// only cares about specific providers/events.
//
// Fields is populated from the trace's own metadata when the provider is
// self-describing (e.g. Microsoft-DotNETCore-EventPipe). The CLR runtime
// provider (GC/JIT/... events) is manifest-based, not self-describing - its
// MetadataBlock entries carry no field list at all - so PayloadBuffer/
// PayloadOffset/PayloadLength carry the raw bytes for consumers
// (Gc/GcEventProjector) that decode those events using hardcoded offsets
// from the CLR ETW manifest instead.
//
// PayloadBuffer is the whole file's byte array (shared across every
// EventRecord, set by EventBlock.FromStream), not a per-event copy -
// PayloadOffset/PayloadLength mark this event's slice within it. This
// keeps the entire file resident in memory for the process's lifetime
// (already true anyway - NettraceFile.Read reads it whole via
// File.ReadAllBytes) rather than paying for 14.8M+ additional per-event
// array allocations and copies.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A readonly struct, not a class: EventBlock.FromStream constructs one per
// decoded event - 14.8M times for a real 5-minute capture, all held in one
// List<EventRecord> for the rest of the program's life. At ~70 bytes (over
// the struct-passing convention's 16-byte threshold), every consumer that
// iterates a List<EventRecord>/IEnumerable<EventRecord> in a hot loop
// (GcEventProjector, AllocationEventProjector, MethodSymbolTable) should
// avoid copying elements out repeatedly where practical. No call site relies
// on reference semantics (no null checks, no identity comparisons), so this
// trades 14.8M+ heap allocations for one contiguous List<T>'s worth of
// stack-sized values.
public readonly struct EventRecord
{
    public readonly string ProviderName;
    public readonly string EventName;
    public readonly int EventId;
    public readonly int Version;
    public readonly long TimeStampRelativeQPC;
    public readonly long ThreadId;
    // Resolved eagerly, at the moment this event is parsed (see
    // Blocks/EventBlock.cs), against whatever StackBlock data has been read
    // SO FAR - not a raw StackId int to be looked up later. This matters
    // because StackId values are recyclable: NetTraceFormat_v5.md's own
    // StackBlock section describes a *bounded* cache ("Events are only
    // allowed to refer to a stack id if there is no sequence point in
    // between the event and the stack") specifically so a reader doesn't
    // need to keep every stack in memory - which means a later StackBlock
    // can legitimately reuse a numeric id an earlier, already-evicted stack
    // used. A single whole-file `Dictionary<int, long[]>`, looked up lazily
    // after the entire file has been read (the original design), silently
    // resolves EVERY event's StackId against whichever stack most recently
    // claimed that number by the time parsing finished - which, for a real
    // multi-million-event capture, is essentially never the stack that
    // event's own StackId actually meant at the point it was recorded. This
    // was a real, confirmed bug: cross-checked against
    // Microsoft.Diagnostics.Tracing.TraceEvent on a real production capture,
    // 0 of 30 sampled GCAllocationTick events' leaf frames agreed before this
    // fix - every single stack was silently wrong, not just an occasional
    // collision. Capturing the array reference at parse time (this field) is
    // immune to later reuse of the same numeric id, since it holds the real
    // long[] object directly rather than a number to re-look-up afterward.
    // StackTable.EmptyStackIndex when the event has no stack (StackId 0, or
    // stack-walking wasn't enabled for that event) - that index resolves to a
    // real empty array, so no consumer needs a null or sentinel check.
    //
    // An INDEX rather than the long[] itself as of 2026-08-15: consumers group
    // events by stack constantly, and keying those groupings by the array's
    // object identity made RuntimeHelpers.GetHashCode the single largest cost
    // in the whole export (see StackTable.cs). It also drops one reference
    // from a struct that exists 35M times over.
    public readonly int StackIndex;
    public readonly Dictionary<string, object> Fields;
    public readonly byte[] PayloadBuffer;
    public readonly int PayloadOffset;
    public readonly int PayloadLength;

    public EventRecord(string providerName, string eventName, int eventId, int version, long timeStampRelativeQpc, long threadId, int stackIndex, Dictionary<string, object> fields, byte[] payloadBuffer, int payloadOffset, int payloadLength)
    {
        this.ProviderName = providerName;
        this.EventName = eventName;
        this.EventId = eventId;
        this.Version = version;
        this.TimeStampRelativeQPC = timeStampRelativeQpc;
        this.ThreadId = threadId;
        this.StackIndex = stackIndex;
        this.Fields = fields;
        this.PayloadBuffer = payloadBuffer;
        this.PayloadOffset = payloadOffset;
        this.PayloadLength = payloadLength;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
