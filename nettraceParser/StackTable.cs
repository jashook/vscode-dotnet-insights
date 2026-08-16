////////////////////////////////////////////////////////////////////////////////
// Module: StackTable.cs
//
// Notes:
// Every distinct call stack decoded from the capture, in decode order, so that
// an event can refer to its stack by a small dense INDEX rather than by
// holding the decoded long[] itself.
//
// This exists for one measured reason. Every consumer that groups events by
// "which stack was this" - the CPU, allocation, exception and contention
// exporters all do - used to key a Dictionary<long[], _> by the array OBJECT,
// with ReferenceEqualityComparer. That works (see EventRecord's own comment on
// why stacks are resolved eagerly at parse time), but it makes object identity
// the key, so every lookup pays RuntimeHelpers.GetHashCode plus a probe into a
// dictionary with hundreds of thousands of entries. On a real 3.23GB/16.24M-
// sample capture that probe was the single largest cost in the whole CPU
// export phase. Two attempts to make it cheaper both failed and are recorded
// in CLAUDE.md so they aren't retried: shrinking the number of probes with a
// better sticky cache didn't move wall time, and replacing the identity hash
// with a content-derived one was 6x SLOWER through collisions.
//
// An index sidesteps the question entirely: the consumers index a plain array
// instead of hashing anything, and "the same stack" is an integer compare.
//
// Index 0 is always the empty stack, so "this event had no stack" needs no
// sentinel and no null check - EmptyStackIndex resolves to a real, empty
// frames array like any other index.
//
// The table also owns the only reference to each decoded long[], which is why
// EventRecord could drop its own: 35M event records holding one fewer
// reference each is 35M fewer references for the GC to trace (see
// ReadPhaseGcSuppression.cs for how much that phase's GC behaviour matters).
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class StackTable
{
    // Reserved at construction so it is a valid index into framesByIndex,
    // never a magic number a caller has to test for before indexing.
    public const int EmptyStackIndex = 0;

    private const int NoIndex = -1;

    private readonly List<long[]> framesByIndex = new List<long[]>();

    // hash -> most recent index with that hash, then nextWithSameHash chains
    // back through the rest. A chain rather than a Dictionary<key, list> so a
    // collision costs one int per stack and no extra allocation.
    private readonly Dictionary<int, int> indexByHash = new Dictionary<int, int>();
    private readonly List<int> nextWithSameHash = new List<int>();

    public StackTable()
    {
        this.framesByIndex.Add(Array.Empty<long>());
        this.nextWithSameHash.Add(NoIndex);
    }

    // Includes the reserved empty stack at index 0.
    public int Count => this.framesByIndex.Count;

    // Returns the index for these frames, DEDUPLICATED by content: the same
    // call stack decoded twice gets one entry and one index.
    //
    // The runtime re-emits stacks after every sequence point (that is the
    // whole reason StackIds are recyclable - see EventRecord.StackIndex), so
    // the duplication is not incidental, it is the format working as designed.
    // Measured on a real 3.23GB capture: 2,430,313 stacks were decoded and
    // 2,346,969 of them - 96.6%, 1,481MB of 1,539MB - were byte-identical
    // repeats of one already held. Deduplicating leaves 83,344 real stacks.
    //
    // Callers pass a SPAN over a reusable decode buffer, not a fresh array
    // (see StackBlock.FromStream): a hit copies nothing at all, so the 96.6%
    // case allocates zero. Only a miss materializes a right-sized long[].
    // That ordering matters because the read phase runs inside a no-GC region
    // (see ReadPhaseGcSuppression.cs) where allocating the duplicate and
    // dropping it would still hold its pages until the region ends.
    public int GetOrAdd(ReadOnlySpan<long> frames)
    {
        if (frames.Length == 0)
        {
            return EmptyStackIndex;
        }

        int hash = ComputeHash(frames);

        int slot;
        if (this.indexByHash.TryGetValue(hash, out slot))
        {
            // Walk this hash's own chain before concluding it's new - two
            // different stacks CAN hash alike, and merging them would silently
            // attribute one call path's samples to another.
            int candidate = slot;
            while (candidate != NoIndex)
            {
                if (frames.SequenceEqual(this.framesByIndex[candidate]))
                {
                    return candidate;
                }

                candidate = this.nextWithSameHash[candidate];
            }
        }
        else
        {
            slot = NoIndex;
        }

        long[] stored = frames.ToArray();
        int index = this.framesByIndex.Count;
        this.framesByIndex.Add(stored);
        this.nextWithSameHash.Add(slot);
        this.indexByHash[hash] = index;
        return index;
    }

    // Frame count plus a sample of frames, mixed. Deliberately not every
    // frame: real stacks here average ~76 frames, the leaf/root/middle
    // triple already separates them well in practice, and this runs once per
    // decoded stack (2.4M times on the capture above). Collisions cost a
    // SequenceEqual, never correctness.
    private static int ComputeHash(ReadOnlySpan<long> frames)
    {
        long hash = frames.Length;
        hash = (hash * 31) + frames[0];
        hash = (hash * 31) + frames[frames.Length - 1];
        hash = (hash * 31) + frames[frames.Length / 2];
        hash = (hash * 31) + frames[frames.Length / 4];
        return (int)hash ^ (int)(hash >> 32);
    }

    // Leaf-first (index 0 is the innermost frame), the same order every Stack
    // in this codebase carries - see EventRecord's own comment. Empty, never
    // null, for EmptyStackIndex.
    public long[] FramesAt(int stackIndex)
    {
        return this.framesByIndex[stackIndex];
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
