////////////////////////////////////////////////////////////////////////////////
// Module: ThreadingAdjustmentDrillDownTests.cs
//
// Notes:
// Pins the two rules in ThreadingJsonExporter that are judgement calls rather
// than transcriptions of the event payloads, and would therefore regress
// silently:
//
//   1. A thread contributes exactly ONE stack per adjustment - the sample
//      NEAREST that adjustment's own timestamp. A thread produces several
//      samples inside the window, and taking the first (or the last, or all of
//      them) would describe the window rather than the instant the decision
//      was made.
//   2. Whether the POOL created a thread is decided by the creation STACK, not
//      by the adjustment counters. An earlier version required the nearest
//      adjustment to have raised NewWorkerThreadCount; real data showed that
//      counter oscillating +/-20 as hill climbing's target, which labelled
//      threads whose own stack reads PortableThreadPool.CreateWorkerThread as
//      "not pool-driven". The reason is then taken from the nearest preceding
//      decision and reported as context, with its own elapsed time.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json;

using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Rundown;
using DotnetInsights.NetTrace.Threading;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ThreadingAdjustmentDrillDownTests
{
    // "Parked" here must match ThreadingJsonExporter's own prefix list - a
    // worker sitting in LowLevelLifoSemaphore is the pool's idle state.
    private const string ParkedFrameName = "System.Threading.LowLevelLifoSemaphore.WaitForSignal";

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

    private static MethodSymbolTable MakeSymbolTable()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRundownEvent(methodId: 1, startAddress: 0x1000, size: 0x100, name: "Blocking.Read"),
            MakeRundownEvent(methodId: 2, startAddress: 0x2000, size: 0x100, name: "Caller.Run"),
            MakeRundownEvent(methodId: 3, startAddress: 0x3000, size: 0x100, name: "Other.Work"),
            MakeRundownEvent(methodId: 4, startAddress: 0x4000, size: 0x100, name: ParkedFrameName),
            MakeRundownEvent(methodId: 5, startAddress: 0x5000, size: 0x100, name: "System.Threading.PortableThreadPool+WorkerThread.CreateWorkerThread"),
        };

        return MethodSymbolTable.Build(events, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
    }

    private static ThreadPoolAdjustmentRecord MakeAdjustment(double relativeMSec, int newWorkerThreadCount, int reason)
    {
        ThreadPoolAdjustmentRecord adjustment = new ThreadPoolAdjustmentRecord();
        adjustment.RelativeMSec = relativeMSec;
        adjustment.NewWorkerThreadCount = newWorkerThreadCount;
        adjustment.Reason = reason;
        adjustment.AverageThroughput = 1.0;
        return adjustment;
    }

    // isPoolWorker: true builds a stack running through PortableThreadPool
    // (what a real worker creation looks like); false builds an application
    // thread start.
    private static StackedThreadingEvent MakeCreation(double relativeMSec, long threadId, bool isPoolWorker = true)
    {
        StackedThreadingEvent creation = new StackedThreadingEvent();
        creation.RelativeMSec = relativeMSec;
        creation.ThreadId = threadId;
        creation.ObjectId = threadId;
        creation.Stack = isPoolWorker ? new long[] { 0x5010, 0x2010 } : new long[] { 0x2010 };
        return creation;
    }

    private static ThreadingSummary MakeSummary()
    {
        ThreadingSummary summary = new ThreadingSummary();
        summary.HasThreadPoolData = true;
        summary.BucketCount = 1;
        summary.BucketDurationMSec = 1;
        summary.MinActiveByBucket = new int[1];
        summary.MaxActiveByBucket = new int[1];
        summary.AverageActiveByBucket = new double[1];
        summary.ThroughputByBucket = new double[1];
        return summary;
    }

    private static JsonDocument WriteAndParse(ThreadingSummary summary, List<SampleEvent> sampleEvents)
    {
        List<string> methodNames = new List<string>();
        Dictionary<string, int> methodNameIndexByName = new Dictionary<string, int>();

        using System.IO.MemoryStream stream = new System.IO.MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            ThreadingJsonExporter.Write(writer, summary, sampleEvents, MakeSymbolTable(), methodNames, methodNameIndexByName);
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    private static string[] MethodNamesFor(ThreadingSummary summary, List<SampleEvent> sampleEvents, out JsonDocument document)
    {
        List<string> methodNames = new List<string>();
        Dictionary<string, int> methodNameIndexByName = new Dictionary<string, int>();

        using System.IO.MemoryStream stream = new System.IO.MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            ThreadingJsonExporter.Write(writer, summary, sampleEvents, MakeSymbolTable(), methodNames, methodNameIndexByName);
        }

        document = JsonDocument.Parse(stream.ToArray());
        return methodNames.ToArray();
    }

    // Two threads in the same stack collapse into one group carrying both
    // thread ids - the count is the finding, not a repeated stack listing.
    [Fact]
    public void Write_GroupsThreadsSharingAStackIntoOneEntry()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.CooperativeBlocking));

        long[] blockingStack = new long[] { 0x1010, 0x2010 };

        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(99.0, threadId: 11, blockingStack),
            new SampleEvent(99.5, threadId: 12, blockingStack),
            new SampleEvent(100.5, threadId: 13, new long[] { 0x3010 }),
        };

        using JsonDocument document = WriteAndParse(summary, sampleEvents);
        JsonElement snapshot = document.RootElement.GetProperty("adjustments")[0].GetProperty("threadSnapshot");

        Assert.Equal(3, snapshot.GetProperty("threadsSampled").GetInt32());
        Assert.Equal(2, snapshot.GetProperty("stackGroupCount").GetInt32());

        JsonElement stacks = snapshot.GetProperty("stacks");
        JsonElement sharedGroup = stacks[0];

        Assert.Equal(2, sharedGroup.GetProperty("threadCount").GetInt32());
        Assert.Equal(2, sharedGroup.GetProperty("threadIds").GetArrayLength());
        Assert.Equal(2, sharedGroup.GetProperty("frames").GetArrayLength());
    }

    // The rule this file exists for: thread 11 samples twice in the window,
    // and the stack reported must be the one from 100.4 (0.4ms away), not the
    // one from 80.0 (20ms away) that a first-wins pass would keep.
    [Fact]
    public void Write_KeepsTheSampleNearestTheAdjustmentForEachThread()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.CooperativeBlocking));

        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(80.0, threadId: 11, new long[] { 0x3010 }),   // Other.Work, far
            new SampleEvent(100.4, threadId: 11, new long[] { 0x1010 }),  // Blocking.Read, near
        };

        string[] methodNames = MethodNamesFor(summary, sampleEvents, out JsonDocument document);

        using (document)
        {
            JsonElement snapshot = document.RootElement.GetProperty("adjustments")[0].GetProperty("threadSnapshot");

            Assert.Equal(1, snapshot.GetProperty("threadsSampled").GetInt32());
            Assert.Equal(1, snapshot.GetProperty("stackGroupCount").GetInt32());

            JsonElement frames = snapshot.GetProperty("stacks")[0].GetProperty("frames");
            Assert.Equal(1, frames.GetArrayLength());
            Assert.Equal("Blocking.Read", methodNames[frames[0].GetInt32()]);
        }
    }

    // Parked workers are the pool's idle state: flagged, counted, and sorted
    // below running threads even when they outnumber them.
    [Fact]
    public void Write_SortsParkedWorkersBelowRunningThreadsAndCountsThem()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.CooperativeBlocking));

        long[] parkedStack = new long[] { 0x4010 };

        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(100.0, threadId: 11, parkedStack),
            new SampleEvent(100.0, threadId: 12, parkedStack),
            new SampleEvent(100.0, threadId: 13, parkedStack),
            new SampleEvent(100.0, threadId: 14, new long[] { 0x1010 }),
        };

        using JsonDocument document = WriteAndParse(summary, sampleEvents);
        JsonElement snapshot = document.RootElement.GetProperty("adjustments")[0].GetProperty("threadSnapshot");

        Assert.Equal(3, snapshot.GetProperty("parkedThreadCount").GetInt32());

        JsonElement stacks = snapshot.GetProperty("stacks");
        // The single running thread outranks the three parked ones.
        Assert.False(stacks[0].GetProperty("isParkedWorker").GetBoolean());
        Assert.Equal(1, stacks[0].GetProperty("threadCount").GetInt32());
        Assert.True(stacks[1].GetProperty("isParkedWorker").GetBoolean());
        Assert.Equal(3, stacks[1].GetProperty("threadCount").GetInt32());
    }

    // Samples outside the window contribute nothing, and an adjustment with no
    // samples reports a null snapshot rather than an empty one - "nothing was
    // sampled here" is not "every thread was idle".
    [Fact]
    public void Write_AdjustmentWithNoSamplesInWindowGetsNullSnapshot()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.CooperativeBlocking));

        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(1000.0, threadId: 11, new long[] { 0x1010 }),
        };

        using JsonDocument document = WriteAndParse(summary, sampleEvents);

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("adjustments")[0].GetProperty("threadSnapshot").ValueKind);
    }

    // workerThreadDelta is the change this decision made, which is what says
    // whether the pool grew or shrank - the payload only carries the resulting
    // count.
    [Fact]
    public void Write_ReportsWorkerThreadDeltaBetweenConsecutiveAdjustments()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.ClimbingMove));
        summary.Adjustments.Add(MakeAdjustment(200.0, newWorkerThreadCount: 7, ThreadAdjustmentReason.CooperativeBlocking));
        summary.Adjustments.Add(MakeAdjustment(300.0, newWorkerThreadCount: 6, ThreadAdjustmentReason.ThreadTimedOut));

        using JsonDocument document = WriteAndParse(summary, new List<SampleEvent>());
        JsonElement adjustments = document.RootElement.GetProperty("adjustments");

        Assert.Equal(0, adjustments[0].GetProperty("workerThreadDelta").GetInt32());
        Assert.Equal(2, adjustments[1].GetProperty("workerThreadDelta").GetInt32());
        Assert.Equal(-1, adjustments[2].GetProperty("workerThreadDelta").GetInt32());
    }

    // The reason comes from the NEAREST preceding decision, including one that
    // lowered the target count. This is the case the old "must have raised the
    // count" rule got wrong: the adjustment at 250ms lowered the target, yet a
    // worker creation 10ms later is entirely normal (measured on a real
    // capture: a creation 49ms after a -20 adjustment).
    [Fact]
    public void Write_AttributesCreationToTheNearestPrecedingAdjustmentEvenWhenItLoweredTheTarget()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.ClimbingMove));
        summary.Adjustments.Add(MakeAdjustment(200.0, newWorkerThreadCount: 7, ThreadAdjustmentReason.CooperativeBlocking));
        summary.Adjustments.Add(MakeAdjustment(250.0, newWorkerThreadCount: 6, ThreadAdjustmentReason.ThreadTimedOut));
        summary.ThreadCreations.Add(MakeCreation(260.0, threadId: 42));

        using JsonDocument document = WriteAndParse(summary, new List<SampleEvent>());
        JsonElement creation = document.RootElement.GetProperty("threadCreations")[0];

        Assert.Equal(2, creation.GetProperty("causeAdjustmentIndex").GetInt32());
        Assert.Equal("Thread timed out", creation.GetProperty("causeReasonName").GetString());
        Assert.Equal(10.0, creation.GetProperty("causeDelayMSec").GetDouble(), 3);
    }

    // Pool ownership is read off the stack, so it holds even when no
    // adjustment is anywhere near - which is exactly the combination the old
    // rule mislabelled as "not pool-driven".
    [Fact]
    public void Write_MarksAPoolWorkerFromItsStackEvenWithNoNearbyAdjustment()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.CooperativeBlocking));
        summary.ThreadCreations.Add(MakeCreation(50000.0, threadId: 42, isPoolWorker: true));

        using JsonDocument document = WriteAndParse(summary, new List<SampleEvent>());
        JsonElement creation = document.RootElement.GetProperty("threadCreations")[0];

        Assert.True(creation.GetProperty("isPoolWorker").GetBoolean());
        Assert.Equal(-1, creation.GetProperty("causeAdjustmentIndex").GetInt32());
        Assert.False(creation.TryGetProperty("causeReasonName", out JsonElement _));
    }

    // A thread whose stack has no thread-pool frame was started by application
    // or library code - a normal, common case, not missing data.
    [Fact]
    public void Write_MarksAThreadWithNoPoolFrameAsNotAPoolWorker()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.CooperativeBlocking));
        summary.ThreadCreations.Add(MakeCreation(110.0, threadId: 42, isPoolWorker: false));

        using JsonDocument document = WriteAndParse(summary, new List<SampleEvent>());

        Assert.False(document.RootElement.GetProperty("threadCreations")[0].GetProperty("isPoolWorker").GetBoolean());
    }

    // Beyond the window the row reports no nearby decision rather than being
    // pinned on a stale one from seconds earlier.
    [Fact]
    public void Write_LeavesCreationUnattributedBeyondTheAttributionWindow()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.CooperativeBlocking));
        summary.ThreadCreations.Add(MakeCreation(5000.0, threadId: 42));

        using JsonDocument document = WriteAndParse(summary, new List<SampleEvent>());
        JsonElement creation = document.RootElement.GetProperty("threadCreations")[0];

        Assert.Equal(-1, creation.GetProperty("causeAdjustmentIndex").GetInt32());
        Assert.False(creation.TryGetProperty("causeReasonName", out JsonElement _));
    }

    // Lock creations are not caused by pool decisions, so they must carry no
    // attribution at all - a nearby adjustment there would be coincidence.
    [Fact]
    public void Write_DoesNotAttributeLockCreationsToAdjustments()
    {
        ThreadingSummary summary = MakeSummary();
        summary.Adjustments.Add(MakeAdjustment(100.0, newWorkerThreadCount: 5, ThreadAdjustmentReason.CooperativeBlocking));
        summary.LockCreations.Add(MakeCreation(110.0, threadId: 42));

        using JsonDocument document = WriteAndParse(summary, new List<SampleEvent>());
        JsonElement lockCreation = document.RootElement.GetProperty("lockCreations")[0];

        Assert.False(lockCreation.TryGetProperty("causeAdjustmentIndex", out JsonElement _));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
