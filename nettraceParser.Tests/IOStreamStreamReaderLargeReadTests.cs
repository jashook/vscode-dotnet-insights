////////////////////////////////////////////////////////////////////////////////
// Module: IOStreamStreamReaderLargeReadTests.cs
//
// Notes:
// Regression coverage for a silent data-corruption bug in the vendored
// FastSerialization reader (StreamReaderWriter.cs, from microsoft/perfview),
// found when a 3.01GB capture failed to open.
//
// IOStreamStreamReader.Read has a separate path for reads larger than its own
// 16KB cache: it copies whatever the cache holds, then pulls the remainder
// straight out of the underlying Stream. Before that read, upstream ran a
// "throw out delta" loop gated on `inputStreamBytesRead < positionInStream`.
//
// That gate is only meaningful for a NON-seekable stream. On a seekable one,
// Fill() positions the stream with inputStream.Seek(...) and merely does
// `inputStreamBytesRead += count`, making it a running total of bytes read
// rather than a stream position - so the two drift apart (each pass through
// the large-read path advances positionInStream by more than it adds to
// inputStreamBytesRead). Once the drift makes inputStreamBytesRead dip below
// positionInStream, the loop "catches up" by swallowing that many REAL bytes,
// and every byte the large read then pulls from the stream is shifted by that
// amount.
//
// Nothing reports this as an error. On the real capture the drift crossed zero
// at EventBlock #2839 (file offset 513,015,076), 80 bytes were swallowed, and
// the block's bytes past its cached prefix were garbage - the parser decoded
// stack IPs where an event header belonged. It only surfaced at all because
// that particular block then ran off its own end; a block that stayed in
// bounds would have produced plausible-looking, wrong events.
//
// EventBlock.FromStream is what exercises this path (one bulk Read per block,
// ~100KB each), which is why the bug appeared only after blocks started owning
// their own buffers - nothing in this codebase previously issued a large Read
// against a seekable stream.
//
// The test drives the same shape as a real parse - interleaved small reads
// (which drain and refill the cache) and ~100KB bulk block reads at
// non-8-aligned positions - over a synthetic stream whose every byte is known,
// and asserts the bytes handed back are the bytes at the requested offset.
// Verified to FAIL against the pre-fix reader and pass after it.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.IO;

