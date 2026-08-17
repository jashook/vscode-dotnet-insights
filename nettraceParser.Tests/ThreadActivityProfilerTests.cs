////////////////////////////////////////////////////////////////////////////////
// Module: ThreadActivityProfilerTests.cs
//
// Notes:
// Covers ThreadActivityProfiler's classification, which exists to answer one
// question - can the reader skip this thread - and is therefore tested the way
// that question fails in practice rather than by asserting each field it
// computes.
//
// The shapes below are lifted from real captures, not invented. Four are
// regression tests for classifiers that looked right and were wrong on real
// data:
//
//   - a gRPC/Kafka style native poll loop, whose managed leaf frame reads like
//     ordinary running code while every sample is External. The leaf-frame
//     wait classifier scored six of these at "100% running".
//   - the runtime's own timer thread, which parks in WaitHandle.WaitOneNoCheck
//     rather than the pool's semaphore while carrying TimerQueue on its stack,
//     and so read as "a pool worker held up outside the pool's own park" - the
//     loudest label this view has, on a thread doing exactly its job.
//   - a dedicated BlockingCollection drain worker, whose loop has three park
//     sites rather than one, so a top-STACK concentration test called it
//     Blocked along with ~45 siblings - and whose 157 contention events turned
//     out to total 8.3ms of a 300-second life.
//   - four thread-POOL workers running a synchronous Kafka consume inside an
//     ExecuteAsync, parked for an entire capture. Behaviourally identical to
//     the benign dedicated consumer above; the opposite verdict, because they
//     are standing on workers the pool can never reclaim. The pair is the
//     whole point: "is it parked" and "can I ignore it" are different
//     questions, and only the second one is what the view is asked.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Rundown;
using DotnetInsights.NetTrace.Threading;

