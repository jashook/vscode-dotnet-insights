////////////////////////////////////////////////////////////////////////////////
// Module: MetadataBlock.cs
//
// Notes:
// Decodes a MetadataBlock: a sequence of event blobs (same compressed header
// format as EventBlock) whose payload describes another event's schema
// (NetTraceFormat_v5.md "Metadata Event Payload Format"). Populates the
// shared MetadataId -> EventMetadata dictionary that EventBlock consults to
// decode ordinary events.
//
// Only the V1 field description is parsed (primitive/nested-struct fields).
// V2 metadata tags (which add Array field support, format V5+) are skipped -
// each event blob's own declared PayloadSize means unparsed trailing bytes
// never misalign the rest of the block, they just mean Array-typed fields
// aren't decoded yet.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class MetadataBlock : IFastSerializable, IFastSerializableVersion
{
    // NetTraceFormat_v5.md documents MetadataBlock as Version 2 / MinumumReaderVersion 2.
    public int Version => 2;
    public int MinimumVersionCanRead => 2;
    public int MinimumReaderVersion => 2;

    private readonly Dictionary<int, EventMetadata> metadataById;

    public int EventCount { get; set; }

    public MetadataBlock(Dictionary<int, EventMetadata> metadataById)
    {
        this.metadataById = metadataById;
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

            EventMetadata metadata = ReadMetadataPayload(deserializer.Reader);
            this.metadataById[metadata.MetadataId] = metadata;
            ++this.EventCount;

            deserializer.Reader.Goto((StreamLabel)payloadEnd);
        }

        deserializer.Reader.Goto((StreamLabel)blockContentEnd);
    }

    private static EventMetadata ReadMetadataPayload(IStreamReader reader)
    {
        EventMetadata metadata = new EventMetadata();

        metadata.MetadataId = reader.ReadInt32();
        metadata.ProviderName = NettraceStrings.ReadNullTerminatedUtf16String(reader);
        metadata.EventId = reader.ReadInt32();
        metadata.EventName = NettraceStrings.ReadNullTerminatedUtf16String(reader);
        metadata.Keywords = reader.ReadInt64();
        metadata.Version = reader.ReadInt32();
        metadata.Level = reader.ReadInt32();
        metadata.Fields = ReadFieldList(reader);

        return metadata;
    }

    private static List<FieldDefinition> ReadFieldList(IStreamReader reader)
    {
        int fieldCount = reader.ReadInt32();
        List<FieldDefinition> fields = new List<FieldDefinition>(fieldCount);

        for (int fieldIndex = 0; fieldIndex < fieldCount; ++fieldIndex)
        {
            FieldDefinition field = new FieldDefinition();
            field.TypeCode = (FieldTypeCode)reader.ReadInt32();

            if (field.TypeCode == FieldTypeCode.Object)
            {
                field.NestedFields = ReadFieldList(reader);
            }

            field.Name = NettraceStrings.ReadNullTerminatedUtf16String(reader);

            fields.Add(field);
        }

        return fields;
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
