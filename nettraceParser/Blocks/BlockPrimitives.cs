////////////////////////////////////////////////////////////////////////////////
// Module: BlockPrimitives.cs
//
// Notes:
// Small byte-level helpers shared by every Block decoder: the 4-byte stream
// alignment padding documented in NetTraceFormat_v5.md, and the LEB128-style
// variable-length integer encoding used throughout the compressed event
// header and VarInt/VarUInt typed payload fields.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Text;

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class NettraceStrings
{
    // Event payload strings (provider/event/field names, and NullTerminatedUTF16String
    // typed fields) are 2-byte-per-char UTF-16, null terminated - a different encoding
    // than Deserializer.ReadString()'s length-prefixed UTF8 FastSerialization strings.
    public static string ReadNullTerminatedUtf16String(IStreamReader reader)
    {
        StringBuilder builder = new StringBuilder();
        byte[] charBytes = new byte[2];

        while (true)
        {
            reader.Read(charBytes, 0, 2);
            char nextChar = (char)(charBytes[0] | (charBytes[1] << 8));

            if (nextChar == '\0')
            {
                break;
            }

            builder.Append(nextChar);
        }

        return builder.ToString();
    }
}

public static class NettraceBlockAlignment
{
    public static void SkipPaddingToFourByteAlignment(Deserializer deserializer)
    {
        long currentPosition = (long)deserializer.Current;
        int remainder = (int)(currentPosition % 4);

        if (remainder != 0)
        {
            int paddingBytes = 4 - remainder;
            long targetPosition = currentPosition + paddingBytes;
            deserializer.Reader.Goto((StreamLabel)targetPosition);
        }
    }
}

public static class VarIntReader
{
    public static uint ReadVarUInt32(IStreamReader reader)
    {
        uint result = 0;
        int shift = 0;

        while (true)
        {
            byte nextByte = reader.ReadByte();
            result |= (uint)(nextByte & 0x7F) << shift;

            if ((nextByte & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }

    public static ulong ReadVarUInt64(IStreamReader reader)
    {
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            byte nextByte = reader.ReadByte();
            result |= (ulong)(nextByte & 0x7F) << shift;

            if ((nextByte & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return result;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
