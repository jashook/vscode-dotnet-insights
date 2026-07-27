////////////////////////////////////////////////////////////////////////////////
// Module: MethodSymbolTableTests.cs
//
// Notes:
// ClrMethodRecord.Decode's layout was pinned by dumping and hand-decoding
// three real MethodDCStartVerbose payloads (see ClrMethodRundown.cs's own
// header comment for the full reasoning) - the synthetic-payload tests here
// pin that exact byte layout with a readable PayloadBuilder-based test
// instead of only trusting the one-time manual decode. The real-capture
// test then closes the loop this whole "Drill Down" feature depends on:
// resolving the actual stacks decoded by StackBlockTests.cs against real
// method names, not placeholders.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Rundown;
using DotnetInsights.NetTrace.Tests;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class MethodSymbolTableTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "trace2.nettrace");

    private static byte[] MakeMethodDCStartVerbosePayload(long methodId, long moduleId, long methodStartAddress, int methodSize, int methodToken, int methodFlags, string methodNamespace, string methodName, string signature, int pointerSize)
    {
        return new PayloadBuilder()
            .WriteAddress(methodId, pointerSize)
            .WriteAddress(moduleId, pointerSize)
            .WriteAddress(methodStartAddress, pointerSize)
            .WriteInt32(methodSize)
            .WriteInt32(methodToken)
            .WriteInt32(methodFlags)
            .WriteUnicodeString(methodNamespace)
            .WriteUnicodeString(methodName)
            .WriteUnicodeString(signature)
            .ToArray();
    }

    [Fact]
    public void ClrMethodRecord_Decode_ParsesRealPayloadLayout()
    {
        byte[] payload = MakeMethodDCStartVerbosePayload(
            methodId: 0x0000000109542030,
            moduleId: 0x0000000108AB2D00,
            methodStartAddress: 4450820184,
            methodSize: 1152,
            methodToken: 0x06000859,
            methodFlags: 398,
            methodNamespace: "System.Buffers.SearchValues",
            methodName: "TryGetSingleRange",
            signature: "generic bool  (...)",
            pointerSize: 8);

        ClrMethodRecord method = ClrMethodRecord.Decode(new PayloadReader(payload, 8));

        Assert.NotNull(method);
        Assert.Equal(4450820184, method.MethodStartAddress);
        Assert.Equal(1152, method.MethodSize);
        Assert.Equal("System.Buffers.SearchValues.TryGetSingleRange", method.DisplayName);
    }

    [Fact]
    public void ClrMethodRecord_Decode_UsesBareMethodNameWhenNamespaceIsEmpty()
    {
        byte[] payload = MakeMethodDCStartVerbosePayload(
            methodId: 1, moduleId: 2, methodStartAddress: 1000, methodSize: 16, methodToken: 0x06000001, methodFlags: 0,
            methodNamespace: "", methodName: "<Main>$", signature: "void  (class System.String[])", pointerSize: 8);

        ClrMethodRecord method = ClrMethodRecord.Decode(new PayloadReader(payload, 8));

        Assert.Equal("<Main>$", method.DisplayName);
    }

    [Fact]
    public void MethodSymbolTable_Resolve_FindsTheContainingRange()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRundownEvent(startAddress: 1000, size: 100, name: "TypeA.MethodA"),
            MakeRundownEvent(startAddress: 2000, size: 50, name: "TypeB.MethodB")
        };

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(events, pointerSize: 8);

        Assert.Equal("TypeA.MethodA", symbolTable.Resolve(1050));
        Assert.Equal("TypeA.MethodA", symbolTable.Resolve(1000));       // range start, inclusive
        Assert.Equal("TypeB.MethodB", symbolTable.Resolve(2049));       // range end, exclusive - 2049 is the last valid byte
        Assert.Equal("TypeB.MethodB", symbolTable.Resolve(2000));
    }

    [Fact]
    public void MethodSymbolTable_Resolve_ReturnsPlaceholderForAnUnresolvedAddress()
    {
        List<EventRecord> events = new List<EventRecord> { MakeRundownEvent(startAddress: 1000, size: 100, name: "TypeA.MethodA") };
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(events, pointerSize: 8);

        string resolved = symbolTable.Resolve(9999);

        Assert.StartsWith("<unresolved", resolved);
    }

    [Fact]
    public void MethodSymbolTable_Resolve_HandlesAdjacentNonOverlappingRangesCorrectly()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            // Deliberately registered out of address order - Build sorts internally.
            MakeRundownEvent(startAddress: 2000, size: 100, name: "Second"),
            MakeRundownEvent(startAddress: 1000, size: 100, name: "First"),
            MakeRundownEvent(startAddress: 3000, size: 100, name: "Third")
        };

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(events, pointerSize: 8);

        Assert.Equal("First", symbolTable.Resolve(1099));
        Assert.Equal("Second", symbolTable.Resolve(2000));
        Assert.Equal("Third", symbolTable.Resolve(3099));
        Assert.StartsWith("<unresolved", symbolTable.Resolve(1999));  // gap between First and Second
    }

    [Fact]
    public void MethodSymbolTable_Build_IgnoresEventsFromOtherProviders()
    {
        byte[] foreignPayload = MakeMethodDCStartVerbosePayload(1, 2, 1000, 100, 0x06000001, 0, "T", "M", "sig", 8);

        EventRecord foreignEvent = new EventRecord("Some-Other-Provider", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stackId: 0, fields: null, foreignPayload, payloadOffset: 0, foreignPayload.Length);

        MethodSymbolTable symbolTable = MethodSymbolTable.Build(new List<EventRecord> { foreignEvent }, pointerSize: 8);

        Assert.StartsWith("<unresolved", symbolTable.Resolve(1050));
    }

    private static EventRecord MakeRundownEvent(long startAddress, int size, string name)
    {
        byte[] payload = MakeMethodDCStartVerbosePayload(1, 2, startAddress, size, 0x06000001, 0, "", name, "sig", 8);

        return new EventRecord("Microsoft-Windows-DotNETRuntimeRundown", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stackId: 0, fields: null, payload, payloadOffset: 0, payload.Length);
    }

    [Fact]
    public void MethodSymbolTable_ResolvesRealDecodedStacksToRealMethodNames()
    {
        // Closes the loop this whole feature depends on: every stack
        // decoded by StackBlockTests.cs (real StackBlock data) resolved
        // against a real symbol table (real MethodDCStartVerbose rundown
        // data) - at least one frame across all of them must be a real
        // method name, not every frame falling back to "<unresolved ...>"
        // (which would mean the rundown decode silently failed, e.g. a
        // wrong offset producing garbage addresses that never match).
        NettraceFile file = NettraceFile.Read(FixturePath);
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(file.Events, file.Header.PointerSize);

        int resolvedFrameCount = 0;
        int totalFrameCount = 0;

        foreach (KeyValuePair<int, long[]> stackEntry in file.StacksById)
        {
            foreach (long instructionPointer in stackEntry.Value)
            {
                ++totalFrameCount;
                string resolved = symbolTable.Resolve(instructionPointer);

                if (!resolved.StartsWith("<unresolved"))
                {
                    ++resolvedFrameCount;
                }
            }
        }

        Assert.True(totalFrameCount > 0);
        Assert.True(resolvedFrameCount > 0, "Expected at least one real stack frame to resolve to an actual method name.");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
