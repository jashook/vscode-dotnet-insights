////////////////////////////////////////////////////////////////////////////////
// Module: EventBlock.cs
//
// Notes:
// Decodes an EventBlock: a sequence of compressed-header event blobs, each
// resolved against the shared MetadataId -> EventMetadata dictionary
// (populated by MetadataBlock) and decoded into a generic EventRecord via
// FieldValueReader. Nothing here is specific to any one provider or event -
// that interpretation belongs to consumers like Gc/GcEventProjector.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventBlock : IFastSerializable, IFastSerializableVersion
{
    // NetTraceFormat_v5.md documents EventBlock as Version 2 / MinumumReaderVersion 2.
    public int Version => 2;
    public int MinimumVersionCanRead => 2;
    public int MinimumReaderVersion => 2;

    // Shared across every EventRecord that has no manifest field list to
    // decode - which is every event from the CLR runtime provider (GC,
    // AllocationTick, ...), the only kind this parser actually consumes,
    // since manifest-based providers declare no fields at all (see
    // FromStream's own comment). Never mutated after construction -
    // record.Fields is write-once (see FromStream, the only assigner) - so
    // one shared empty instance is safe. Profiling a real 1GB/14.8M-event
    // capture (nearly all CLR-provider events) showed this allocating a
    // fresh, always-empty Dictionary once per event as one of the largest
    // contributors to parse time.
    private static readonly Dictionary<string, object> EmptyFields = new Dictionary<string, object>();

    private readonly Dictionary<int, EventMetadata> metadataById;
    private readonly List<EventRecord> events;
    // The whole file's bytes (NettraceFile.Read's fileBytes) - every EventRecord
    // this block produces stores an (offset, length) slice into this same shared
    // array instead of its own copied byte[]. See EventRecord.cs.
    private readonly byte[] fileBytes;

    public int EventCount { get; set; }
    public int SkippedEventCount { get; set; }

    public EventBlock(Dictionary<int, EventMetadata> metadataById, List<EventRecord> events, byte[] fileBytes)
    {
        this.metadataById = metadataById;
        this.events = events;
        this.fileBytes = fileBytes;
    }

    public void FromStream(Deserializer deserializer)
    {
        int blockSize;
        deserializer.Read(out blockSize);

        NettraceBlockAlignment.SkipPaddingToFourByteAlignment(deserializer);

        long blockContentStart = (long)deserializer.Current;
        long blockContentEnd = blockContentStart + blockSize;

        short headerSize;
        short headerFlags;
        long minTimeStamp;
        long maxTimeStamp;

        deserializer.Read(out headerSize);
        deserializer.Read(out headerFlags);
        deserializer.Read(out minTimeStamp);
        deserializer.Read(out maxTimeStamp);

        long headerEnd = blockContentStart + headerSize;
        deserializer.Reader.Goto((StreamLabel)headerEnd);

        CompressedEventBlobDecoderState decoderState = new CompressedEventBlobDecoderState();
        decoderState.TimeStamp = minTimeStamp;

        while ((long)deserializer.Current < blockContentEnd)
        {
            CompressedEventBlobHeader eventHeader = CompressedEventBlobHeader.Read(deserializer.Reader, decoderState);

            long payloadStart = (long)deserializer.Current;
            long payloadEnd = payloadStart + eventHeader.PayloadSize;

            EventMetadata metadata;
            if (this.metadataById.TryGetValue(eventHeader.MetadataId, out metadata))
            {
                // Always capture the raw payload - manifest-based providers (the CLR
                // runtime provider, for GC/JIT/... events) declare no field list at
                // all, so there is nothing for FieldValueReader to walk. Consumers
                // for those providers (Gc/GcEventProjector) decode the payload
                // themselves using hardcoded offsets from the CLR ETW manifest.
                // No allocation/copy needed here - this reader (MemoryStreamReader,
                // see NettraceFile.Read) operates directly on fileBytes, so
                // payloadStart is already an absolute offset into it and the
                // record can just reference that shared array + slice.
                Dictionary<string, object> fields;
                if (metadata.Fields.Count > 0)
                {
                    try
                    {
                        deserializer.Reader.Goto((StreamLabel)payloadStart);
                        fields = FieldValueReader.ReadFields(deserializer.Reader, metadata.Fields);
                    }
                    catch (Exception)
                    {
                        // A field type we don't decode yet (e.g. an Array-typed field).
                        // The payload slice was already captured above regardless, and
                        // payloadEnd below re-syncs the stream either way.
                        fields = EmptyFields;
                    }
                }
                else
                {
                    fields = EmptyFields;
                }

                EventRecord record = new EventRecord(
                    metadata.ProviderName,
                    metadata.EventName,
                    metadata.EventId,
                    metadata.Version,
                    eventHeader.TimeStamp,
                    eventHeader.ThreadId,
                    eventHeader.StackId,
                    fields,
                    this.fileBytes,
                    (int)payloadStart,
                    eventHeader.PayloadSize);

                this.events.Add(record);
                ++this.EventCount;
            }
            else
            {
                ++this.SkippedEventCount;
            }

            deserializer.Reader.Goto((StreamLabel)payloadEnd);
        }

        deserializer.Reader.Goto((StreamLabel)blockContentEnd);
    }

    public void ToStream(Serializer serializer)
    {
        throw new System.NotImplementedException("nettraceParser is read-only.");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
