////////////////////////////////////////////////////////////////////////////////
// Module: EventMetadata.cs
//
// Notes:
// The schema for one event type, decoded from a MetadataBlock entry. Every
// EventBlock entry references one of these (by MetadataId) to know how to
// decode its payload bytes into named/typed fields.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Mirrors the EventPipeTypeCode enum used inside a MetadataBlock's V1 field
// descriptions (NetTraceFormat_v5.md). Value 2 is intentionally unused/reserved
// in the source format.
public enum FieldTypeCode
{
    Object = 1,
    Boolean32 = 3,
    UTF16CodeUnit = 4,
    SByte = 5,
    Byte = 6,
    Int16 = 7,
    UInt16 = 8,
    Int32 = 9,
    UInt32 = 10,
    Int64 = 11,
    UInt64 = 12,
    Single = 13,
    Double = 14,
    Decimal = 15,
    DateTime = 16,
    Guid = 17,
    NullTerminatedUTF16String = 18,
    Array = 19,
    VarInt = 20,
    VarUInt = 21,
    FixedLengthArray = 22,
    UTF8CodeUnit = 23,
    RelLoc = 24,
    DataLoc = 25,
    Boolean8 = 26
}

public class FieldDefinition
{
    public string Name { get; set; }
    public FieldTypeCode TypeCode { get; set; }

    // Only populated when TypeCode == FieldTypeCode.Object: the nested struct's
    // own field list, which can recurse to arbitrary depth.
    public List<FieldDefinition> NestedFields { get; set; }
}

public class EventMetadata
{
    public int MetadataId { get; set; }
    public string ProviderName { get; set; }
    public int EventId { get; set; }
    public string EventName { get; set; }
    public long Keywords { get; set; }
    public int Version { get; set; }
    public int Level { get; set; }
    public List<FieldDefinition> Fields { get; set; }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
