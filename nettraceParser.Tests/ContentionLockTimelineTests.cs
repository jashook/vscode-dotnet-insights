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
        using System.IO.MemoryStream stream = new System.IO.MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            ContentionJsonExporter.Write(writer, contentionEvents, MakeSymbolTable());
        }

        return JsonDocument.Parse(stream.ToArray());
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
    public void Write_TotalDistinctLockCountCountsEveryLockNotJustRankedOnes()
    {
        List<ContentionEvent> events = new List<ContentionEvent>();
        for (int lockIndex = 0; lockIndex < 60; ++lockIndex)
        {
            events.Add(MakeEvent(lockIndex * 10.0, 1.0, 0x1000 + lockIndex, ownerThreadId: 1, waiterThreadId: 2));
        }

        JsonDocument document = WriteAndParse(events);
        JsonElement timeline = document.RootElement.GetProperty("lockTimeline");

        // Only TopLocksLimit (40) get their own track, but the header count
        // reports the real total so the UI can say "showing 40 of 60".
        Assert.Equal(60, timeline.GetProperty("totalDistinctLockCount").GetInt32());
        Assert.Equal(40, timeline.GetProperty("locks").GetArrayLength());
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
