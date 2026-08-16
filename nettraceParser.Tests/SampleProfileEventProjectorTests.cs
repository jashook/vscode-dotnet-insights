////////////////////////////////////////////////////////////////////////////////
// Module: SampleProfileEventProjectorTests.cs
//
// Notes:
// The rules SampleProfileEventProjector.Project actually needs to guard
// against regressing: filtering to the Microsoft-DotNETCore-SampleProfiler
// provider's EventId 0 (confirmed against a real capture - see that file's
// own header comment), passing the already-resolved Stack/ThreadId straight
// through unchanged, and the QPC-to-relative-ms conversion shared with
// GcEventProjector/AllocationEventProjector.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Cpu;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class SampleProfileEventProjectorTests
{
    // One stack table for the whole class so the static Make* helpers
    // below can register stacks too - see TestStacks.cs.
    private static readonly TestStacks stacks = new TestStacks();

    private const string SampleProfilerProviderName = "Microsoft-DotNETCore-SampleProfiler";
    private const long QpcFrequency = 10_000_000;

    private static EventRecord MakeSampleEvent(long timeStampQpc, long threadId, int stackIndex)
    {
        return new EventRecord(SampleProfilerProviderName, string.Empty, eventId: 0, version: 0, timeStampQpc, threadId, stackIndex, fields: null, payloadBuffer: System.Array.Empty<byte>(), payloadOffset: 0, payloadLength: 0);
    }

    [Fact]
    public void Project_DecodesSampleEventsWithStackAndThreadId()
    {
        int stack = stacks.Index(0x1000, 0x2000, 0x3000);
        List<EventRecord> events = new List<EventRecord>
        {
            MakeSampleEvent(timeStampQpc: 50000, threadId: 42, stackIndex: stack)
        };

        List<SampleEvent> projected = SampleProfileEventProjector.Project(events, qpcFrequency: QpcFrequency, referenceQpc: 0);

        Assert.Single(projected);

        SampleEvent sampleEvent = projected[0];
        Assert.Equal(42, sampleEvent.ThreadId);
        Assert.Equal(stack, sampleEvent.StackIndex);
        // 50000 QPC ticks @ 10,000,000/sec == 5ms.
        Assert.Equal(5.0, sampleEvent.RelativeMSec, precision: 6);
    }

    [Fact]
    public void Project_IgnoresEventsFromOtherProviders()
    {
        EventRecord foreignEvent = new EventRecord("Some-Other-Provider", string.Empty, eventId: 0, version: 0, timeStampRelativeQpc: 0, threadId: 0, stackIndex: StackTable.EmptyStackIndex, fields: null, payloadBuffer: System.Array.Empty<byte>(), payloadOffset: 0, payloadLength: 0);

        List<SampleEvent> projected = SampleProfileEventProjector.Project(new List<EventRecord> { foreignEvent }, qpcFrequency: QpcFrequency, referenceQpc: 0);

        Assert.Empty(projected);
    }

    [Fact]
    public void Project_IgnoresNonZeroEventIdsOnTheSameProvider()
    {
        EventRecord otherEvent = new EventRecord(SampleProfilerProviderName, string.Empty, eventId: 1, version: 0, timeStampRelativeQpc: 0, threadId: 0, stackIndex: StackTable.EmptyStackIndex, fields: null, payloadBuffer: System.Array.Empty<byte>(), payloadOffset: 0, payloadLength: 0);

        List<SampleEvent> projected = SampleProfileEventProjector.Project(new List<EventRecord> { otherEvent }, qpcFrequency: QpcFrequency, referenceQpc: 0);

        Assert.Empty(projected);
    }

    [Fact]
    public void Project_ComputesRelativeMSecAgainstReferenceQpcNotAbsoluteZero()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeSampleEvent(timeStampQpc: 1_010_000, threadId: 1, stackIndex: stacks.Index(0x1000))
        };

        List<SampleEvent> projected = SampleProfileEventProjector.Project(events, qpcFrequency: QpcFrequency, referenceQpc: 1_000_000);

        // (1,010,000 - 1,000,000) QPC ticks @ 10,000,000/sec == 1ms.
        Assert.Equal(1.0, projected[0].RelativeMSec, precision: 6);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
