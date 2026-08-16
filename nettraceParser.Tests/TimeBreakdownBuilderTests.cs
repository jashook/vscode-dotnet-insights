////////////////////////////////////////////////////////////////////////////////
// Module: TimeBreakdownBuilderTests.cs
//
// Notes:
// Covers the non-obvious rules TimeBreakdownBuilder.Build needs to keep:
// GcPercent/ContentionPercent are computed from real measured durations
// against captureDurationMSec and are both BOUNDED by 100% - contention gets
// there by unioning overlapping blocked windows rather than summing them,
// with the concurrency that summing encoded reported separately as
// AverageThreadsBlocked (see the class's own header comment), while
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
    // One stack table for the whole class so the static Make* helpers
    // below can register stacks too - see TestStacks.cs.
    private static readonly TestStacks stacks = new TestStacks();

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

        return new EventRecord("Microsoft-Windows-DotNETRuntimeRundown", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stackIndex: StackTable.EmptyStackIndex, fields: null, payload, payloadOffset: 0, payload.Length);
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
        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), new List<ContentionEvent>(), new List<SampleEvent>(), stacks.Table, MakeSymbolTable(), captureDurationMSec: 0);

        Assert.False(breakdown.HasCaptureDuration);
        Assert.Equal(0, breakdown.GcPercent);
        Assert.Equal(0, breakdown.ContentionPercent);
    }

    [Fact]
    public void Build_NoSampleEvents_HasCpuSampleBreakdownFalse()
    {
        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), new List<ContentionEvent>(), new List<SampleEvent>(), stacks.Table, MakeSymbolTable(), captureDurationMSec: 1000.0);

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
            new ContentionEvent(relativeMSec: 10.0, durationMSec: 30.0, ClrContentionFlags.Managed, threadId: 1, StackTable.EmptyStackIndex),
            new ContentionEvent(relativeMSec: 20.0, durationMSec: 20.0, ClrContentionFlags.Managed, threadId: 2, StackTable.EmptyStackIndex),
        };

        // captureDurationMSec=1000: gc=(100+150)/1000*100=25%.
        // Contention windows are [10,40) and [20,40) - OVERLAPPING, so the
        // union is [10,40) = 30ms => 3%, not the summed 50ms => 5%. Summed
        // wait is still 50ms, i.e. 0.05 threads blocked on average.
        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(gcEvents, contentionEvents, new List<SampleEvent>(), stacks.Table, MakeSymbolTable(), captureDurationMSec: 1000.0);

        Assert.True(breakdown.HasCaptureDuration);
        Assert.Equal(25.0, breakdown.GcPercent, 3);
        Assert.Equal(3.0, breakdown.ContentionPercent, 3);
        Assert.Equal(0.05, breakdown.AverageThreadsBlocked, 3);
    }

    // The bug this replaced: summed wait divided by wall-clock time was being
    // rendered as a percentage on a tile beside GC/Idle/CPU-Bound, and read
    // "Contending Locks 426.1%" on a real 3.01GB capture. Concurrently blocked
    // threads make that sum exceed the capture's own span, so it was never a
    // percentage of anything.
    [Fact]
    public void Build_ConcurrentlyBlockedThreads_ContentionPercentStaysBounded()
    {
        // Four threads all blocked for the ENTIRE capture. Summed wait is 4x
        // the wall-clock span, but only 100% of the clock had a blocked
        // thread on it.
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            new ContentionEvent(relativeMSec: 0.0, durationMSec: 100.0, ClrContentionFlags.Managed, threadId: 1, StackTable.EmptyStackIndex),
            new ContentionEvent(relativeMSec: 0.0, durationMSec: 100.0, ClrContentionFlags.Managed, threadId: 2, StackTable.EmptyStackIndex),
            new ContentionEvent(relativeMSec: 0.0, durationMSec: 100.0, ClrContentionFlags.Managed, threadId: 3, StackTable.EmptyStackIndex),
            new ContentionEvent(relativeMSec: 0.0, durationMSec: 100.0, ClrContentionFlags.Managed, threadId: 4, StackTable.EmptyStackIndex),
        };

        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), contentionEvents, new List<SampleEvent>(), stacks.Table, MakeSymbolTable(), captureDurationMSec: 100.0);

        Assert.Equal(100.0, breakdown.ContentionPercent, 3);

        // The concurrency the old percentage really encoded, kept but named
        // for what it is.
        Assert.Equal(4.0, breakdown.AverageThreadsBlocked, 3);
    }

    [Fact]
    public void Build_DisjointBlockedWindows_AreSummedNotCollapsed()
    {
        // Non-overlapping windows must still add up - a union that collapsed
        // everything to one span would pass the concurrency test above while
        // being badly wrong here.
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            new ContentionEvent(relativeMSec: 0.0, durationMSec: 10.0, ClrContentionFlags.Managed, threadId: 1, StackTable.EmptyStackIndex),
            new ContentionEvent(relativeMSec: 50.0, durationMSec: 10.0, ClrContentionFlags.Managed, threadId: 2, StackTable.EmptyStackIndex),
        };

        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), contentionEvents, new List<SampleEvent>(), stacks.Table, MakeSymbolTable(), captureDurationMSec: 100.0);

        Assert.Equal(20.0, breakdown.ContentionPercent, 3);
    }

    [Fact]
    public void Build_UnsortedContentionEvents_AreUnionedCorrectly()
    {
        // Events arrive in stream order, which is only approximately time
        // order across blocks, so the union must not assume sortedness.
        // [0,10) overlaps [5,20) -> [0,20) = 20ms; [40,50) is disjoint -> 10ms;
        // 30ms total.
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            new ContentionEvent(relativeMSec: 40.0, durationMSec: 10.0, ClrContentionFlags.Managed, threadId: 3, StackTable.EmptyStackIndex),
            new ContentionEvent(relativeMSec: 5.0, durationMSec: 15.0, ClrContentionFlags.Managed, threadId: 2, StackTable.EmptyStackIndex),
            new ContentionEvent(relativeMSec: 0.0, durationMSec: 10.0, ClrContentionFlags.Managed, threadId: 1, StackTable.EmptyStackIndex),
        };

        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), contentionEvents, new List<SampleEvent>(), stacks.Table, MakeSymbolTable(), captureDurationMSec: 100.0);

        Assert.Equal(30.0, breakdown.ContentionPercent, 3);
    }

    // A window fully inside another must not extend the merged span - the
    // classic interval-merge mistake.
    [Fact]
    public void Build_FullyContainedWindow_DoesNotExtendTheMergedSpan()
    {
        List<ContentionEvent> contentionEvents = new List<ContentionEvent>
        {
            new ContentionEvent(relativeMSec: 0.0, durationMSec: 50.0, ClrContentionFlags.Managed, threadId: 1, StackTable.EmptyStackIndex),
            new ContentionEvent(relativeMSec: 10.0, durationMSec: 5.0, ClrContentionFlags.Managed, threadId: 2, StackTable.EmptyStackIndex),
        };

        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), contentionEvents, new List<SampleEvent>(), stacks.Table, MakeSymbolTable(), captureDurationMSec: 100.0);

        Assert.Equal(50.0, breakdown.ContentionPercent, 3);
    }

    [Fact]
    public void Build_SampleEvents_IdleAndCpuBoundSumToOneHundredAndIgnoreCaptureDuration()
    {
        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(relativeMSec: 0.0, threadId: 1, stackIndex: stacks.Index(0x1000)),  // idle (Monitor.Enter)
            new SampleEvent(relativeMSec: 1.0, threadId: 1, stackIndex: stacks.Index(0x1000)),  // idle
            new SampleEvent(relativeMSec: 2.0, threadId: 1, stackIndex: stacks.Index(0x2000)),  // cpu-bound (MyApp.DoWork)
            new SampleEvent(relativeMSec: 3.0, threadId: 1, stackIndex: stacks.Index(0x2000)),  // cpu-bound
        };

        // captureDurationMSec deliberately tiny/unrelated - idle/cpuBound
        // must not be derived from it at all.
        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), new List<ContentionEvent>(), sampleEvents, stacks.Table, MakeSymbolTable(), captureDurationMSec: 1.0);

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
            new SampleEvent(relativeMSec: 0.0, threadId: 1, stackIndex: StackTable.EmptyStackIndex),
            new SampleEvent(relativeMSec: 1.0, threadId: 1, stackIndex: stacks.Index(0x2000)),
        };

        TimeBreakdown breakdown = TimeBreakdownBuilder.Build(new List<GcEvent>(), new List<ContentionEvent>(), sampleEvents, stacks.Table, MakeSymbolTable(), captureDurationMSec: 1.0);

        Assert.Equal(0.0, breakdown.IdlePercent, 3);
        Assert.Equal(100.0, breakdown.CpuBoundPercent, 3);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
