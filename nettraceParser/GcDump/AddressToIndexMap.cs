////////////////////////////////////////////////////////////////////////////////
// Module: AddressToIndexMap.cs
//
// Notes:
// An open-addressed hash map from object address to node index, used while
// decoding a heap dump. It exists because `Dictionary<ulong, int>` is the
// single largest structure in that decode and is far more expensive than the
// data it holds.
//
// MEASURED, on a 12M-object heap. A Dictionary<ulong, int> stores each entry as
// { int hashCode; int next; ulong key; int value }, which pads to 24 bytes, and
// carries a separate int[] bucket array:
//
//     entries   16.8M x 24B  = 403MB
//     buckets   16.8M x  4B  =  67MB
//                              -----
//                              470MB
//
// and it reaches that size by DOUBLING, so the resize from 8.4M to 16.8M holds
// the old 235MB alive while allocating the new 403MB - a transient spike well
// past 600MB, copied entry by entry.
//
// This map stores the same information in two flat parallel arrays:
//
//     keys      16.8M x  8B  = 134MB
//     values    16.8M x  4B  =  67MB
//                              -----
//                              201MB
//
// It is sized ONCE from a count taken before any insertion, so it never
// resizes and never copies. That is roughly 270MB saved plus the spike
// removed, on one structure.
//
// WHY THIS IS SAFE TO HAND-ROLL HERE. The usual argument against a custom hash
// map is that the general-purpose one handles collisions, resizing, removal and
// arbitrary key types correctly and this will not. None of that applies: there
// are no removals, no resizes (capacity is known up front), and exactly one key
// type. What is left is linear probing over two arrays.
//
// Addresses are pointers, so their low bits are zero (objects are at least
// 8-byte aligned) and their high bits are near-constant within a process. A
// plain modulo of the raw value would collide catastrophically on both ends,
// which is why the hash below mixes before masking.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class AddressToIndexMap
{
    // Address 0 is never a real object, so it doubles as the empty marker and
    // saves a parallel occupancy bitmap.
    private const ulong EmptyKey = 0;

    private readonly ulong[] keys;
    private readonly int[] values;
    private readonly int capacityMask;

    private int count;

    public AddressToIndexMap(long expectedEntryCount)
    {
        // Load factor 0.75. Linear probing degrades sharply past that, and the
        // memory saved by running fuller is not worth the probe lengths on a
        // table this size.
        long requiredSlots = (long)(expectedEntryCount / 0.75) + 16;
        long capacity = 16;

        while (capacity < requiredSlots)
        {
            capacity *= 2;
        }

        this.keys = new ulong[capacity];
        this.values = new int[capacity];
        this.capacityMask = (int)(capacity - 1);
    }

    public int Count
    {
        get
        {
            return this.count;
        }
    }

    // Splitmix64's finalizer. Cheap (three shifts, two multiplies) and mixes
    // both the always-zero low alignment bits and the near-constant high bits
    // down into the slot index, which a raw mask would not.
    private static ulong Mix(ulong address)
    {
        ulong mixed = address;
        mixed ^= mixed >> 30;
        mixed *= 0xBF58476D1CE4E5B9UL;
        mixed ^= mixed >> 27;
        mixed *= 0x94D049BB133111EBUL;
        mixed ^= mixed >> 31;
        return mixed;
    }

    public bool TryGetValue(ulong address, out int index)
    {
        // Address 0 IS the empty marker, so it can never be a stored key -
        // and without this guard, looking it up would match the first empty
        // slot probed and cheerfully return that slot's zero value as a real
        // index. A null reference is not an object, so this is the honest
        // answer rather than a special case bolted on.
        if (address == EmptyKey)
        {
            index = -1;
            return false;
        }

        int slot = (int)(Mix(address) & (ulong)this.capacityMask);

        while (true)
        {
            ulong key = this.keys[slot];

            // Empty is tested FIRST so a miss stops at the first free slot
            // rather than depending on the key comparison above it.
            if (key == EmptyKey)
            {
                index = -1;
                return false;
            }

            if (key == address)
            {
                index = this.values[slot];
                return true;
            }

            slot = (slot + 1) & this.capacityMask;
        }
    }

    // Inserts, or returns the existing index if the address is already
    // present. One probe walk for both cases - a TryGetValue followed by an
    // Add would walk the same chain twice, and this is called once per node
    // and once per edge.
    public int GetOrAdd(ulong address, int indexIfAbsent)
    {
        // Storing address 0 would write the empty marker into a slot, leaving
        // it indistinguishable from a free one and silently truncating every
        // probe chain that runs through it. Callers never pass 0 (a null
        // reference is not an object), and this makes that a property of the
        // map rather than a rule every call site has to remember.
        if (address == EmptyKey)
        {
            return indexIfAbsent;
        }

        int slot = (int)(Mix(address) & (ulong)this.capacityMask);

        while (true)
        {
            ulong key = this.keys[slot];

            if (key == EmptyKey)
            {
                this.keys[slot] = address;
                this.values[slot] = indexIfAbsent;
                ++this.count;
                return indexIfAbsent;
            }

            if (key == address)
            {
                return this.values[slot];
            }

            slot = (slot + 1) & this.capacityMask;
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
