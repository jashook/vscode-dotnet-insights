////////////////////////////////////////////////////////////////////////////////
// Module: FieldValueReader.cs
//
// Notes:
// Walks an EventMetadata's field list and decodes an event payload's raw
// bytes into named/typed values. This is the metadata-driven, per-event-type-
// agnostic decoding step: it has no knowledge of GC or any other specific
// provider, only of the primitive type codes NetTraceFormat_v5.md defines.
//
// Array-typed fields (V2Params metadata tags, format V5+) are not decoded
// here yet - hitting one throws NotSupportedException, which the caller
// (EventBlock) treats as a per-event failure, not a stream-alignment failure,
// since payload boundaries are always tracked by PayloadSize independent of
// how many fields were actually understood.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class FieldValueReader
{
    public static Dictionary<string, object> ReadFields(IStreamReader reader, List<FieldDefinition> fields)
    {
        Dictionary<string, object> values = new Dictionary<string, object>(fields.Count);

        for (int fieldIndex = 0; fieldIndex < fields.Count; ++fieldIndex)
        {
            FieldDefinition field = fields[fieldIndex];
            values[field.Name] = ReadFieldValue(reader, field);
        }

        return values;
    }

    private static object ReadFieldValue(IStreamReader reader, FieldDefinition field)
    {
        switch (field.TypeCode)
        {
            case FieldTypeCode.Object:
                return ReadFields(reader, field.NestedFields);
            case FieldTypeCode.Boolean32:
                return reader.ReadInt32() != 0;
            case FieldTypeCode.Boolean8:
                return reader.ReadByte() != 0;
            case FieldTypeCode.UTF16CodeUnit:
                return ReadUtf16CodeUnit(reader);
            case FieldTypeCode.SByte:
                return (sbyte)reader.ReadByte();
            case FieldTypeCode.Byte:
                return reader.ReadByte();
            case FieldTypeCode.Int16:
                return reader.ReadInt16();
            case FieldTypeCode.UInt16:
                return (ushort)reader.ReadInt16();
            case FieldTypeCode.Int32:
                return reader.ReadInt32();
            case FieldTypeCode.UInt32:
                return (uint)reader.ReadInt32();
            case FieldTypeCode.Int64:
                return reader.ReadInt64();
            case FieldTypeCode.UInt64:
                return (ulong)reader.ReadInt64();
            case FieldTypeCode.Single:
                return ReadSingle(reader);
            case FieldTypeCode.Double:
                return ReadDouble(reader);
            case FieldTypeCode.DateTime:
                return reader.ReadInt64();
            case FieldTypeCode.Guid:
                return ReadGuid(reader);
            case FieldTypeCode.NullTerminatedUTF16String:
                return NettraceStrings.ReadNullTerminatedUtf16String(reader);
            case FieldTypeCode.VarInt:
                return VarIntReader.ReadVarUInt64(reader);
            case FieldTypeCode.VarUInt:
                return VarIntReader.ReadVarUInt64(reader);
            default:
                throw new NotSupportedException($"Field type code {field.TypeCode} for field '{field.Name}' is not yet supported.");
        }
    }

    private static char ReadUtf16CodeUnit(IStreamReader reader)
    {
        byte[] charBytes = new byte[2];
        reader.Read(charBytes, 0, 2);
        return (char)(charBytes[0] | (charBytes[1] << 8));
    }

    private static float ReadSingle(IStreamReader reader)
    {
        byte[] valueBytes = new byte[4];
        reader.Read(valueBytes, 0, 4);
        return BitConverter.ToSingle(valueBytes, 0);
    }

    private static double ReadDouble(IStreamReader reader)
    {
        byte[] valueBytes = new byte[8];
        reader.Read(valueBytes, 0, 8);
        return BitConverter.ToDouble(valueBytes, 0);
    }

    private static Guid ReadGuid(IStreamReader reader)
    {
        byte[] guidBytes = new byte[16];
        reader.Read(guidBytes, 0, 16);
        return new Guid(guidBytes);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
