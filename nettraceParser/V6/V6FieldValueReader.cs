////////////////////////////////////////////////////////////////////////////////
// Module: V6FieldValueReader.cs
//
// Notes:
// Decodes a self-describing v6 event payload into named field values, the way
// Blocks/FieldValueReader.cs does for v5. Only Universal.System events go
// through this (see V6Reader.AddEvent for why nothing high-volume does), so it
// is written for clarity rather than for the per-event budget the v5 reader
// has to respect.
//
// ONE QUIRK WORTH KNOWING, because the spec text and the wire disagree if read
// carelessly: the Universal providers declare their STRING fields as
// UTF8CodeUnit (type code 23), which NetTraceFormat.md defines as "a 1-byte
// UTF8 code unit" - i.e. one character, not a string. The actual encoding is
// the one UniversalProviders.md specifies for these providers: "All strings
// are length-prefixed (16-bit unsigned integer) followed by UTF8 bytes. Not
// null-terminated." Verified against the reference capture - a ProcessSymbol
// row decodes its four varuints and then `1e 00` followed by exactly 30 bytes
// of "entry_S..." - so a UTF8CodeUnit-typed field is read here as a whole
// uint16-length-prefixed string. Reading it as a single byte instead
// desynchronizes the rest of the payload rather than failing.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.V6 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class V6FieldValueReader
{
    public static Dictionary<string, object> ReadFields(ReadOnlySpan<byte> payload, List<FieldDefinition> fields, V6Utf8StringPool stringPool)
    {
        Dictionary<string, object> values = new Dictionary<string, object>(fields.Count);
        V6SpanReader reader = new V6SpanReader(payload);

        for (int fieldIndex = 0; fieldIndex < fields.Count; ++fieldIndex)
        {
            FieldDefinition field = fields[fieldIndex];

            object value;

            if (!TryReadValue(ref reader, field, stringPool, out value))
            {
                // A type this reader does not decode has no known width, so
                // nothing after it can be located. Everything decoded so far
                // is still returned - the caller's consumers look fields up by
                // name and tolerate a missing one.
                break;
            }

            values[field.Name] = value;
        }

        return values;
    }

    private static bool TryReadValue(ref V6SpanReader reader, FieldDefinition field, V6Utf8StringPool stringPool, out object value)
    {
        value = null;

        switch (field.TypeCode)
        {
            case FieldTypeCode.UTF8CodeUnit:
            {
                // See this file's header: a whole uint16-length-prefixed UTF-8
                // string, not one code unit.
                int byteCount = reader.ReadUInt16();
                value = stringPool.GetOrAdd(reader.ReadBytes(byteCount));
                return true;
            }

            case FieldTypeCode.VarUInt:
            {
                value = (long)reader.ReadVarUInt64();
                return true;
            }

            case FieldTypeCode.VarInt:
            {
                value = reader.ReadVarInt64();
                return true;
            }

            case FieldTypeCode.Boolean8:
            {
                value = reader.ReadUInt8() != 0;
                return true;
            }

            case FieldTypeCode.Boolean32:
            {
                value = reader.ReadInt32() != 0;
                return true;
            }

            case FieldTypeCode.SByte:
            {
                value = (sbyte)reader.ReadUInt8();
                return true;
            }

            case FieldTypeCode.Byte:
            {
                value = reader.ReadUInt8();
                return true;
            }

            case FieldTypeCode.Int16:
            {
                value = reader.ReadInt16();
                return true;
            }

            case FieldTypeCode.UInt16:
            {
                value = reader.ReadUInt16();
                return true;
            }

            case FieldTypeCode.Int32:
            {
                value = reader.ReadInt32();
                return true;
            }

            case FieldTypeCode.UInt32:
            {
                value = reader.ReadUInt32();
                return true;
            }

            case FieldTypeCode.Int64:
            {
                value = reader.ReadInt64();
                return true;
            }

            case FieldTypeCode.UInt64:
            {
                value = (long)reader.ReadUInt64();
                return true;
            }

            case FieldTypeCode.Single:
            {
                value = BitConverter.Int32BitsToSingle(reader.ReadInt32());
                return true;
            }

            case FieldTypeCode.Double:
            {
                value = BitConverter.Int64BitsToDouble(reader.ReadInt64());
                return true;
            }

            case FieldTypeCode.Guid:
            {
                value = reader.ReadGuid();
                return true;
            }

            default:
            {
                return false;
            }
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.V6)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
