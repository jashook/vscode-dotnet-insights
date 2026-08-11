////////////////////////////////////////////////////////////////////////////////
// Module: TimeBreakdownBuilderTests.cs
//
// Notes:
// Covers the non-obvious rules TimeBreakdownBuilder.Build needs to keep:
// GcPercent/ContentionPercent are computed from a real summed duration
// against captureDurationMSec and CAN exceed 100% (multi-threaded contention
// wait time is not clamped - see the class's own header comment), while
// IdlePercent/CpuBoundPercent are a sample-count proportion that always sums
// to exactly 100% between themselves and is entirely independent of
// captureDurationMSec. HasCaptureDuration/HasCpuSampleBreakdown gate the
// two pairs independently, mirroring the null-when-absent convention used by
// every other JSON-optional block in this codebase (see
// ExceptionJsonExporterTests.cs's own "timeline is null" tests).
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class TimeBreakdownBuilderTests
{
    private static byte[] MakeMethodDCStartVerbosePayload(long methodId, long moduleId, long methodStartAddress, int methodSize, string methodName)
    {
        return new PayloadBuilder()
            .WriteAddress(methodId, 8)
            .WriteAddress(moduleId, 8)
            .WriteAddress(methodStartAddress, 8)
            .WriteInt32(methodSize)
            .WriteInt32(0x06000001)
            .WriteInt32(0)
            .WriteUnicodeString("")
            .WriteUnicodeString(methodName)
            .WriteUnicodeString("sig")
            .ToArray();
    }

    private static EventRecord MakeRundownEvent(long methodId, long startAddress, int size, string name)
    {
        byte[] payload = MakeMethodDCStartVerbosePayload(methodId, moduleId: 2, startAddress, size, name);

        return new EventRecord("Microsoft-Windows-DotNETRuntimeRundown", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stack: Array.Empty<long>(), fields: null, payload, payloadOffset: 0, payload.Length);
    }

    // Resolves 0x1000 -> "System.Threading.Monitor.Enter" (a known idle-wait
    // leaf per CpuIdleWaitClassifier) and 0x2000 -> "MyApp.DoWork" (not).
    private static MethodSymbolTable MakeSymbolTable()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRundownEvent(methodId: 1, startAddress: 0x1000, size: 0x100, name: "System.Threading.Monitor.Enter"),
            MakeRundownEvent(methodId: 2, startAddress: 0x2000, size: 0x100, name: "MyApp.DoWork"),
        };

        return MethodSymbolTable.Build(events, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
    }

    private static GcEvent MakeGcEvent(double pauseDurationMSec)
    {
        GcEvent gcEvent = new GcEvent();
        gcEvent.PauseDurationMSec = pauseDurationMSec;
        return gcEvent;
    }

    [Fact]
    public void Build_ZeroCaptureDuration_HasCaptureDurationFalse()
    {
        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), new List<ContentionEvent>(), new List<SampleEvent>(), MakeSymbolTable(), captureDurationMSec: 0);

        Assert.False(breakdown.HasCaptureDuration);
        Assert.Equal(0, breakdown.GcPercent);
        Assert.Equal(0, breakdown.ContentionPercent);
    }

    [Fact]
    public void Build_NoSampleEvents_HasCpuSampleBreakdownFalse()
    {
        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), new List<ContentionEvent>(), new List<SampleEvent>(), MakeSymbolTable(), captureDurationMSec: 1000.0);

        Assert.False(breakdown.HasCpuSampleBreakdown);
        Assert.Equal(0, breakdown.IdlePercent);
        Assert.Equal(0, breakdown.CpuBoundPercent);
    }

    [Fact]
    public void Build_GcAndContentionDurations_ComputedAsExactPercentOfCaptureDuration()
    {
        List<GcEvent> gcEvents = new List<GcEvent>
        {
            MakeGcEvent(100.0),
            MakeGcEvent(150.0),
        };

        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            new ContentionEvent(relativeMSec: 10.0, durationMSec: 30.0, ClrContentionFlags.Managed, threadId: 1, Array.Empty<long>()),
            new ContentionEvent(relativeMSec: 20.0, durationMSec: 20.0, ClrContentionFlags.Managed, threadId: 2, Array.Empty<long>()),
        };

        // captureDurationMSec=1000: gc=(100+150)/1000*100=25%, contention=(30+20)/1000*100=5%.
        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(gcEvents, contentionEvents, new List<SampleEvent>(), MakeSymbolTable(), captureDurationMSec: 1000.0);

        Assert.True(breakdown.HasCaptureDuration);
        Assert.Equal(25.0, breakdown.GcPercent, 3);
        Assert.Equal(5.0, breakdown.ContentionPercent, 3);
    }

    [Fact]
    public void Build_ContentionWaitExceedsCaptureDuration_NotClamped()
    {
        // Two threads each blocked for the whole capture - summed wait time
        // is double the wall-clock span, and that's real information (see
        // the class's own header comment), not a bug to clamp away.
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            new ContentionEvent(relativeMSec: 0.0, durationMSec: 100.0, ClrContentionFlags.Managed, threadId: 1, Array.Empty<long>()),
            new ContentionEvent(relativeMSec: 0.0, durationMSec: 100.0, ClrContentionFlags.Managed, threadId: 2, Array.Empty<long>()),
        };

        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), contentionEvents, new List<SampleEvent>(), MakeSymbolTable(), captureDurationMSec: 100.0);

        Assert.Equal(200.0, breakdown.ContentionPercent, 3);
    }

    [Fact]
    public void Build_SampleEvents_IdleAndCpuBoundSumToOneHundredAndIgnoreCaptureDuration()
    {
        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(relativeMSec: 0.0, threadId: 1, stack: new long[] { 0x1000 }),  // idle (Monitor.Enter)
            new SampleEvent(relativeMSec: 1.0, threadId: 1, stack: new long[] { 0x1000 }),  // idle
            new SampleEvent(relativeMSec: 2.0, threadId: 1, stack: new long[] { 0x2000 }),  // cpu-bound (MyApp.DoWork)
            new SampleEvent(relativeMSec: 3.0, threadId: 1, stack: new long[] { 0x2000 }),  // cpu-bound
        };

        // captureDurationMSec deliberately tiny/unrelated - idle/cpuBound
        // must not be derived from it at all.
        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), new List<ContentionEvent>(), sampleEvents, MakeSymbolTable(), captureDurationMSec: 1.0);

        Assert.True(breakdown.HasCpuSampleBreakdown);
        Assert.Equal(50.0, breakdown.IdlePercent, 3);
        Assert.Equal(50.0, breakdown.CpuBoundPercent, 3);
        Assert.Equal(100.0, breakdown.IdlePercent + breakdown.CpuBoundPercent, 3);
    }

    [Fact]
    public void Build_SampleWithEmptyStack_NotCountedAsIdle()
    {
        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(relativeMSec: 0.0, threadId: 1, stack: Array.Empty<long>()),
            new SampleEvent(relativeMSec: 1.0, threadId: 1, stack: new long[] { 0x2000 }),
        };

        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), new List<ContentionEvent>(), sampleEvents, MakeSymbolTable(), captureDurationMSec: 1.0);

        Assert.Equal(0.0, breakdown.IdlePercent, 3);
        Assert.Equal(100.0, breakdown.CpuBoundPercent, 3);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
