////////////////////////////////////////////////////////////////////////////////
// Module: AddressToIndexMapTests.cs
//
// Notes:
// Covers the hand-rolled open-addressed map the heap-dump decoder uses in
// place of Dictionary<ulong, int> (see AddressToIndexMap.cs for why).
//
// A hand-rolled hash map is exactly the kind of thing that works on the data
// it was written against and fails on a collision pattern nobody tried, so
// these deliberately push on the parts a happy-path test would miss: forced
// collisions, a table filled to its stated load factor, and the sentinel
// value the empty-slot marker is built on.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.GcDump;

using Xunit;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class AddressToIndexMapTests
{
    [Fact]
    public void GetOrAdd_ReturnsTheAssignedIndexAndThenTheExistingOne()
    {
        AddressToIndexMap map = new AddressToIndexMap(16);

        Assert.Equal(0, map.GetOrAdd(0x1000, 0));
        Assert.Equal(1, map.GetOrAdd(0x2000, 1));

        // A second insert of the same address must return the ORIGINAL index
        // and not consume a new one - the decoder relies on this to notice an
        // address defined twice.
        Assert.Equal(0, map.GetOrAdd(0x1000, 99));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void TryGetValue_DistinguishesPresentFromAbsent()
    {
        AddressToIndexMap map = new AddressToIndexMap(16);
        map.GetOrAdd(0x1000, 7);

        int index;
        Assert.True(map.TryGetValue(0x1000, out index));
        Assert.Equal(7, index);

        Assert.False(map.TryGetValue(0x2000, out index));
        Assert.Equal(-1, index);
    }

    // Real heap addresses are 8- or 16-byte aligned, so their low bits are
    // always zero and a table that masked the raw value would pile every entry
    // into a fraction of its slots. This is the pattern that motivated hashing
    // at all, so it gets its own test.
    [Fact]
    public void GetOrAdd_HandlesAlignedAddressesWithoutLosingEntries()
    {
        const int entryCount = 5000;
        AddressToIndexMap map = new AddressToIndexMap(entryCount);

        for (int index = 0; index < entryCount; ++index)
        {
            ulong address = 0x7F0000000000UL + ((ulong)index * 32);
            Assert.Equal(index, map.GetOrAdd(address, index));
        }

        Assert.Equal(entryCount, map.Count);

        for (int index = 0; index < entryCount; ++index)
        {
            ulong address = 0x7F0000000000UL + ((ulong)index * 32);
            int found;
            Assert.True(map.TryGetValue(address, out found));
            Assert.Equal(index, found);
        }
    }

    // Fills to the load factor the constructor sizes for. Linear probing walks
    // ever-longer chains as a table fills, so this pins that a full table still
    // resolves every key rather than looping forever or overwriting.
    [Fact]
    public void GetOrAdd_StillResolvesEveryKeyWhenFilledToCapacity()
    {
        const int entryCount = 3000;
        AddressToIndexMap map = new AddressToIndexMap(entryCount);

        Dictionary<ulong, int> expected = new Dictionary<ulong, int>();

        for (int index = 0; index < entryCount; ++index)
        {
            // Deliberately clustered: consecutive addresses in a handful of
            // widely separated regions, which is what a real heap looks like
            // and what defeats a naive hash.
            ulong address = (0x10000000UL * (ulong)((index % 4) + 1)) + ((ulong)index * 8);
            expected[address] = index;
            map.GetOrAdd(address, index);
        }

        Assert.Equal(expected.Count, map.Count);

        foreach (KeyValuePair<ulong, int> entry in expected)
        {
            int found;
            Assert.True(map.TryGetValue(entry.Key, out found), $"address 0x{entry.Key:x} went missing");
            Assert.Equal(entry.Value, found);
        }
    }

    // Address 0 doubles as the empty-slot marker, which is only safe because a
    // null reference is never a heap object. The decoder never inserts it, and
    // this pins that looking it up reports absent rather than matching every
    // empty slot it happens to probe.
    [Fact]
    public void TryGetValue_TreatsZeroAsAbsentBecauseItIsTheEmptyMarker()
    {
        AddressToIndexMap map = new AddressToIndexMap(16);
        map.GetOrAdd(0x1000, 0);

        int index;
        Assert.False(map.TryGetValue(0, out index));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
