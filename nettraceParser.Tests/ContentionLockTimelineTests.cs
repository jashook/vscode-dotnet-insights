////////////////////////////////////////////////////////////////////////////////
// Module: ContentionLockTimelineTests.cs
//
// Notes:
// Covers the two halves of the Lock Timeline feature that are easy to break
// silently:
//   - ClrContentionStart's V2 payload decode. The V2 layout (LockID /
//     AssociatedObjectID / LockOwnerThreadID appended after the V1 fields)
//     is hardcoded by byte offset, so a wrong offset still "decodes" - it
//     just yields plausible-looking garbage. These tests pin each field to a
//     distinct known value so a shifted offset fails rather than passing
//     with the neighbouring field's contents. A V1 payload must decode to
//     zeros, never read past its own end.
//   - The "lockTimeline" JSON block: locks ranked by total wait (not count -
//     a lock contended often but briefly matters less than one contended
//     rarely but for seconds), lockId emitted as a HEX STRING rather than a
//     JSON number (a 64-bit pointer loses precision as a JS double past
//     2^53), events with no lock identity excluded entirely, and
//     ownerThreadId 0 preserved as "unknown" rather than dropped.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ContentionLockTimelineTests
{
    private const int PointerSize = 8;

    private static MethodSymbolTable MakeSymbolTable()
    {
        return MethodSymbolTable.Build(new List<EventRecord>(), pointerSize: PointerSize, qpcFrequency: 0, referenceQpc: 0);
    }

    private static ContentionEvent MakeEvent(double relativeMSec, double durationMSec, long lockId, long ownerThreadId, long waiterThreadId)
    {
        return new ContentionEvent(relativeMSec, durationMSec, ClrContentionFlags.Managed, waiterThreadId, Array.Empty<long>(), lockId, associatedObjectId: 0, ownerThreadId);
    }

    private static JsonDocument WriteAndParse(List<ContentionEvent> contentionEvents)
    {
        return WriteAndParseWith(contentionEvents, MakeSymbolTable());
    }

    private static JsonDocument WriteAndParseWith(List<ContentionEvent> contentionEvents, MethodSymbolTable symbolTable)
    {
        using System.IO.MemoryStream stream = new System.IO.MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            ContentionJsonExporter.Write(writer, contentionEvents, symbolTable);
        }

        return JsonDocument.Parse(stream.ToArray());
    }

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

    // 0x1000/0x2000 resolve to lock-acquisition primitives (which lock
    // naming must skip), 0x3000/0x4000 to real application frames.
    private static MethodSymbolTable MakeNamedSymbolTable()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRundownEvent(methodId: 1, startAddress: 0x1000, size: 0x100, name: "System.Threading.Monitor.Enter_Slowpath"),
            MakeRundownEvent(methodId: 2, startAddress: 0x2000, size: 0x100, name: "System.Threading.Monitor.Enter"),
            MakeRundownEvent(methodId: 3, startAddress: 0x3000, size: 0x100, name: "MyApp.DoRealWork"),
            MakeRundownEvent(methodId: 4, startAddress: 0x4000, size: 0x100, name: "MyApp.SlowPath"),
            MakeRundownEvent(methodId: 5, startAddress: 0x5000, size: 0x100, name: "System.Threading.ThreadPoolWorkQueue.Dispatch"),
            MakeRundownEvent(methodId: 6, startAddress: 0x6000, size: 0x100, name: "MyApp.DedicatedBackgroundLoop"),
        };

        return MethodSymbolTable.Build(events, pointerSize: PointerSize, qpcFrequency: 0, referenceQpc: 0);
    }

    [Fact]
    public void ClrContentionStart_Decode_V2PayloadReadsEachFieldFromItsOwnOffset()
    {
        // Deliberately distinct values - a one-field offset slip would read
        // a neighbour and still "succeed", so no two of these may match.
        byte[] payload = new PayloadBuilder()
            .WriteByte((byte)ClrContentionFlags.Managed)
            .WriteInt16(7)
            .WriteAddress(0x00007FE4488FB0A8, PointerSize)
            .WriteAddress(0x00007FEDD101A090, PointerSize)
            .WriteInt64(39868)
            .ToArray();

        Assert.Equal(27, payload.Length);

        PayloadReader reader = new PayloadReader(payload, 0, payload.Length, PointerSize);
        ClrContentionStart decoded = ClrContentionStart.Decode(reader, version: 2);

        Assert.Equal(ClrContentionFlags.Managed, decoded.ContentionFlags);
        Assert.Equal(7, decoded.ClrInstanceID);
        Assert.Equal(0x00007FE4488FB0A8, decoded.LockID);
        Assert.Equal(0x00007FEDD101A090, decoded.AssociatedObjectID);
        Assert.Equal(39868, decoded.LockOwnerThreadID);
    }

    [Fact]
    public void ClrContentionStart_Decode_V1PayloadYieldsZeroedV2FieldsNotGarbage()
    {
        byte[] payload = new PayloadBuilder()
            .WriteByte((byte)ClrContentionFlags.Managed)
            .WriteInt16(7)
            .ToArray();

        PayloadReader reader = new PayloadReader(payload, 0, payload.Length, PointerSize);
        ClrContentionStart decoded = ClrContentionStart.Decode(reader, version: 1);

        Assert.Equal(7, decoded.ClrInstanceID);
        Assert.Equal(0, decoded.LockID);
        Assert.Equal(0, decoded.AssociatedObjectID);
        Assert.Equal(0, decoded.LockOwnerThreadID);
    }

    [Fact]
    public void ClrContentionStart_Decode_VersionTwoButTruncatedPayloadDoesNotReadPastEnd()
    {
        // A V2-versioned event whose payload is too short for the V2 fields
        // (defensive - the length check must gate, not the version alone).
        byte[] payload = new PayloadBuilder()
            .WriteByte((byte)ClrContentionFlags.Managed)
            .WriteInt16(7)
            .WriteInt32(1234)
            .ToArray();

        PayloadReader reader = new PayloadReader(payload, 0, payload.Length, PointerSize);
        ClrContentionStart decoded = ClrContentionStart.Decode(reader, version: 2);

        Assert.Equal(0, decoded.LockID);
        Assert.Equal(0, decoded.LockOwnerThreadID);
    }

    [Fact]
    public void Write_NoEventsCarryLockIdentity_LockTimelineIsNull()
    {
        // Every event is a V1-style contention (LockId 0) - there is no lock
        // to place on any track, so the whole block is null rather than an
        // empty-but-present object.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 5.0, lockId: 0, ownerThreadId: 0, waiterThreadId: 100),
            MakeEvent(20.0, 5.0, lockId: 0, ownerThreadId: 0, waiterThreadId: 101),
        };

        JsonDocument document = WriteAndParse(events);

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("lockTimeline").ValueKind);
    }

    [Fact]
    public void Write_EmptyCapture_LockTimelineIsNull()
    {
        JsonDocument document = WriteAndParse(new List<ContentionEvent>());

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("lockTimeline").ValueKind);
    }

    [Fact]
    public void Write_LocksAreRankedByTotalWaitNotContentionCount()
    {
        // Lock 0xAA: 3 short waits (3ms total). Lock 0xBB: 1 long wait
        // (50ms). Ranking by count would put 0xAA first; ranking by total
        // wait - the dimension that actually matters - puts 0xBB first.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 1.0, 0xAA, ownerThreadId: 1, waiterThreadId: 2),
            MakeEvent(20.0, 1.0, 0xAA, ownerThreadId: 1, waiterThreadId: 3),
            MakeEvent(30.0, 1.0, 0xAA, ownerThreadId: 1, waiterThreadId: 4),
            MakeEvent(40.0, 50.0, 0xBB, ownerThreadId: 5, waiterThreadId: 6),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement locks = document.RootElement.GetProperty("lockTimeline").GetProperty("locks");

        Assert.Equal(2, locks.GetArrayLength());
        Assert.Equal("0xBB", locks[0].GetProperty("lockId").GetString());
        Assert.Equal(50.0, locks[0].GetProperty("totalWaitMSec").GetDouble(), 3);
        Assert.Equal("0xAA", locks[1].GetProperty("lockId").GetString());
        Assert.Equal(3, locks[1].GetProperty("contentionCount").GetInt32());
    }

    [Fact]
    public void Write_LockIdIsHexStringSoLargePointerValuesSurviveJsonRoundTrip()
    {
        // A real 64-bit lock pointer exceeds 2^53 and would lose precision
        // as a JSON number once parsed into a JS double.
        const long realWorldLockPointer = 0x00007FE4488FB0A8;

        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 1.0, realWorldLockPointer, ownerThreadId: 1, waiterThreadId: 2),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement lockEntry = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0];

        Assert.Equal(JsonValueKind.String, lockEntry.GetProperty("lockId").ValueKind);
        Assert.Equal("0x7FE4488FB0A8", lockEntry.GetProperty("lockId").GetString());
    }

    [Fact]
    public void Write_SegmentSpansWaitWindowAndCarriesBothThreads()
    {
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(100.0, 25.0, 0xAA, ownerThreadId: 4242, waiterThreadId: 9999),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement segment = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0].GetProperty("segments")[0];

        Assert.Equal(100.0, segment.GetProperty("startMSec").GetDouble(), 3);
        Assert.Equal(125.0, segment.GetProperty("endMSec").GetDouble(), 3);
        Assert.Equal(4242, segment.GetProperty("ownerThreadId").GetInt64());
        Assert.Equal(9999, segment.GetProperty("waiterThreadId").GetInt64());
    }

    [Fact]
    public void Write_UnknownOwnerSegmentIsKeptWithZeroOwnerNotDropped()
    {
        // ~12% of real waits have no attributable owner. They're still real
        // waits on that lock and must survive to the renderer (which shows
        // them as "unknown"), not silently vanish from the track.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 5.0, 0xAA, ownerThreadId: 0, waiterThreadId: 2),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement lockEntry = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0];

        Assert.Equal(1, lockEntry.GetProperty("segments").GetArrayLength());
        Assert.Equal(0, lockEntry.GetProperty("segments")[0].GetProperty("ownerThreadId").GetInt64());
    }

    [Fact]
    public void Write_EventsWithoutLockIdentityAreExcludedFromEveryTrack()
    {
        // The LockId==0 events must not be folded into a shared bogus track,
        // and must not inflate a real lock's own counts either.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 5.0, lockId: 0, ownerThreadId: 0, waiterThreadId: 2),
            MakeEvent(20.0, 5.0, 0xAA, ownerThreadId: 1, waiterThreadId: 3),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement locks = document.RootElement.GetProperty("lockTimeline").GetProperty("locks");

        Assert.Equal(1, locks.GetArrayLength());
        Assert.Equal("0xAA", locks[0].GetProperty("lockId").GetString());
        Assert.Equal(1, locks[0].GetProperty("contentionCount").GetInt32());
    }

    [Fact]
    public void Write_EveryLockIsEmittedNotJustATopSlice()
    {
        // The long tail is exported too - the UI's own Top-N control decides
        // how many tracks to draw, and "All" has to actually be reachable
        // (a lock contended once for seconds lives in that tail). Affordable
        // because total segments are bounded by contention count, not lock
        // count - see MaxOwnershipSegments' own comment.
        List<ContentionEvent> events = new List<ContentionEvent>();
        for (int lockIndex = 0; lockIndex < 60; ++lockIndex)
        {
            events.Add(MakeEvent(lockIndex * 10.0, 1.0, 0x1000 + lockIndex, ownerThreadId: 1, waiterThreadId: 2));
        }

        JsonDocument document = WriteAndParse(events);
        JsonElement timeline = document.RootElement.GetProperty("lockTimeline");

        Assert.Equal(60, timeline.GetProperty("totalDistinctLockCount").GetInt32());
        Assert.Equal(60, timeline.GetProperty("locks").GetArrayLength());
    }

    [Fact]
    public void Write_LockDrillDownFoldsContendedStacksForThatLock()
    {
        // Two waits on the same lock share a stack; a third uses a
        // different one. The lock's drillDown must fold them into the same
        // tree shape siteDrillDown emits (so the webview reuses one
        // renderer), with counts summed at the shared frames.
        long[] sharedStack = new long[] { 0x1000, 0x2000 };
        long[] otherStack = new long[] { 0x1000 };

        List<ContentionEvent> events = new List<ContentionEvent>
        {
            new ContentionEvent(10.0, 5.0, ClrContentionFlags.Managed, 2, sharedStack, 0xAA, 0, 1),
            new ContentionEvent(20.0, 5.0, ClrContentionFlags.Managed, 3, sharedStack, 0xAA, 0, 1),
            new ContentionEvent(30.0, 5.0, ClrContentionFlags.Managed, 4, otherStack, 0xAA, 0, 1),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement lockEntry = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0];
        JsonElement drillDown = lockEntry.GetProperty("drillDown");

        Assert.Equal(JsonValueKind.Object, drillDown.ValueKind);
        Assert.Equal(3, drillDown.GetProperty("contentionCount").GetInt32());
        Assert.Equal(15.0, drillDown.GetProperty("totalWaitMSec").GetDouble(), 3);
        Assert.Equal(2, drillDown.GetProperty("distinctStackCount").GetInt32());
        Assert.True(drillDown.GetProperty("children").GetArrayLength() > 0);
    }

    [Fact]
    public void Write_LockIsNamedAfterFirstNonLockPrimitiveFrame()
    {
        // Every contention stack bottoms out in the same generic
        // Monitor.Enter_Slowpath, so naming a lock by its LEAF frame would
        // label every lock in the capture identically. The name must come
        // from the first frame BELOW the lock primitives - that's what
        // distinguishes one lock from another.
        MethodSymbolTable symbolTable = MakeNamedSymbolTable();

        List<ContentionEvent> events = new List<ContentionEvent>
        {
            new ContentionEvent(10.0, 5.0, ClrContentionFlags.Managed, 2, new long[] { 0x1000, 0x2000, 0x3000 }, 0xAA, 0, 1),
        };

        JsonDocument document = WriteAndParseWith(events, symbolTable);
        JsonElement root = document.RootElement;
        JsonElement lockEntry = root.GetProperty("lockTimeline").GetProperty("locks")[0];

        int nameFrame = lockEntry.GetProperty("nameFrame").GetInt32();
        Assert.True(nameFrame >= 0);

        string resolvedName = root.GetProperty("methodNames")[nameFrame].GetString();
        Assert.Equal("MyApp.DoRealWork", resolvedName);
    }

    [Fact]
    public void Write_LockWhoseStackIsAllLockPrimitives_HasNoNameFrame()
    {
        // Nothing in the stack identifies the lock, so the renderer falls
        // back to the raw pointer rather than labelling it with a primitive
        // that says nothing.
        MethodSymbolTable symbolTable = MakeNamedSymbolTable();

        List<ContentionEvent> events = new List<ContentionEvent>
        {
            new ContentionEvent(10.0, 5.0, ClrContentionFlags.Managed, 2, new long[] { 0x1000, 0x2000 }, 0xAA, 0, 1),
        };

        JsonDocument document = WriteAndParseWith(events, symbolTable);
        JsonElement lockEntry = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0];

        Assert.Equal(-1, lockEntry.GetProperty("nameFrame").GetInt32());
    }

    [Fact]
    public void Write_LockNameComesFromItsHeaviestStackNotItsFirstSeenOne()
    {
        // Two different call paths contend the same lock; the name should
        // describe where the TIME went, matching how locks themselves are
        // ranked, not whichever stack happened to arrive first.
        MethodSymbolTable symbolTable = MakeNamedSymbolTable();

        long[] lightStack = new long[] { 0x1000, 0x3000 };
        long[] heavyStack = new long[] { 0x1000, 0x4000 };

        List<ContentionEvent> events = new List<ContentionEvent>
        {
            new ContentionEvent(10.0, 1.0, ClrContentionFlags.Managed, 2, lightStack, 0xAA, 0, 1),
            new ContentionEvent(20.0, 90.0, ClrContentionFlags.Managed, 3, heavyStack, 0xAA, 0, 1),
        };

        JsonDocument document = WriteAndParseWith(events, symbolTable);
        JsonElement root = document.RootElement;
        JsonElement lockEntry = root.GetProperty("lockTimeline").GetProperty("locks")[0];

        string resolvedName = root.GetProperty("methodNames")[lockEntry.GetProperty("nameFrame").GetInt32()].GetString();
        Assert.Equal("MyApp.SlowPath", resolvedName);
    }

    [Fact]
    public void Write_WaiterThreadCountCountsDistinctThreadsNotEvents()
    {
        // The ping-pong case: a lock hammered by only two threads. Ranking
        // by contention count makes this look like a top offender; ranking
        // by contending threads correctly shows it involves two threads and
        // starves nothing else.
        List<ContentionEvent> events = new List<ContentionEvent>();
        for (int waitIndex = 0; waitIndex < 50; ++waitIndex)
        {
            long waiter = (waitIndex % 2 == 0) ? 100 : 101;
            events.Add(new ContentionEvent(waitIndex, 1.0, ClrContentionFlags.Managed, waiter, new long[] { 0x1000, 0x3000 }, 0xAA, 0, ownerThreadId: 200));
        }

        JsonDocument document = WriteAndParseWith(events, MakeNamedSymbolTable());
        JsonElement lockEntry = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0];

        Assert.Equal(50, lockEntry.GetProperty("contentionCount").GetInt32());
        Assert.Equal(2, lockEntry.GetProperty("waiterThreadCount").GetInt32());
        Assert.Equal(1, lockEntry.GetProperty("ownerThreadCount").GetInt32());
    }

    [Fact]
    public void Write_UnknownOwnerDoesNotCountAsAnOwnerThread()
    {
        // Owner 0 means "the runtime couldn't attribute one". Counting it
        // would report a phantom extra owner on every lock that has any
        // unattributed wait.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            new ContentionEvent(10.0, 1.0, ClrContentionFlags.Managed, 100, new long[] { 0x1000, 0x3000 }, 0xAA, 0, ownerThreadId: 0),
            new ContentionEvent(20.0, 1.0, ClrContentionFlags.Managed, 101, new long[] { 0x1000, 0x3000 }, 0xAA, 0, ownerThreadId: 200),
        };

        JsonDocument document = WriteAndParseWith(events, MakeNamedSymbolTable());
        JsonElement lockEntry = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0];

        Assert.Equal(1, lockEntry.GetProperty("ownerThreadCount").GetInt32());
    }

    [Fact]
    public void Write_PoolWaiterCountOnlyCountsThreadsBlockedInsideThreadPoolWork()
    {
        // The distinction the Lock Timeline's "pool threads blocked" ranking
        // exists for: a lock blocking pool workers serializes every queued
        // work item behind it, while one blocking a dedicated background
        // thread costs only that thread. Both look identical by contention
        // count.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            // Two pool workers (stack passes through the dispatch loop).
            new ContentionEvent(10.0, 1.0, ClrContentionFlags.Managed, 100, new long[] { 0x1000, 0x3000, 0x5000 }, 0xAA, 0, 1),
            new ContentionEvent(20.0, 1.0, ClrContentionFlags.Managed, 101, new long[] { 0x1000, 0x3000, 0x5000 }, 0xAA, 0, 1),
            // One dedicated background thread on the same lock.
            new ContentionEvent(30.0, 1.0, ClrContentionFlags.Managed, 102, new long[] { 0x1000, 0x3000, 0x6000 }, 0xAA, 0, 1),
        };

        JsonDocument document = WriteAndParseWith(events, MakeNamedSymbolTable());
        JsonElement lockEntry = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0];

        Assert.Equal(3, lockEntry.GetProperty("waiterThreadCount").GetInt32());
        Assert.Equal(2, lockEntry.GetProperty("poolWaiterThreadCount").GetInt32());
        Assert.Equal(2, lockEntry.GetProperty("poolContentionCount").GetInt32());
    }

    [Fact]
    public void Write_LockContendedOnlyByDedicatedThreadsReportsNoPoolWaiters()
    {
        // The "two background threads contend all the time and I don't
        // care" case - it must be distinguishable at a glance from a lock
        // starving the pool.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            new ContentionEvent(10.0, 5.0, ClrContentionFlags.Managed, 100, new long[] { 0x1000, 0x3000, 0x6000 }, 0xAA, 0, 101),
            new ContentionEvent(20.0, 5.0, ClrContentionFlags.Managed, 101, new long[] { 0x1000, 0x3000, 0x6000 }, 0xAA, 0, 100),
        };

        JsonDocument document = WriteAndParseWith(events, MakeNamedSymbolTable());
        JsonElement lockEntry = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0];

        Assert.Equal(2, lockEntry.GetProperty("waiterThreadCount").GetInt32());
        Assert.Equal(0, lockEntry.GetProperty("poolWaiterThreadCount").GetInt32());
        Assert.Equal(0, lockEntry.GetProperty("poolContentionCount").GetInt32());
    }

    [Fact]
    public void Write_LockWithNoStacksHasNullDrillDownNotAnEmptyTree()
    {
        // Every wait on this lock was captured without a stack - an empty
        // tree would render as "no callers", which reads as a fact about
        // the code rather than about the capture.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 5.0, 0xAA, ownerThreadId: 1, waiterThreadId: 2),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement lockEntry = document.RootElement.GetProperty("lockTimeline").GetProperty("locks")[0];

        Assert.Equal(JsonValueKind.Null, lockEntry.GetProperty("drillDown").ValueKind);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
