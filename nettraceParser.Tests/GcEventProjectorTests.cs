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
        return new EventRecord
        {
            ProviderName = ClrProviderName,
            EventName = "Test",
            EventId = eventId,
            Version = version,
            TimeStampRelativeQPC = timeStampQpc,
            PayloadBytes = payloadBytes
        };
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

    private static EventRecord MakeGcGlobalHeapHistory(int numHeaps, GCReason reason, long timeStampQpc, GCGlobalMechanisms globalMechanisms = GCGlobalMechanisms.Concurrent)
    {
        byte[] payload = new PayloadBuilder()
            .WriteInt64(8 * 1024 * 1024)
            .WriteInt32(numHeaps)
            .WriteInt32(2)
            .WriteInt32(0)
            .WriteInt32((int)reason)
            .WriteInt32((int)globalMechanisms)
            .WriteInt16(1)
            .ToArray();

        return MakeRecord(ClrGcEventIds.GCGlobalHeapHistory, version: 1, timeStampQpc: timeStampQpc, payloadBytes: payload);
    }

    [Fact]
    public void Project_CorrelatesStartEndHeapStatsAndGlobalHistoryIntoOneCompletedGc()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 1, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 0),
            MakeGcHeapStats(generationSize0: 4096, timeStampQpc: 40000),
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.AllocSmall, timeStampQpc: 45000),
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
        // GCHeapStats/GCGlobalHeapHistory land AFTER GCEnd here - this is the
        // exact ordering GcEventProjector.cs's own header comment documents
        // as observed in real captures, and the reason correlation keys off
        // "most recently started", not "not yet ended".
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 1, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 0),
            MakeGcEnd(count: 1, depth: 0, timeStampQpc: 50000),
            MakeGcHeapStats(generationSize0: 8192, timeStampQpc: 51000),
            MakeGcGlobalHeapHistory(numHeaps: 2, reason: GCReason.AllocSmall, timeStampQpc: 52000)
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
            MakeGcHeapStats(generationSize0: 111, timeStampQpc: 10500),

            MakeGcStart(count: 1, depth: 2, reason: GCReason.Induced, timeStampQpc: 20000),
            MakeGcEnd(count: 1, depth: 2, timeStampQpc: 60000),
            MakeGcHeapStats(generationSize0: 222, timeStampQpc: 60500)
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
        EventRecord foreignEvent = new EventRecord
        {
            ProviderName = "Some-Other-Provider",
            EventName = "Whatever",
            EventId = ClrGcEventIds.GCStart,
            Version = 1,
            TimeStampRelativeQPC = 0,
            PayloadBytes = new PayloadBuilder().WriteInt32(1).WriteInt32(0).WriteInt32(0).WriteInt32(0).WriteInt16(1).ToArray()
        };

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
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.Induced, timeStampQpc: 1000),
            MakeGcEnd(count: 1, depth: 0, timeStampQpc: 50000)
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Equal(GCReason.Induced, completed[0].Reason);
    }

    [Fact]
    public void Project_PopulatesPinnedObjectCountFromHeapStats()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeGcStart(count: 1, depth: 0, reason: GCReason.AllocSmall, timeStampQpc: 0),
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
            MakeGcGlobalHeapHistory(numHeaps: 1, reason: GCReason.AllocSmall, timeStampQpc: 1000,
                globalMechanisms: GCGlobalMechanisms.Compaction | GCGlobalMechanisms.Concurrent),
            MakeGcEnd(count: 1, depth: 2, timeStampQpc: 50000)
        };

        List<GcEvent> completed = GcEventProjector.Project(events, pointerSize: 8, qpcFrequency: QpcFrequency, referenceUtc: ReferenceUtc, referenceQpc: 0);

        Assert.Single(completed);
        Assert.True((completed[0].GlobalMechanisms & GCGlobalMechanisms.Compaction) != 0);
        Assert.True((completed[0].GlobalMechanisms & GCGlobalMechanisms.Concurrent) != 0);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
