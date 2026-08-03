////////////////////////////////////////////////////////////////////////////////
// Module: GcEventProjectorTests.cs
//
// Notes:
// GcEventProjector.Project's whole design rests on two documented, but
// never previously unit-tested, invariants: (1) GCHeapStats/GCGlobalHeapHistory
// correlate to whichever GC most recently *started*, not "started and not
// yet ended" - they're observed to land on either side of GCEnd on the wire
// - and (2) only GCs that eventually see a GCEnd are returned. Both are
// exercised directly here with hand-built EventRecord streams, including the
// out-of-order-arrival case that motivated invariant (1) in the first place.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Gc;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class GcEventProjectorTests
{
    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";
    private static readonly DateTime ReferenceUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
    private const long QpcFrequency = 10_000_000;

    private static EventRecord MakeRecord(int eventId, int version, long timeStampQpc, byte[] payloadBytes)
    {
        return new EventRecord(ClrProviderName, "Test", eventId, version, timeStampQpc, threadId: 0, stack: System.Array.Empty<long>(), fields: null, payloadBytes, payloadOffset: 0, payloadBytes.Length);
    }

    private static EventRecord MakeGcStart(int count, int depth, GCReason reason, long timeStampQpc)
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt32(count)
            .WriteInt32(depth)
            .WriteInt32((int)reason)
            .WriteInt32(0)
            .WriteInt16(1)
            .ToArray();

        return MakeRecord(ClrGcEventIds.GCStart, version: 1, timeStampQpc: timeStampQpc, payloadBytes: payload);
    }

    private static EventRecord MakeGcEnd(int count, int depth, long timeStampQpc)
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt32(count)
            .WriteInt32(depth)
            .WriteInt16(1)
            .ToArray();

        return MakeRecord(ClrGcEventIds.GCEnd, version: 1, timeStampQpc: timeStampQpc, payloadBytes: payload);
    }

    private static EventRecord MakeGcHeapStats(long generationSize0, long timeStampQpc, int pinnedObjectCount = 0)
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt64(generationSize0)
            .WriteInt64(0).WriteInt64(0).WriteInt64(0)
            .WriteInt64(0).WriteInt64(0)
            .WriteInt64(0).WriteInt64(0)
            .WriteInt64(0).WriteInt64(0)
            .WriteInt32(pinnedObjectCount).WriteInt32(0).WriteInt32(0)
            .WriteInt16(1)
            .WriteInt64(0).WriteInt64(0)
            .ToArray();

        return MakeRecord(ClrGcEventIds.GCHeapStats, version: 2, timeStampQpc: timeStampQpc, payloadBytes: payload);
    }

    // condemnedGeneration has no default - GcEventProjector.cs now keys its
    // per-generation pending queue off this field (GCStart's own Depth
    // enqueues a GC, GCGlobalHeapHistory's CondemnedGeneration is what
    // resolves it back out), so it must match whatever depth/generation
    // the test's own MakeGcStart used or correlation will silently fail to
    // resolve (found the hard way: an earlier version of this helper
    // hardcoded CondemnedGeneration=2 for every call, which happened to
    // match only the gen2 test scenarios and broke every gen0 one once
    // the projector started using it).
    private static EventRecord MakeGcGlobalHeapHistory(int numHeaps, GCReason reason, long timeStampQpc, int condemnedGeneration, GCGlobalMechanisms globalMechanisms = GCGlobalMechanisms.Concurrent)
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt64(8 * 1024 * 1024)
            .WriteInt32(numHeaps)
            .WriteInt32(condemnedGeneration)
            .WriteInt32(0)
            .WriteInt32((int)reason)
            .WriteInt32((int)globalMechanisms)
            .WriteInt16(1)
            .ToArray();

        return MakeRecord(ClrGcEventIds.GCGlobalHeapHistory, version: 1, timeStampQpc: timeStampQpc, payloadBytes: payload);
    }

    // Matches ClrGcHeap.Decode's version>=3, pointerSize=8 field layout:
    // ClrInstanceID (Int16 @ 0), HeapIndex (Int32 @ HostOffset(46,6)=70 for
    // an 8-byte pointer size), generation count (Int32 @ HostOffset(54,7)=82),
    // then that many 80-byte (PointerSize*10) generation entries starting
    // at offset 86. Only HeapIndex matters for what these tests check, so
    // generation count is fixed at 1 and its entry is all zeros.
    private static EventRecord MakeGcPerHeapHistory(int heapIndex, long timeStampQpc)
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt16(1)
            .Pad(68)
            .WriteInt32(heapIndex)
            .Pad(8)
            .WriteInt32(1)
            .Pad(80)
            .ToArray();

        return MakeRecord(ClrGcEventIds.GCPerHeapHistory, version: 3, timeStampQpc: timeStampQpc, payloadBytes: payload);
    }

    [Fact]
    public void Project_CorrelatesStartEndHeapStatsAndGlobalHistoryIntoOneCompletedGc()
    {
        // GlobalHeapHistory before HeapStats - verified real-capture order
        // (GcEventProjector.cs's own comment on ResolveNextInGeneration):
        // GlobalHeapHistory carries CondemnedGeneration, the only field
        // that disambiguates which GC a bookkeeping batch belongs to, so
        // the projector resolves on GlobalHeapHistory and routes the
        // HeapStats/PerHeapHistory that follow it to that same GC.
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 1, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 0),
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.AllocSmall, timeStampQpc: 39000, condemnedGeneration: 0),
            MakeGcHeapStats(generationSize0: 4096, timeStampQpc: 40000),
            MakeGcEnd(count: 1, depth: 0, timeStampQpc: 50000)
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Single(completed);

        GcEvent gcEvent = completed[0];
        Assert.Equal(1, gcEvent.Id);
        Assert.Equal(0, gcEvent.Generation);
        Assert.True(gcEvent.HasEnd);
        Assert.True(gcEvent.HasHeapStats);
        Assert.True(gcEvent.HasGlobalHeapHistory);
        Assert.Equal(4096, gcEvent.GenerationSize0);
        Assert.Equal(1, gcEvent.NumHeaps);
        // 50000 QPC ticks @ 10,000,000/sec == 5ms.
        Assert.Equal(5.0, gcEvent.PauseDurationMSec, precision: 6);
        Assert.Equal(0.0, gcEvent.PauseStartRelativeMSec, precision: 6);
        Assert.Equal(5.0, gcEvent.PauseEndRelativeMSec, precision: 6);
    }

    [Fact]
    public void Project_CorrelatesBookkeepingEventsThatArriveAfterGcEndOnTheWire()
    {
        // GCGlobalHeapHistory/GCHeapStats land AFTER GCEnd here - this is
        // the exact ordering GcEventProjector.cs's own comments document as
        // observed in real captures, and the reason correlation doesn't
        // key off "not yet ended". GlobalHeapHistory still precedes
        // HeapStats (see ResolveNextInGeneration's comment).
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 1, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 0),
            MakeGcEnd(count: 1, depth: 0, timeStampQpc: 50000),
            MakeGcGlobalHeapHistory(numHeaps: 2, reason: GCReason.AllocSmall, timeStampQpc: 51000, condemnedGeneration: 0),
            MakeGcHeapStats(generationSize0: 8192, timeStampQpc: 52000)
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Single(completed);
        Assert.Equal(8192, completed[0].GenerationSize0);
        Assert.Equal(2, completed[0].NumHeaps);
    }

    [Fact]
    public void Project_ExcludesGcsThatNeverSawAnEnd()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 1, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 0)
            // No matching GCEnd.
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Empty(completed);
    }

    [Fact]
    public void Project_CorrelatesMultipleInterleavedGcsIndependentlyAndSortsById()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 2, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 0),
            MakeGcEnd(count: 2, depth: 0, timeStampQpc: 10000),
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.AllocSmall, timeStampQpc: 10500, condemnedGeneration: 0),
            MakeGcHeapStats(generationSize0: 111, timeStampQpc: 10600),

            MakeGcStart(count: 1, depth: 2, reason: GCReason.Induced, timeStampQpc: 20000),
            MakeGcEnd(count: 1, depth: 2, timeStampQpc: 60000),
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.Induced, timeStampQpc: 60500, condemnedGeneration: 2),
            MakeGcHeapStats(generationSize0: 222, timeStampQpc: 60600)
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Equal(2, completed.Count);
        // Sorted by Id, not by wire/start order (GC #2 started first here).
        Assert.Equal(1, completed[0].Id);
        Assert.Equal(222, completed[0].GenerationSize0);
        Assert.Equal(2, completed[1].Id);
        Assert.Equal(111, completed[1].GenerationSize0);
    }

    [Fact]
    public void Project_IgnoresEventsFromOtherProviders()
    {
        byte[] payload = new PayloadBuilder().WriteInt32(1).WriteInt32(0).WriteInt32(0).WriteInt32(0).WriteInt16(1).ToArray();

        EventRecord foreignEvent = new EventRecord("Some-Other-Provider", "Whatever", ClrGcEventIds.GCStart, version: 1, timeStampRelativeQpc: 0, threadId: 0, stack: System.Array.Empty<long>(), fields: null, payload, payloadOffset: 0, payload.Length);

        List<GcEvent> completed = GcEventProjector.Project(new List<EventRecord> { foreignEvent }, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Empty(completed);
    }

    [Fact]
    public void Project_GlobalHeapHistoryReasonSupersedesGcStartReason()
    {
        // GcEventProjector.cs's own comment: GCGlobalHeapHistory's Reason is
        // the more reliable one since GCStart's can be superseded by the
        // time collection actually begins.
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 1, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 0),
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.Induced, timeStampQpc: 1000, condemnedGeneration: 0),
            MakeGcEnd(count: 1, depth: 0, timeStampQpc: 50000)
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Equal(GCReason.Induced, completed[0].Reason);
    }

    [Fact]
    public void Project_PopulatesPinnedObjectCountFromHeapStats()
    {
        // GcEventProjector.cs resolves which GC a HeapStats event belongs
        // to via the GlobalHeapHistory that precedes it (HeapStats carries
        // no generation/id field of its own) - a real capture always pairs
        // them, so this includes one rather than testing an unrealistic
        // "HeapStats with nothing to resolve against" shape.
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 1, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 0),
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.AllocSmall, timeStampQpc: 39000, condemnedGeneration: 0),
            MakeGcHeapStats(generationSize0: 4096, timeStampQpc: 40000, pinnedObjectCount: 37),
            MakeGcEnd(count: 1, depth: 0, timeStampQpc: 50000)
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Single(completed);
        Assert.Equal(37, completed[0].PinnedObjectCount);
    }

    [Fact]
    public void Project_PopulatesGlobalMechanismsWithCompactionFlagFromGlobalHeapHistory()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 1, depth: 2, reason: GCReason.AllocSmall, timeStampQpc: 0),
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.AllocSmall, timeStampQpc: 1000, condemnedGeneration: 2,
                globalMechanisms: GCGlobalMechanisms.Compaction | GCGlobalMechanisms.Concurrent),
            MakeGcEnd(count: 1, depth: 2, timeStampQpc: 50000)
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Single(completed);
        Assert.True((completed[0].GlobalMechanisms & GCGlobalMechanisms.Compaction) != 0);
        Assert.True((completed[0].GlobalMechanisms & GCGlobalMechanisms.Concurrent) != 0);
    }

    [Fact]
    public void Project_CorrelatesSlowBackgroundGen2GcCorrectlyDespiteFasterOverlappingGen0Gc()
    {
        // Regression test for the actual bug this design was built to fix,
        // reproduced with a minimal shape rather than the real ~109,000-
        // event capture that surfaced it: a slow background/gen2 GC's own
        // bookkeeping (GlobalHeapHistory then PerHeapHistory) can genuinely
        // still be pending when a faster gen0 GC starts AND completes its
        // *entire* bookkeeping cycle in the meantime. Before resolving via
        // GlobalHeapHistory's own CondemnedGeneration (keyed per
        // generation), both a single "most recently started" pointer and a
        // single shared FIFO queue misattributed one GC's heap data to the
        // other whenever this overlap occurred.
        List<EventRecord> events = new List<EventRecord>
        {
            // Background gen2 GC starts first...
            MakeGcStart(count: 100, depth: 2, reason: GCReason.Induced, timeStampQpc: 0),

            // ...but a faster gen0 GC starts and fully completes its own
            // bookkeeping entirely before GC 100's own bookkeeping arrives.
            MakeGcStart(count: 101, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 1000),
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.AllocSmall, timeStampQpc: 1500, condemnedGeneration: 0),
            MakeGcPerHeapHistory(heapIndex: 0, timeStampQpc: 1600),
            MakeGcEnd(count: 101, depth: 0, timeStampQpc: 2000),

            // GC 100's own bookkeeping finally arrives, well after 101's.
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.Induced, timeStampQpc: 5000, condemnedGeneration: 2),
            MakeGcPerHeapHistory(heapIndex: 0, timeStampQpc: 5100),
            MakeGcEnd(count: 100, depth: 2, timeStampQpc: 5500)
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Equal(2, completed.Count);

        GcEvent gen0Gc = completed.Find(gc => gc.Id == 101);
        GcEvent gen2Gc = completed.Find(gc => gc.Id == 100);

        Assert.NotNull(gen0Gc);
        Assert.NotNull(gen2Gc);

        // Each GC gets exactly its own single heap entry, not the other's
        // (and not zero, which is what the bug produced for one of them).
        Assert.Single(gen0Gc.Heaps);
        Assert.Single(gen2Gc.Heaps);
        Assert.True(gen0Gc.HasGlobalHeapHistory);
        Assert.True(gen2Gc.HasGlobalHeapHistory);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
