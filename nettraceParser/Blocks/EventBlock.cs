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

    private readonly Dictionary<int, EventMetadata> metadataById;
    private readonly List<EventRecord> events;

    public int EventCount { get; set; }
    public int SkippedEventCount { get; set; }

    public EventBlock(Dictionary<int, EventMetadata> metadataById, List<EventRecord> events)
    {
        this.metadataById = metadataById;
        this.events = events;
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
                // for those providers (Gc/GcEventProjector) decode PayloadBytes
                // themselves using hardcoded offsets from the CLR ETW manifest.
                byte[] payloadBytes = new byte[eventHeader.PayloadSize];
                deserializer.Read(payloadBytes, 0, eventHeader.PayloadSize);

                EventRecord record = new EventRecord();
                record.ProviderName = metadata.ProviderName;
                record.EventName = metadata.EventName;
                record.EventId = metadata.EventId;
                record.Version = metadata.Version;
                record.TimeStampRelativeQPC = eventHeader.TimeStamp;
                record.ThreadId = eventHeader.ThreadId;
                record.StackId = eventHeader.StackId;
                record.PayloadBytes = payloadBytes;

                if (metadata.Fields.Count > 0)
                {
                    try
                    {
                        deserializer.Reader.Goto((StreamLabel)payloadStart);
                        record.Fields = FieldValueReader.ReadFields(deserializer.Reader, metadata.Fields);
                    }
                    catch (Exception)
                    {
                        // A field type we don't decode yet (e.g. an Array-typed field).
                        // PayloadBytes was already captured above regardless, and
                        // payloadEnd below re-syncs the stream either way.
                        record.Fields = new Dictionary<string, object>();
                    }
                }
                else
                {
                    record.Fields = new Dictionary<string, object>();
                }

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
