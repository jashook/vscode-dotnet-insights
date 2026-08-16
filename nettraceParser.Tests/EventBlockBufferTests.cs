////////////////////////////////////////////////////////////////////////////////
// Module: EventBlockBufferTests.cs
//
// Notes:
// Regression coverage for the two bugs found when a 3.01GB capture
// (3,229,489,950 bytes) could not be opened at all.
//
//   1. THE 2GB CEILING. NettraceFile.Read used to File.ReadAllBytes the whole
//      capture into one byte[] and hand every EventRecord an ABSOLUTE offset
//      into it. That capped the parser twice over: a byte[] cannot hold more
//      than int.MaxValue elements, so the read threw "The file is too long"
//      outright, and the offset was itself an int, so even a successful read
//      would have wrapped to negative offsets past 2GB and mis-decoded
//      silently. Blocks now own their buffers and offsets are block-relative.
//
//      A 3GB fixture cannot be checked in, so what is pinned here is the
//      PROPERTY that removes the ceiling rather than the file size: every
//      payload slice lies inside a per-block buffer whose own size is what
//      bounds the offsets. If anything reverts to a whole-file buffer, the
//      buffer-size assertions below fail immediately on a 1MB fixture.
//
//   2. PADDING DECODED AS A DUPLICATE EVENT. A real capture's event block
//      ends with zero padding (measured: an 8-byte zero tail on a 101,836-byte
//      block). A zero flags byte does not mean "empty event" - per
//      CompressedEventBlobHeader.Read it means "reuse EVERY field from the
//      previous event" - so two bytes of padding decode into a complete,
//      plausible-looking duplicate of the last real event, with its payload
//      read from whatever followed. The whole-file buffer made that a legal
//      read, so the trace silently gained fabricated events.
//
//      The rule that rejects it is the format's own: an event lives entirely
//      inside its own block. Both halves of that rule are asserted below.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using DotnetInsights.NetTrace;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventBlockBufferTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "trace2.nettrace");

    // A structural invariant that a fabricated padding event would violate:
    // its payload runs past the end of its own block.
    //
    // Honest about its reach - this does NOT reproduce bug 2. The checked-in
    // fixture contains no zero-padded block (verified: the pre-fix parser
    // decodes it to byte-identical output), so only the 3.01GB capture
    // actually exercises that path. What this pins is the rule that makes the
    // fabrication impossible, on every block of a real capture.
    [Fact]
    public void Read_EveryEventPayloadSliceLiesInsideItsOwnBlockBuffer()
    {
        NettraceFile file = NettraceFile.Read(FixturePath);

        Assert.True(file.Events.Count > 0, "Fixture decoded no events at all.");

        for (int eventIndex = 0; eventIndex < file.Events.Count; ++eventIndex)
        {
            EventRecord record = file.Events[eventIndex];

            Assert.NotNull(record.PayloadBuffer);
            Assert.True(record.PayloadOffset >= 0,
                $"Event {eventIndex} has a negative payload offset ({record.PayloadOffset}) - the signature of an int-truncated absolute file offset.");
            Assert.True(record.PayloadLength >= 0, $"Event {eventIndex} has a negative payload length.");
            Assert.True((long)record.PayloadOffset + record.PayloadLength <= record.PayloadBuffer.Length,
                $"Event {eventIndex} payload runs past the end of its own block buffer " +
                $"(offset {record.PayloadOffset} + length {record.PayloadLength} > buffer {record.PayloadBuffer.Length}).");
        }
    }

    // Bug 1: the buffers must be per-block, which is what keeps every offset
    // an int no matter how large the capture is. A whole-file buffer would
    // make each buffer as large as the file and leave exactly one instance.
    [Fact]
    public void Read_PayloadBuffersAreOwnedPerBlockNotSharedAcrossTheWholeFile()
    {
        long fixtureLength = new FileInfo(FixturePath).Length;

        NettraceFile file = NettraceFile.Read(FixturePath);

        // Reference equality: distinct blocks must hand out distinct arrays.
        HashSet<byte[]> distinctBuffers = new HashSet<byte[]>(ReferenceEqualityComparer.Instance as IEqualityComparer<byte[]>);
        int largestBufferLength = 0;

        for (int eventIndex = 0; eventIndex < file.Events.Count; ++eventIndex)
        {
            byte[] buffer = file.Events[eventIndex].PayloadBuffer;
            distinctBuffers.Add(buffer);

            if (buffer.Length > largestBufferLength)
            {
                largestBufferLength = buffer.Length;
            }
        }

        Assert.True(file.EventBlockCount > 1,
            "Fixture has too few event blocks to distinguish per-block buffers from a whole-file one.");
        Assert.True(distinctBuffers.Count > 1,
            $"All {file.Events.Count} events share a single payload buffer across {file.EventBlockCount} blocks - the whole-file buffer is back, and with it the 2GB ceiling.");
        Assert.True(largestBufferLength < fixtureLength,
            $"Largest payload buffer ({largestBufferLength} bytes) is the size of the whole file ({fixtureLength} bytes).");
    }

    // The padding guard must reject only padding. If it ever starts trimming
    // real events off the end of a block, this count moves - it is the same
    // fixture RealCaptureTests pins its GC and allocation counts against, and
    // those two counts are the cross-check that the events dropped here would
    // have been real ones.
    [Fact]
    public void Read_FixtureEventCountIsUnchangedByTheBlockPaddingGuard()
    {
        NettraceFile file = NettraceFile.Read(FixturePath);

        Assert.Equal(PinnedFixtureEventCount, file.Events.Count);
    }

    // Verified against the pre-fix parser rather than simply read off the
    // fixed one: the whole-file reader was restored (git stash) and run on
    // this same fixture, giving the same 14,126 events and byte-identical
    // --json output. So this pin is not a value the fix invented for itself.
    private const int PinnedFixtureEventCount = 14126;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
