////////////////////////////////////////////////////////////////////////////////
// Module: ClrExceptionTypesTests.cs
//
// Notes:
// Byte-offset regression tests for Exceptions/ClrExceptionTypes.cs's
// ExceptionThrown_V1 decoder, mirroring ClrGcTypesTests.cs's own approach
// (synthetic payload built to the documented layout, checked field by
// field). Unlike the GC decoders (verified only against real captures
// elsewhere in this repo), the second test below additionally pins the
// EXACT raw bytes of a real ExceptionThrown_V1 payload captured from
// testApps/ExceptionLoadGenerator/example-exceptions.nettrace - captured
// during this feature's own development by hand-decoding the real bytes
// and cross-checking every field against
// Microsoft.Diagnostics.Tracing.TraceEvent's own decoded values (see
// GroundTruthDiffTests.cs's ExceptionEventProjector test for the
// continuously-running version of that same cross-check). A pinned real
// payload catches a byte-offset regression a purely synthetic payload
// built from the same (possibly wrong) assumptions never could.
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Gc;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ClrExceptionTypesTests
{
    [Fact]
    public void ClrExceptionThrown_DecodesStringsAndFixedFieldsWhenVersionOne()
    {
        byte[] payload = new PayloadBuilder()
            .WriteUnicodeString("System.InvalidOperationException")  // ExceptionType @0
            .WriteUnicodeString("Widget cache is not initialized.")  // ExceptionMessage
            .WriteAddress(0x10BA6444C, 8)                            // ExceptionEIP
            .WriteInt32(unchecked((int)0x80131509))                  // ExceptionHRESULT
            .WriteInt16(0x10)                                        // ExceptionFlags (CLSCompliant)
            .WriteInt16(0)                                           // ClrInstanceID
            .ToArray();

        ClrExceptionThrown thrown = ClrExceptionThrown.Decode(new PayloadReader(payload, 8), version: 1);

        Assert.Equal("System.InvalidOperationException", thrown.ExceptionType);
        Assert.Equal("Widget cache is not initialized.", thrown.ExceptionMessage);
        Assert.Equal(0x10BA6444C, thrown.ExceptionEIP);
        Assert.Equal(unchecked((int)0x80131509), thrown.ExceptionHRESULT);
        Assert.Equal(ClrExceptionFlags.CLSCompliant, thrown.ExceptionFlags);
        Assert.Equal(0, thrown.ClrInstanceID);
    }

    [Fact]
    public void ClrExceptionThrown_DecodesCombinedFlags()
    {
        byte[] payload = new PayloadBuilder()
            .WriteUnicodeString("DotnetInsights.ExceptionLoadGenerator.WidgetNotFoundException")
            .WriteUnicodeString("Widget 2 was not found.")
            .WriteAddress(0x109D8EE5C, 8)
            .WriteInt32(unchecked((int)0x80131500))
            .WriteInt16(0x13)  // HasInnerException | Nested | CLSCompliant
            .WriteInt16(0)
            .ToArray();

        ClrExceptionThrown thrown = ClrExceptionThrown.Decode(new PayloadReader(payload, 8), version: 1);

        Assert.Equal(ClrExceptionFlags.HasInnerException | ClrExceptionFlags.Nested | ClrExceptionFlags.CLSCompliant, thrown.ExceptionFlags);
    }

    [Fact]
    public void ClrExceptionThrown_OmitsFixedFieldsWhenVersionZero()
    {
        byte[] payload = new PayloadBuilder()
            .WriteUnicodeString("System.Exception")
            .WriteUnicodeString("message")
            .ToArray();

        ClrExceptionThrown thrown = ClrExceptionThrown.Decode(new PayloadReader(payload, 8), version: 0);

        Assert.Equal("System.Exception", thrown.ExceptionType);
        Assert.Equal("message", thrown.ExceptionMessage);
        Assert.Equal(0, thrown.ExceptionEIP);
        Assert.Equal(0, thrown.ExceptionHRESULT);
        Assert.Equal(ClrExceptionFlags.None, thrown.ExceptionFlags);
    }

    // Exact raw bytes of a real ExceptionThrown_V1 payload (System.ArgumentException,
    // "Widget id must be non-empty.", 64-bit pointer size) captured from
    // testApps/ExceptionLoadGenerator/example-exceptions.nettrace - see this
    // file's own header comment.
    [Fact]
    public void ClrExceptionThrown_DecodesRealCapturedPayload()
    {
        byte[] payload = new byte[]
        {
            0x53, 0x00, 0x79, 0x00, 0x73, 0x00, 0x74, 0x00, 0x65, 0x00, 0x6D, 0x00, 0x2E, 0x00, 0x41, 0x00,
            0x72, 0x00, 0x67, 0x00, 0x75, 0x00, 0x6D, 0x00, 0x65, 0x00, 0x6E, 0x00, 0x74, 0x00, 0x45, 0x00,
            0x78, 0x00, 0x63, 0x00, 0x65, 0x00, 0x70, 0x00, 0x74, 0x00, 0x69, 0x00, 0x6F, 0x00, 0x6E, 0x00,
            0x00, 0x00,
            0x57, 0x00, 0x69, 0x00, 0x64, 0x00, 0x67, 0x00, 0x65, 0x00, 0x74, 0x00, 0x20, 0x00, 0x69, 0x00,
            0x64, 0x00, 0x20, 0x00, 0x6D, 0x00, 0x75, 0x00, 0x73, 0x00, 0x74, 0x00, 0x20, 0x00, 0x62, 0x00,
            0x65, 0x00, 0x20, 0x00, 0x6E, 0x00, 0x6F, 0x00, 0x6E, 0x00, 0x2D, 0x00, 0x65, 0x00, 0x6D, 0x00,
            0x70, 0x00, 0x74, 0x00, 0x79, 0x00, 0x2E, 0x00,
            0x00, 0x00,
            0x0C, 0x45, 0xA6, 0x0B, 0x01, 0x00, 0x00, 0x00,
            0x57, 0x00, 0x07, 0x80,
            0x10, 0x00,
            0x00, 0x00,
        };

        Assert.Equal(124, payload.Length);

        ClrExceptionThrown thrown = ClrExceptionThrown.Decode(new PayloadReader(payload, 8), version: 1);

        Assert.Equal("System.ArgumentException", thrown.ExceptionType);
        Assert.Equal("Widget id must be non-empty.", thrown.ExceptionMessage);
        Assert.Equal(0x10BA6450C, thrown.ExceptionEIP);
        Assert.Equal(unchecked((int)0x80070057), thrown.ExceptionHRESULT);
        Assert.Equal(ClrExceptionFlags.CLSCompliant, thrown.ExceptionFlags);
        Assert.Equal(0, thrown.ClrInstanceID);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