using Xunit;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ThreadActivityProfilerTests
{
    private static readonly TestStacks stacks = new TestStacks();

    private const string PoolParkFrameName = "System.Threading.LowLevelLifoSemaphore.WaitForSignal";
    private const string PoolWorkerRootFrameName = "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart";
    private const string GateThreadRootFrameName = "System.Threading.PortableThreadPool+GateThread.GateThreadStart";
    private const string TimerThreadRootFrameName = "System.Threading.TimerQueue.TimerThread";
    private const string WaitHandleFrameName = "System.Threading.WaitHandle.WaitOneNoCheck";
    private const string NativePollFrameName = "Grpc.Core.Internal.GrpcThreadPool.RunHandlerLoop";
    private const string UserCodeFrameName = "Contoso.Service.RequestHandler.Handle";
    private const string ThreadStartFrameName = "System.Threading.Thread.StartCallback";

    // Sampling is one sample per thread per millisecond, matching EventPipe's
    // SampleProfiler default, so a sample count reads directly as milliseconds
    // and the spans below are legible as durations.
    private const double SampleIntervalMSec = 1.0;

    private static byte[] MakeMethodDCStartVerbosePayload(long methodId, long methodStartAddress, int methodSize, string methodName)
    {
        return new PayloadBuilder()
            .WriteAddress(methodId, 8)
            .WriteAddress(2, 8)
            .WriteAddress(methodStartAddress, 8)
            .WriteInt32(methodSize)
            .WriteInt32(0x06000001)
            .WriteInt32(0)
            .WriteUnicodeString("")
            .WriteUnicodeString(methodName)
            .WriteUnicodeString("sig")
            .ToArray();
    }

    private static EventRecord MakeRundownEvent(long methodId, long startAddress, string name)
    {
        byte[] payload = MakeMethodDCStartVerbosePayload(methodId, startAddress, 0x100, name);

        return new EventRecord("Microsoft-Windows-DotNETRuntimeRundown", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stackIndex: StackTable.EmptyStackIndex, fields: null, payload, payloadOffset: 0, payload.Length);
    }

    private static MethodSymbolTable MakeSymbolTable()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRundownEvent(methodId: 1, startAddress: 0x1000, name: PoolParkFrameName),
            MakeRundownEvent(methodId: 2, startAddress: 0x2000, name: PoolWorkerRootFrameName),
            MakeRundownEvent(methodId: 3, startAddress: 0x3000, name: GateThreadRootFrameName),
            MakeRundownEvent(methodId: 4, startAddress: 0x4000, name: TimerThreadRootFrameName),
            MakeRundownEvent(methodId: 5, startAddress: 0x5000, name: WaitHandleFrameName),
            MakeRundownEvent(methodId: 6, startAddress: 0x6000, name: NativePollFrameName),
            MakeRundownEvent(methodId: 7, startAddress: 0x7000, name: UserCodeFrameName),
            MakeRundownEvent(methodId: 8, startAddress: 0x8000, name: ThreadStartFrameName),
        };

        return MethodSymbolTable.Build(events, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
    }

    private static ContentionEvent MakeContention(double relativeMSec, double durationMSec, long threadId, long ownerThreadId = 0)
    {
        return new ContentionEvent(relativeMSec, durationMSec, ClrContentionFlags.Managed, threadId, StackTable.EmptyStackIndex, lockId: 1, associatedObjectId: 0, ownerThreadId);
    }

    // Appends `sampleCount` consecutive samples for one thread, all in the same
    // stack and all of the same type. `startMSec` lets several threads be laid
    // over the same window.
    private static void AddSamples(List<SampleEvent> sampleEvents, long threadId, int stackIndex, ThreadSampleType sampleType, int sampleCount, double startMSec = 0)
    {
        for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex)
        {
            sampleEvents.Add(new SampleEvent(startMSec + (sampleIndex * SampleIntervalMSec), threadId, stackIndex, sampleType));
        }
    }

    private static ThreadActivityProfileSet Build(List<SampleEvent> sampleEvents, List<ContentionEvent> contentionEvents = null)
    {
        // Time order is a documented precondition of the single-pass
        // projectors this consumes; sorting here rather than requiring every
        // test to interleave by hand.
        sampleEvents.Sort((SampleEvent left, SampleEvent right) => left.RelativeMSec.CompareTo(right.RelativeMSec));

        return ThreadActivityProfiler.Build(sampleEvents, contentionEvents ?? new List<ContentionEvent>(), stacks.Table, MakeSymbolTable());
    }

    // The regression test for the classifier this whole file exists because
    // of. A gRPC completion thread's leaf frame is ordinary managed code, so
    // no list of blocking-primitive names can ever recognise it - but every
    // one of its samples is External, and it never leaves that stack.
    [Fact]
    public void Build_ClassifiesNativePollLoopAsParked_EvenThoughItsLeafFrameIsNotAKnownWait()
    {
        int pollStackIndex = stacks.Index(0x6010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 103, pollStackIndex, ThreadSampleType.External, sampleCount: 30000);

        ThreadActivityProfile profile = Build(sampleEvents).TryGet(103);

        Assert.Equal(ThreadActivityRole.ParkedThread, profile.Role);
        Assert.True(profile.IsBenignlyParked);
        // The leaf is not a known wait primitive, and that is the point: the
        // classification does not depend on recognising it.
        Assert.Equal(0, profile.WaitSampleCount);
        Assert.Equal(0, profile.ManagedSampleCount);
    }

    // The same stack, the same leaf, the same External-dominant profile - but
    // this thread actually works. This is the pair that proves the filter has
    // to be per THREAD: on a real capture the identical
    // Confluent.Kafka.Consumer.Consume frame appeared in both the actionable
    // and the excluded stall table, from different threads.
    [Fact]
    public void Build_DoesNotCallANativePollLoopParked_WhenItRunsManagedCodeBetweenPolls()
    {
        int pollStackIndex = stacks.Index(0x6010, 0x8010);
        int userStackIndex = stacks.Index(0x7010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();

        // 10% of its samples are real managed work, interleaved rather than
        // appended, so the dominant-stack share falls too.
        for (int blockIndex = 0; blockIndex < 3000; ++blockIndex)
        {
            AddSamples(sampleEvents, threadId: 119, pollStackIndex, ThreadSampleType.External, sampleCount: 9, startMSec: blockIndex * 10.0);
            AddSamples(sampleEvents, threadId: 119, userStackIndex, ThreadSampleType.Managed, sampleCount: 1, startMSec: (blockIndex * 10.0) + 9);
        }

        ThreadActivityProfile profile = Build(sampleEvents).TryGet(119);

        Assert.Equal(ThreadActivityRole.ActiveThread, profile.Role);
        Assert.False(profile.IsBenignlyParked);
    }

    // A pool worker parked in the pool's own semaphore is idle capacity, and
    // is called that rather than lumped in with application threads - a
    // snapshot full of these means the pool had spare workers, which is a real
    // answer to "why did it grow".
    [Fact]
    public void Build_ClassifiesAWorkerParkedInThePoolSemaphoreAsIdlePoolWorker()
    {
        int parkStackIndex = stacks.Index(0x1010, 0x2010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 2143, parkStackIndex, ThreadSampleType.External, sampleCount: 30000);

        ThreadActivityProfile profile = Build(sampleEvents).TryGet(2143);

        Assert.Equal(ThreadActivityRole.IdlePoolWorker, profile.Role);
        Assert.True(profile.IsBenignlyParked);
        Assert.True(profile.IsPoolWorker);
    }

    // A pool worker stuck somewhere OTHER than the pool's park is the
    // starvation case: the pool cannot reuse it, so hill climbing injects more
    // threads. It must never be filtered out as benign.
    [Fact]
    public void Build_ClassifiesAWorkerBlockedOutsideThePoolParkAsBlockedPoolWorker()
    {
        int parkStackIndex = stacks.Index(0x1010, 0x2010, 0x8010);
        int blockedStackIndex = stacks.Index(0x5010, 0x7010, 0x2010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();

        // Mostly waiting, but only a fifth of that waiting is the pool's own
        // park - the rest is a blocking call inside a work item.
        AddSamples(sampleEvents, threadId: 128853, blockedStackIndex, ThreadSampleType.External, sampleCount: 24000);
        AddSamples(sampleEvents, threadId: 128853, parkStackIndex, ThreadSampleType.External, sampleCount: 6000, startMSec: 24000);

        ThreadActivityProfileSet profileSet = Build(sampleEvents, new List<ContentionEvent>
        {
            MakeContention(relativeMSec: 500, durationMSec: 4909.5, threadId: 128853)
        });

        ThreadActivityProfile profile = profileSet.TryGet(128853);

        Assert.Equal(ThreadActivityRole.BlockedPoolWorker, profile.Role);
        Assert.False(profile.IsBenignlyParked);
        Assert.Equal(4909.5, profile.ContentionWaitMSec, precision: 1);
    }

    // Regression test for the false positive found on a real 1.4GB capture:
    // the timer thread carries TimerQueue on its stack (so it reads as a pool
    // worker), parks in WaitOne rather than the pool's semaphore (so it is not
    // an idle worker), and wakes on every timer tick to run real managed code
    // (so the parked test does not catch it either). Behaviour alone lands it
    // on BlockedPoolWorker; only its entry point says otherwise.
    [Fact]
    public void Build_ClassifiesTheRuntimeTimerThreadAsInfrastructure_NotAsABlockedPoolWorker()
    {
        int timerStackIndex = stacks.Index(0x5010, 0x4010, 0x8010);
        int timerWorkStackIndex = stacks.Index(0x7010, 0x4010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();

        for (int blockIndex = 0; blockIndex < 3000; ++blockIndex)
        {
            AddSamples(sampleEvents, threadId: 52, timerStackIndex, ThreadSampleType.External, sampleCount: 9, startMSec: blockIndex * 10.0);
            AddSamples(sampleEvents, threadId: 52, timerWorkStackIndex, ThreadSampleType.Managed, sampleCount: 1, startMSec: (blockIndex * 10.0) + 9);
        }

        ThreadActivityProfile profile = Build(sampleEvents).TryGet(52);

        Assert.Equal(ThreadActivityRole.RuntimeInfrastructureThread, profile.Role);
        Assert.True(profile.IsBenignlyParked);
    }

    [Fact]
    public void Build_ClassifiesTheGateThreadAsInfrastructure()
    {
        int gateStackIndex = stacks.Index(0x5010, 0x3010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 54, gateStackIndex, ThreadSampleType.External, sampleCount: 30000);

        Assert.Equal(ThreadActivityRole.RuntimeInfrastructureThread, Build(sampleEvents).TryGet(54).Role);
    }

    // The contention join, and the reason this view can be read alongside the
    // Contention view: a thread whose idleness that view materially accounts
    // for stays visible here no matter how parked its samples make it look.
    [Fact]
    public void Build_NeverCallsAThreadBenign_WhenContentionAccountsForRealTime()
    {
        int blockedStackIndex = stacks.Index(0x5010, 0x7010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 274, blockedStackIndex, ThreadSampleType.External, sampleCount: 30000);

        // Without the contention events this thread is textbook benign.
        Assert.True(Build(new List<SampleEvent>(sampleEvents)).TryGet(274).IsBenignlyParked);

        // 6 seconds blocked out of a 30-second life: the Contention view
        // explains a fifth of why this thread is sitting still.
        ThreadActivityProfile profile = Build(sampleEvents, new List<ContentionEvent>
        {
            MakeContention(relativeMSec: 100, durationMSec: 6000.0, threadId: 274)
        }).TryGet(274);

        Assert.False(profile.IsBenignlyParked);
        Assert.Equal(ThreadActivityRole.BlockedThread, profile.Role);
    }

    // The counterpart, and a regression test for a real misclassification: a
    // dedicated BlockingCollection drain worker on a live capture carried 157
    // contention events worth 8.3ms across a 300-second life. An "any event at
    // all" rule kept it, and ~45 sibling queue-drain threads, out of the benign
    // bucket on 0.0028% of their own time.
    [Fact]
    public void Build_StillCallsAThreadBenign_WhenItsContentionIsAmbientNoise()
    {
        int parkStackIndex = stacks.Index(0x5010, 0x7010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 274, parkStackIndex, ThreadSampleType.External, sampleCount: 30000);

        List<ContentionEvent> ambientContention = new List<ContentionEvent>();

        for (int contentionIndex = 0; contentionIndex < 157; ++contentionIndex)
        {
            ambientContention.Add(MakeContention(relativeMSec: contentionIndex * 100.0, durationMSec: 0.05, threadId: 274));
        }

        ThreadActivityProfile profile = Build(sampleEvents, ambientContention).TryGet(274);

        Assert.True(profile.IsBenignlyParked);
        Assert.Equal(157, profile.ContentionCount);
        // Reported in full even though it did not change the verdict - the
        // roster shows this column, and the Contention view still has the rows.
        Assert.True(profile.ContentionShareOfLife < 0.001);
    }

    // A parked worker is a small LOOP, not one frozen stack. This is the shape
    // a real service has dozens of and the one that judging on the top stack
    // alone got wrong: parked on an empty queue most of the time, sleeping out
    // a poll interval the rest, and occasionally blocked in the call that does
    // the actual work.
    [Fact]
    public void Build_ClassifiesAQueueDrainLoopAsParked_ThoughItsTopStackAloneIsOnly90Percent()
    {
        int queueParkStackIndex = stacks.Index(0x5010, 0x7010, 0x8010);
        int pollSleepStackIndex = stacks.Index(0x1010, 0x7010, 0x8010);
        int sendCallStackIndex = stacks.Index(0x6010, 0x7010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();

        // 90.9% / 6.4% / 1.0%, the real proportions off a live capture. No one
        // stack clears the concentration bar; the three together do.
        for (int loopIndex = 0; loopIndex < 300; ++loopIndex)
        {
            double loopStartMSec = loopIndex * 1000.0;
            AddSamples(sampleEvents, threadId: 274, queueParkStackIndex, ThreadSampleType.External, sampleCount: 909, startMSec: loopStartMSec);
            AddSamples(sampleEvents, threadId: 274, pollSleepStackIndex, ThreadSampleType.External, sampleCount: 64, startMSec: loopStartMSec + 909);
            AddSamples(sampleEvents, threadId: 274, sendCallStackIndex, ThreadSampleType.External, sampleCount: 10, startMSec: loopStartMSec + 973);
            AddSamples(sampleEvents, threadId: 274, sendCallStackIndex, ThreadSampleType.Managed, sampleCount: 17, startMSec: loopStartMSec + 983);
        }

        ThreadActivityProfile profile = Build(sampleEvents).TryGet(274);

        Assert.True(profile.DominantStackShare < 0.95, "the top stack alone should NOT clear the bar");
        Assert.True(profile.TopStacksShare >= 0.95, "the loop's three stacks together should");
        Assert.Equal(ThreadActivityRole.ParkedThread, profile.Role);
        Assert.True(profile.IsBenignlyParked);
        // The property that makes it benign at all: it is not in the pool, so
        // it cannot be occupying a worker the pool needs back.
        Assert.False(profile.IsPoolWorker);
    }

    // The same "parked forever in one native call" behaviour as the poll-loop
    // test above, on a thread that IS a pool worker - and the opposite verdict,
    // because this one is occupying a worker the pool can never reclaim.
    //
    // Straight off a live capture: four threads running
    // Confluent.Kafka.Consumer.Consume synchronously inside an ExecuteAsync,
    // rooted in PortableThreadPool+WorkerThread.WorkerThreadStart ->
    // ThreadPoolWorkQueue.Dispatch, each parked for the entire 300-second
    // capture. Behaviourally identical to a benign dedicated consumer; the
    // difference is entirely whose thread it is standing on.
    [Fact]
    public void Build_ClassifiesAPoolWorkerParkedOutsideThePoolAsBlocked_HoweverStillItSits()
    {
        int occupiedStackIndex = stacks.Index(0x6010, 0x2010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 28, occupiedStackIndex, ThreadSampleType.External, sampleCount: 30000);

        ThreadActivityProfile profile = Build(sampleEvents).TryGet(28);

        Assert.True(profile.IsPoolWorker);
        Assert.Equal(0, profile.PoolParkSampleCount);
        Assert.Equal(ThreadActivityRole.BlockedPoolWorker, profile.Role);
        Assert.False(profile.IsBenignlyParked);
    }

    // A thread named only as the OWNER of a contended lock still gets a
    // profile - it is the thread everyone else was blocked behind, which is
    // the most actionable row the contention data produces.
    [Fact]
    public void Build_ProfilesAThreadSeenOnlyAsAContentionOwner()
    {
        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 274, stacks.Index(0x5010, 0x8010), ThreadSampleType.External, sampleCount: 100);

        ThreadActivityProfile ownerProfile = Build(sampleEvents, new List<ContentionEvent>
        {
            MakeContention(relativeMSec: 100, durationMSec: 8.3, threadId: 274, ownerThreadId: 900)
        }).TryGet(900);

        Assert.NotNull(ownerProfile);
        Assert.Equal(1, ownerProfile.ContentionOwnerCount);
        Assert.Equal(0, ownerProfile.SampleCount);
    }

    // Fail-safe, and the direction matters: with no managed/native flag the
    // "it never ran" half of the parked test cannot be evaluated, so nothing
    // is called benign and the view shows every thread rather than hiding one
    // on evidence it does not have.
    [Fact]
    public void Build_ClassifiesNothingAsBenign_WhenTheCaptureCarriesNoSampleType()
    {
        int pollStackIndex = stacks.Index(0x6010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 103, pollStackIndex, ThreadSampleType.Unknown, sampleCount: 30000);

        ThreadActivityProfileSet profileSet = Build(sampleEvents);

        Assert.False(profileSet.HasSampleTypeData);
        Assert.Equal(0, profileSet.BenignlyParkedThreadCount);
        Assert.Equal(ThreadActivityRole.ActiveThread, profileSet.TryGet(103).Role);
    }

    // Too little evidence is not the same as evidence of benignity. A thread
    // that produced a handful of samples, or lived for a fraction of a second,
    // gets no claim made about it.
    [Fact]
    public void Build_MakesNoParkedClaim_AboutAThreadWithTooFewSamplesOrTooShortALife()
    {
        int pollStackIndex = stacks.Index(0x6010, 0x8010);

        List<SampleEvent> tinySampleCount = new List<SampleEvent>();
        AddSamples(tinySampleCount, threadId: 400, pollStackIndex, ThreadSampleType.External, sampleCount: 10);
        Assert.False(Build(tinySampleCount).TryGet(400).IsBenignlyParked);

        // Enough samples, but crammed into 200ms - "it never moved" says
        // nothing over a window that short.
        List<SampleEvent> shortLife = new List<SampleEvent>();
        for (int sampleIndex = 0; sampleIndex < 200; ++sampleIndex)
        {
            shortLife.Add(new SampleEvent(sampleIndex * 0.001, 401, pollStackIndex, ThreadSampleType.External));
        }

        Assert.False(Build(shortLife).TryGet(401).IsBenignlyParked);
    }

    // An unbroken park is not split by a gap in sampling, only by the thread
    // being caught running - a thread blocked in a native call is exactly the
    // one the sampler is most likely to miss.
    [Fact]
    public void Build_ReportsTheLongestParkAcrossASamplingGap()
    {
        int pollStackIndex = stacks.Index(0x6010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 103, pollStackIndex, ThreadSampleType.External, sampleCount: 1000, startMSec: 0);
        // A 20-second hole with no samples at all, then the park resumes.
        AddSamples(sampleEvents, threadId: 103, pollStackIndex, ThreadSampleType.External, sampleCount: 1000, startMSec: 21000);

        ThreadActivityProfile profile = Build(sampleEvents).TryGet(103);

        Assert.Equal(0, profile.WakeCount);
        Assert.Equal(21999.0, profile.LongestContinuousIdleMSec, precision: 1);
    }

    // The roster's order is what makes the emitted-thread cap safe to apply -
    // truncation has to drop the rows the reader was going to skip.
    [Fact]
    public void Build_RanksActionableThreadsAheadOfBenignOnes()
    {
        int pollStackIndex = stacks.Index(0x6010, 0x8010);
        int parkStackIndex = stacks.Index(0x1010, 0x2010, 0x8010);
        int blockedStackIndex = stacks.Index(0x5010, 0x7010, 0x2010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 103, pollStackIndex, ThreadSampleType.External, sampleCount: 30000);
        AddSamples(sampleEvents, threadId: 2143, parkStackIndex, ThreadSampleType.External, sampleCount: 30000);
        AddSamples(sampleEvents, threadId: 128853, blockedStackIndex, ThreadSampleType.External, sampleCount: 24000);
        AddSamples(sampleEvents, threadId: 128853, parkStackIndex, ThreadSampleType.External, sampleCount: 6000, startMSec: 24000);

        ThreadActivityProfileSet profileSet = Build(sampleEvents, new List<ContentionEvent>
        {
            MakeContention(relativeMSec: 500, durationMSec: 4909.5, threadId: 128853)
        });

        Assert.Equal(128853, profileSet.Ranked[0].ThreadId);
        Assert.True(profileSet.Ranked[profileSet.Ranked.Count - 1].IsBenignlyParked);
        Assert.Equal(2, profileSet.BenignlyParkedThreadCount);
        Assert.Equal(60000, profileSet.BenignlyParkedSampleCount);
    }

    // "Is this a pool worker" is a majority of the thread's samples, not any
    // one of them: on a real capture an "any sample ever" test marked a gRPC
    // handler thread a pool worker off 8 samples in 222,240, because it queued
    // a work item once.
    [Fact]
    public void Build_DoesNotCallAThreadAPoolWorker_OffASingleIncidentalPoolFrame()
    {
        int pollStackIndex = stacks.Index(0x6010, 0x8010);
        int incidentalPoolStackIndex = stacks.Index(0x7010, 0x2010, 0x8010);

        List<SampleEvent> sampleEvents = new List<SampleEvent>();
        AddSamples(sampleEvents, threadId: 104, pollStackIndex, ThreadSampleType.External, sampleCount: 30000);
        AddSamples(sampleEvents, threadId: 104, incidentalPoolStackIndex, ThreadSampleType.Managed, sampleCount: 8, startMSec: 30000);

        ThreadActivityProfile profile = Build(sampleEvents).TryGet(104);

        Assert.False(profile.IsPoolWorker);
        Assert.Equal(ThreadActivityRole.ParkedThread, profile.Role);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
