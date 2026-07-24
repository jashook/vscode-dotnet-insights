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
// MetadataBlock entries carry no field list at all - so PayloadBytes carries
// the raw bytes for consumers (Gc/GcEventProjector) that decode those events
// using hardcoded offsets from the CLR ETW manifest instead.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventRecord
{
    public string ProviderName { get; set; }
    public string EventName { get; set; }
    public int EventId { get; set; }
    public int Version { get; set; }
    public long TimeStampRelativeQPC { get; set; }
    public long ThreadId { get; set; }
    public int StackId { get; set; }
    public Dictionary<string, object> Fields { get; set; }
    public byte[] PayloadBytes { get; set; }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
