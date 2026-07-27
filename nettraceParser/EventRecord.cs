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
    public readonly int StackId;
    public readonly Dictionary<string, object> Fields;
    public readonly byte[] PayloadBuffer;
    public readonly int PayloadOffset;
    public readonly int PayloadLength;

    public EventRecord(string providerName, string eventName, int eventId, int version, long timeStampRelativeQpc, long threadId, int stackId, Dictionary<string, object> fields, byte[] payloadBuffer, int payloadOffset, int payloadLength)
    {
        this.ProviderName = providerName;
        this.EventName = eventName;
        this.EventId = eventId;
        this.Version = version;
        this.TimeStampRelativeQPC = timeStampRelativeQpc;
        this.ThreadId = threadId;
        this.StackId = stackId;
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
