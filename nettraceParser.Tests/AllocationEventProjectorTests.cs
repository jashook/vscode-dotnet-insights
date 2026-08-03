////////////////////////////////////////////////////////////////////////////////
// Module: AllocationEventProjectorTests.cs
//
// Notes:
// The one non-obvious rule in AllocationEventProjector.Project is the
// Version >= 2 filter (Version < 2 payloads have no TypeName, making them
// useless for "what's allocating" ranking - see the file's own header
// comment) - that filter, plus the QPC-to-relative-ms conversion shared
// with GcEventProjector, are what this file actually needs to guard against
// regressing.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Gc;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class AllocationEventProjectorTests
{
    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";
    private static readonly DateTime ReferenceUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
    private const long QpcFrequency = 10_000_000;

    private static EventRecord MakeAllocationTick(int version, long allocationAmount64, GCAllocationKind kind, string typeName, int heapIndex, long timeStampQpc)
    {
        PayloadBuilder builder = new PayloadBuilder()
            .WriteInt32((int)allocationAmount64)
            .WriteInt32((int)kind)
            .WriteInt16(1);

        if (version >= 2)
        {
            builder
                .WriteInt64(allocationAmount64)
                .WriteAddress(0, 8)
                .WriteUnicodeString(typeName)
                .WriteInt32(heapIndex);
        }

        byte[] payload = builder.ToArray();

        return new EventRecord(ClrProviderName, "GCAllocationTick", ClrGcEventIds.GCAllocationTick, version, timeStampQpc, threadId: 0, stack: System.Array.Empty<long>(), fields: null, payload, payloadOffset: 0, payload.Length);
    }

    [Fact]
    public void Project_DecodesVersionTwoTicksWithTypeNameAndHeapIndex()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeAllocationTick(version: 2, allocationAmount64: 106928, kind: GCAllocationKind.Small, typeName: "System.Byte[]", heapIndex: 3, timeStampQpc: 50000)
        };

        List<AllocationEvent> projected = AllocationEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Single(projected);

        AllocationEvent allocationEvent = projected[0];
        Assert.Equal(106928, allocationEvent.AllocationAmount);
        Assert.Equal(GCAllocationKind.Small, allocationEvent.AllocationKind);
        Assert.Equal("System.Byte[]", allocationEvent.TypeName);
        Assert.Equal(3, allocationEvent.HeapIndex);
        // 50000 QPC ticks @ 10,000,000/sec == 5ms.
        Assert.Equal(5.0, allocationEvent.RelativeMSec, precision: 6);
        Assert.Equal(ReferenceUtc.AddMilliseconds(5.0), allocationEvent.Timestamp);
    }

    [Fact]
    public void Project_SkipsTicksBelowVersionTwo()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeAllocationTick(version: 1, allocationAmount64: 4096, kind: GCAllocationKind.Small, typeName: null, heapIndex: 0, timeStampQpc: 0),
            MakeAllocationTick(version: 0, allocationAmount64: 4096, kind: GCAllocationKind.Small, typeName: null, heapIndex: 0, timeStampQpc: 0)
        };

        List<AllocationEvent> projected = AllocationEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Empty(projected);
    }

    [Fact]
    public void Project_IgnoresEventsFromOtherProviders()
    {
        EventRecord foreignEvent = new EventRecord("Some-Other-Provider", "GCAllocationTick", ClrGcEventIds.GCAllocationTick, version: 2, timeStampRelativeQpc: 0, threadId: 0, stack: System.Array.Empty<long>(), fields: null, new byte[64], payloadOffset: 0, payloadLength: 64);

        List<AllocationEvent> projected = AllocationEventProjector.Project(new List<EventRecord> { foreignEvent }, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Empty(projected);
    }

    [Fact]
    public void Project_IgnoresOtherClrEventTypes()
    {
        EventRecord gcStartEvent = new EventRecord(ClrProviderName, "GCStart", ClrGcEventIds.GCStart, version: 2, timeStampRelativeQpc: 0, threadId: 0, stack: System.Array.Empty<long>(), fields: null, new byte[64], payloadOffset: 0, payloadLength: 64);

        List<AllocationEvent> projected = AllocationEventProjector.Project(new List<EventRecord> { gcStartEvent }, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Empty(projected);
    }

    [Fact]
    public void Project_ComputesRelativeMSecAgainstReferenceQpcNotAbsoluteZero()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeAllocationTick(version: 2, allocationAmount64: 1024, kind: GCAllocationKind.Small, typeName: "T", heapIndex: 0, timeStampQpc: 1_010_000)
        };

        List<AllocationEvent> projected = AllocationEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 1_000_000);

        // (1,010,000 - 1,000,000) QPC ticks @ 10,000,000/sec == 1ms.
        Assert.Equal(1.0, projected[0].RelativeMSec, precision: 6);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
