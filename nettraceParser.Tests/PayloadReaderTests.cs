////////////////////////////////////////////////////////////////////////////////
// Module: PayloadReaderTests.cs
//
// Notes:
// PayloadReader is the fixed-offset byte reader every CLR-provider event
// decoder in Gc/ClrGcTypes.cs and Gc/ClrGcPerHeapHistory.cs is built on -
// a bug here would silently corrupt every decoded field downstream, so it
// gets tested directly against hand-built byte arrays rather than only
// indirectly through the decoders that use it.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Text;

using DotnetInsights.NetTrace.Gc;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class PayloadReaderTests
{
    [Fact]
    public void GetInt16At_ReadsLittleEndianInt16()
    {
        byte[] payload = BitConverter.GetBytes((short)12345);
        PayloadReader reader = new PayloadReader(payload, 8);

        Assert.Equal((short)12345, reader.GetInt16At(0));
    }

    [Fact]
    public void GetInt32At_ReadsLittleEndianInt32()
    {
        byte[] payload = BitConverter.GetBytes(123456789);
        PayloadReader reader = new PayloadReader(payload, 8);

        Assert.Equal(123456789, reader.GetInt32At(0));
    }

    [Fact]
    public void GetInt64At_ReadsLittleEndianInt64()
    {
        byte[] payload = BitConverter.GetBytes(9876543210123L);
        PayloadReader reader = new PayloadReader(payload, 8);

        Assert.Equal(9876543210123L, reader.GetInt64At(0));
    }

    [Fact]
    public void GetByteAt_ReadsSingleByte()
    {
        byte[] payload = new byte[] { 0x00, 0x2A, 0x00 };
        PayloadReader reader = new PayloadReader(payload, 8);

        Assert.Equal((byte)0x2A, reader.GetByteAt(1));
    }

    [Fact]
    public void GetAddressAt_Reads4BytesOn32BitTrace()
    {
        // A 64-bit value here would be misread if pointerSize weren't
        // respected - only the first 4 bytes should be consumed.
        byte[] payload = BitConverter.GetBytes(0x11223344UL);
        PayloadReader reader = new PayloadReader(payload, 4);

        Assert.Equal(0x11223344L, reader.GetAddressAt(0));
    }

    [Fact]
    public void GetAddressAt_Reads8BytesOn64BitTrace()
    {
        byte[] payload = BitConverter.GetBytes(0x1122334455667788L);
        PayloadReader reader = new PayloadReader(payload, 8);

        Assert.Equal(0x1122334455667788L, reader.GetAddressAt(0));
    }

    [Fact]
    public void GetAddressAt_DoesNotSignExtendA4ByteHighBitValue()
    {
        // 0xFFFFFFFF as a signed Int32 is -1 - GetAddressAt must return it
        // as the unsigned address 4294967295, not -1, or every downstream
        // "is this a real pointer" comparison would be wrong.
        byte[] payload = BitConverter.GetBytes(0xFFFFFFFFU);
        PayloadReader reader = new PayloadReader(payload, 4);

        Assert.Equal(4294967295L, reader.GetAddressAt(0));
    }

    [Fact]
    public void GetUnicodeStringAt_ReadsNullTerminatedUtf16String()
    {
        byte[] stringBytes = Encoding.Unicode.GetBytes("System.Byte[]\0");
        PayloadReader reader = new PayloadReader(stringBytes, 8);

        Assert.Equal("System.Byte[]", reader.GetUnicodeStringAt(0));
    }

    [Fact]
    public void GetUnicodeStringAt_ReadsEmptyStringWhenImmediatelyNullTerminated()
    {
        byte[] stringBytes = new byte[] { 0x00, 0x00 };
        PayloadReader reader = new PayloadReader(stringBytes, 8);

        Assert.Equal(string.Empty, reader.GetUnicodeStringAt(0));
    }

    [Fact]
    public void SkipUnicodeString_ReturnsOffsetImmediatelyAfterNullTerminator()
    {
        byte[] payload = Encoding.Unicode.GetBytes("AB\0");
        PayloadReader reader = new PayloadReader(payload, 8);

        // "AB" -> 2 chars * 2 bytes + 2-byte null terminator = 6 bytes total.
        Assert.Equal(6, reader.SkipUnicodeString(0));
    }

    [Fact]
    public void HostOffset_IsUnchangedOn4ByteHost()
    {
        PayloadReader reader = new PayloadReader(new byte[64], 4);

        // pointerSize - 4 == 0, so every pointer consumed so far contributes nothing.
        Assert.Equal(46, reader.HostOffset(46, 6));
    }

    [Fact]
    public void HostOffset_AddsFourBytesPerConsumedPointerOn8ByteHost()
    {
        PayloadReader reader = new PayloadReader(new byte[64], 8);

        // Matches ClrGcHeap.Decode's real usage: HostOffset(46, 6) on a
        // 64-bit trace is 46 + 6*(8-4) = 70.
        Assert.Equal(70, reader.HostOffset(46, 6));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
