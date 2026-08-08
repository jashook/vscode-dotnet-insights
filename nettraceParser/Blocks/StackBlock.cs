////////////////////////////////////////////////////////////////////////////////
// Module: StackBlock.cs
//
// Notes:
// Decodes a StackBlock: a run of raw call stacks (NetTraceFormat_v5.md
// "Stack Block") - a FirstId/Count header followed by Count entries, each a
// (StackBytesCount, StackBytes[]) pair of pointer-sized instruction pointers.
// StackId for entry index i (0-based) is FirstId + i - IDs are not stored
// per-entry, only implied by position. Previously swallowed whole by
// SkippableBlock.cs's catch-all (which already named "StackBlock, SPBlock"
// in its own header comment as known-but-unhandled).
//
// Version/MinimumReaderVersion pinned against a real capture the same way
// every other block type in this codebase was (see EventBlock.cs/
// MetadataBlock.cs's own "NetTraceFormat_v5.md documents ... as Version
// N" comments) rather than assumed from the spec alone.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class StackBlock : IFastSerializable, IFastSerializableVersion
{
    // Empirically pinned against this project's real capture fixture:
    // Version=1 threw "App is version 1. File format accepts apps >= 2." -
    // the on-disk StackBlock's own MinimumReaderVersion is 2. We only ever
    // read the leading BlockSize plus the documented FirstId/Count/entry
    // layout (never anything version-specific beyond that), so - matching
    // SkippableBlock.cs's own reasoning for the fields it touches - claim
    // no floor on what file version we can read.
    public int Version => 2;
    public int MinimumVersionCanRead => 0;
    public int MinimumReaderVersion => 0;

    private readonly Dictionary<int, long[]> stacksById;
    private readonly int pointerSize;

    public StackBlock(Dictionary<int, long[]> stacksById, int pointerSize)
    {
        this.stacksById = stacksById;
        this.pointerSize = pointerSize;
    }

    public void FromStream(Deserializer deserializer)
    {
        int blockSize;
        deserializer.Read(out blockSize);

        NettraceBlockAlignment.SkipPaddingToFourByteAlignment(deserializer);

        long blockContentStart = (long)deserializer.Current;
        long blockContentEnd = blockContentStart + blockSize;

        int firstId;
        int count;
        deserializer.Read(out firstId);
        deserializer.Read(out count);

        // Reused across every entry in this block instead of a fresh
        // byte[stackBytesCount] per stack - a single block can have
        // thousands of entries, and allocating (then immediately
        // discarding, once decoded into the persisted long[] below) a new
        // buffer for every one of them was measured (dotnet-trace, a real
        // capture) as a meaningful share of this whole parser's cost. Only
        // grows when a bigger stack than any seen so far in this block
        // shows up; the decoded long[] instructionPointers below is the
        // only thing that actually needs to survive past this loop
        // iteration (it's what gets stored into stacksById), so scratch
        // space for the raw bytes is safe to reuse.
        byte[] scratchBuffer = Array.Empty<byte>();

        for (int entryIndex = 0; entryIndex < count; ++entryIndex)
        {
            int stackBytesCount;
            deserializer.Read(out stackBytesCount);

            if (scratchBuffer.Length < stackBytesCount)
            {
                scratchBuffer = new byte[stackBytesCount];
            }

            deserializer.Read(scratchBuffer, 0, stackBytesCount);

            long[] instructionPointers = DecodeInstructionPointers(scratchBuffer, stackBytesCount, this.pointerSize);

            int stackId = firstId + entryIndex;
            this.stacksById[stackId] = instructionPointers;
        }

        deserializer.Reader.Goto((StreamLabel)blockContentEnd);
    }

    private static long[] DecodeInstructionPointers(byte[] stackBytes, int stackBytesLength, int pointerSize)
    {
        int ipCount = stackBytesLength / pointerSize;
        long[] instructionPointers = new long[ipCount];

        ReadOnlySpan<byte> stackBytesSpan = stackBytes.AsSpan(0, stackBytesLength);

        for (int ipIndex = 0; ipIndex < ipCount; ++ipIndex)
        {
            int offset = ipIndex * pointerSize;

            if (pointerSize == 4)
            {
                instructionPointers[ipIndex] = BinaryPrimitives.ReadUInt32LittleEndian(stackBytesSpan.Slice(offset, 4));
            }
            else
            {
                instructionPointers[ipIndex] = BinaryPrimitives.ReadInt64LittleEndian(stackBytesSpan.Slice(offset, 8));
            }
        }

        return instructionPointers;
    }

    public void ToStream(Serializer serializer)
    {
        throw new System.NotImplementedException("nettraceParser is read-only.");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