using FastSerialization;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class IOStreamStreamReaderLargeReadTests
{
    // Mirrors a real capture's own numbers: EventBlock #2839 was 101,836 bytes
    // (not a multiple of the reader's 8-byte alignment, which is what forces
    // the large-read path's aligned/unaligned split to run both halves).
    private const int RealBlockSize = 101836;

    private static byte PatternByteAt(long offset)
    {
        // Position-derived so any shift, however small, changes the value -
        // a repeating or low-entropy pattern could let an off-by-N read look
        // correct by coincidence.
        return unchecked((byte)((offset * 31) ^ (offset >> 11)));
    }

    private static byte[] BuildPatternedStream(int length)
    {
        byte[] data = new byte[length];

        for (int index = 0; index < length; ++index)
        {
            data[index] = PatternByteAt(index);
        }

        return data;
    }

    [Fact]
    public void Read_LargeBlocksInterleavedWithSmallReads_ReturnsBytesAtRequestedOffset()
    {
        // Big enough for many iterations of the block-shaped access pattern,
        // since the drift that triggers the bug accumulates across them.
        const int StreamLength = 64 * 1024 * 1024;

        byte[] data = BuildPatternedStream(StreamLength);

        using (MemoryStream stream = new MemoryStream(data, writable: false))
        {
            IOStreamStreamReader reader = new IOStreamStreamReader(stream, SerializationSettings.Default);

            byte[] blockBytes = new byte[RealBlockSize];
            int blockIndex = 0;

            while (true)
            {
                // A block is preceded by its tag/size and padding in a real
                // file. Reading a varying, deliberately non-8-aligned number of
                // small values here is what leaves `position` unaligned at the
                // bulk read, exactly as a real parse does.
                int smallReadCount = 1 + (blockIndex % 5);

                for (int smallReadIndex = 0; smallReadIndex < smallReadCount; ++smallReadIndex)
                {
                    reader.ReadByte();
                }

                reader.ReadInt32();

                long blockStart = (long)reader.Current;

                if (blockStart + RealBlockSize > StreamLength)
                {
                    break;
                }

                reader.Read(blockBytes, 0, RealBlockSize);

                for (int index = 0; index < RealBlockSize; ++index)
                {
                    byte expected = PatternByteAt(blockStart + index);

                    if (blockBytes[index] != expected)
                    {
                        Assert.Fail($"Block {blockIndex} at stream offset {blockStart} differs at byte {index}: expected 0x{expected:X2}, got 0x{blockBytes[index]:X2}. The large-read path returned bytes from the wrong stream position.");
                    }
                }

                // Matches EventBlock.FromStream's own trailing Goto to the
                // block's end, which is part of the sequence that lets the
                // position bookkeeping drift.
                reader.Goto((StreamLabel)(blockStart + RealBlockSize));

                // A real parse also SKIPS blocks outright - SkippableBlock, for
                // any type NettraceFile.Read has no factory for, seeks straight
                // past the block's contents. A forward Goto advances the
                // reader's logical position while reading nothing, which is what
                // actually drives inputStreamBytesRead below positionInStream;
                // without a skip in the mix the two stay balanced and the bug
                // never trips.
                if ((blockIndex % 3) == 0)
                {
                    long skipTarget = (long)reader.Current + 50021;

                    if (skipTarget + RealBlockSize > StreamLength)
                    {
                        break;
                    }

                    reader.Goto((StreamLabel)skipTarget);
                }

                ++blockIndex;
            }

            // Guards the guard: if the loop stopped early the assertions above
            // would have proven nothing.
            Assert.True(blockIndex > 500, $"Expected the pattern to run for many blocks, but only {blockIndex} ran.");
        }
    }

    [Fact]
    public void Read_LargeReadImmediatelyAfterSeekBackwards_ReturnsBytesAtRequestedOffset()
    {
        // A backwards Goto invalidates the cache and leaves the underlying
        // stream positioned well AHEAD of the reader's logical position. The
        // large-read path must re-seek rather than trust where the stream
        // happens to sit.
        const int StreamLength = 8 * 1024 * 1024;

        byte[] data = BuildPatternedStream(StreamLength);

        using (MemoryStream stream = new MemoryStream(data, writable: false))
        {
            IOStreamStreamReader reader = new IOStreamStreamReader(stream, SerializationSettings.Default);

            reader.Goto((StreamLabel)(4 * 1024 * 1024));
            reader.ReadInt32();

            // Deliberately unaligned, and behind everything read so far.
            const long RereadStart = 1234567;
            reader.Goto((StreamLabel)RereadStart);

            byte[] blockBytes = new byte[RealBlockSize];
            reader.Read(blockBytes, 0, RealBlockSize);

            for (int index = 0; index < RealBlockSize; ++index)
            {
                byte expected = PatternByteAt(RereadStart + index);

                if (blockBytes[index] != expected)
                {
                    Assert.Fail($"Backwards re-read differs at byte {index}: expected 0x{expected:X2}, got 0x{blockBytes[index]:X2}.");
                }
            }
        }
    }

    [Fact]
    public void Read_LengthJustOverCacheSize_DoesNotOverrunDestination()
    {
        // The cache holds bytes.Length (16KB + 8) bytes, but `alignedLength`
        // for a length just over the cache size can be SMALLER than the number
        // of cached bytes available. Copying all of them would overrun the
        // caller's buffer and push the position bookkeeping past the aligned
        // read. Sizes here straddle that boundary.
        const int StreamLength = 4 * 1024 * 1024;

        byte[] data = BuildPatternedStream(StreamLength);

        for (int length = 16385; length <= 16400; ++length)
        {
            using (MemoryStream stream = new MemoryStream(data, writable: false))
            {
                IOStreamStreamReader reader = new IOStreamStreamReader(stream, SerializationSettings.Default);

                // Fill the cache, then leave the position near its start so the
                // cache holds more than `alignedLength` bytes.
                reader.ReadByte();
                reader.Goto((StreamLabel)3);

                byte[] destination = new byte[length];
                reader.Read(destination, 0, length);

                for (int index = 0; index < length; ++index)
                {
                    byte expected = PatternByteAt(3 + index);

                    if (destination[index] != expected)
                    {
                        Assert.Fail($"Length {length} differs at byte {index}: expected 0x{expected:X2}, got 0x{destination[index]:X2}.");
                    }
                }

                Assert.Equal(3 + length, (long)reader.Current);
            }
        }
    }
}

}
