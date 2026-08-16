////////////////////////////////////////////////////////////////////////////////
// Module: PerfPrimitivesTests.cs
//
// Notes:
// Covers the three array-backed primitives that replaced hashed lookups on
// per-sample/per-event paths (Cpu/FrameIdSet.cs, Cpu/FrameIdTable.cs,
// Utf16StringPool.cs), plus EventOverview.CountForEvent. The interesting
// cases are all structural rather than numeric: the symbol table's id space
// is TWO dense ranges (see MethodSymbolTable.UnresolvedIdBase), every one of
// these types grows its backing array on demand, and Utf16StringPool is open
// addressed so collisions and load-factor growth have to be exercised, not
// assumed.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class PerfPrimitivesTests
{
    [Fact]
    public void FrameIdSet_AddReportsOnlyFirstOccurrenceWithinOneSet()
    {
        FrameIdSet set = new FrameIdSet();
        set.StartNewSet();

        Assert.True(set.Add(7));
        Assert.False(set.Add(7));
        Assert.True(set.Add(9));
        Assert.False(set.Add(9));
    }

    [Fact]
    public void FrameIdSet_StartNewSetForgetsEverythingAdded()
    {
        FrameIdSet set = new FrameIdSet();
        set.StartNewSet();
        set.Add(7);

        set.StartNewSet();

        Assert.True(set.Add(7));
    }

    [Fact]
    public void FrameIdSet_SeparatesResolvedAndUnresolvedIdRanges()
    {
        FrameIdSet set = new FrameIdSet();
        set.StartNewSet();

        // Same array INDEX in the two ranges, different frame ids - a single
        // array indexed by raw id would either collide here or need 4GB.
        Assert.True(set.Add(5));
        Assert.True(set.Add(MethodSymbolTable.UnresolvedIdBase + 5));
        Assert.False(set.Add(MethodSymbolTable.UnresolvedIdBase + 5));
    }

    [Fact]
    public void FrameIdSet_GrowsBeyondInitialCapacity()
    {
        FrameIdSet set = new FrameIdSet();
        set.StartNewSet();

        for (int frameId = 0; frameId < 5000; ++frameId)
        {
            Assert.True(set.Add(frameId));
        }

        for (int frameId = 0; frameId < 5000; ++frameId)
        {
            Assert.False(set.Add(frameId));
        }
    }

    [Fact]
    public void FrameIdTable_GetReturnsDefaultForUnsetIds()
    {
        FrameIdTable<string> table = new FrameIdTable<string>();

        Assert.Null(table.Get(0));
        Assert.Null(table.Get(123456));
        Assert.Null(table.Get(MethodSymbolTable.UnresolvedIdBase));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void FrameIdTable_RoundTripsBothIdRangesAndTracksKeysInOrder()
    {
        FrameIdTable<string> table = new FrameIdTable<string>();
        table.Set(4, "resolved");
        table.Set(MethodSymbolTable.UnresolvedIdBase + 4, "unresolved");

        Assert.Equal("resolved", table.Get(4));
        Assert.Equal("unresolved", table.Get(MethodSymbolTable.UnresolvedIdBase + 4));

        List<int> keys = table.Keys;
        Assert.Equal(2, keys.Count);
        Assert.Equal(4, keys[0]);
        Assert.Equal(MethodSymbolTable.UnresolvedIdBase + 4, keys[1]);
    }

    [Fact]
    public void FrameIdTable_GrowsBeyondInitialCapacityWithoutLosingEarlierValues()
    {
        FrameIdTable<string> table = new FrameIdTable<string>();

        for (int frameId = 0; frameId < 4000; frameId += 250)
        {
            table.Set(frameId, frameId.ToString());
        }

        for (int frameId = 0; frameId < 4000; frameId += 250)
        {
            Assert.Equal(frameId.ToString(), table.Get(frameId));
        }
    }

    [Fact]
    public void Utf16StringPool_ReturnsSameInstanceForEqualContent()
    {
        Utf16StringPool pool = new Utf16StringPool();

        string first = pool.GetOrAdd("System.InvalidOperationException".AsSpan());
        string second = pool.GetOrAdd("System.InvalidOperationException".AsSpan());

        Assert.Equal("System.InvalidOperationException", first);
        Assert.Same(first, second);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Utf16StringPool_KeepsDistinctContentsApart()
    {
        Utf16StringPool pool = new Utf16StringPool();

        string first = pool.GetOrAdd("System.OperationCanceledException".AsSpan());
        string second = pool.GetOrAdd("System.TimeoutException".AsSpan());

        Assert.Equal("System.OperationCanceledException", first);
        Assert.Equal("System.TimeoutException", second);
        Assert.Equal(2, pool.Count);
    }

    [Fact]
    public void Utf16StringPool_MapsEmptySpanToEmptyStringWithoutStoringIt()
    {
        Utf16StringPool pool = new Utf16StringPool();

        Assert.Same(string.Empty, pool.GetOrAdd(ReadOnlySpan<char>.Empty));
        Assert.Equal(0, pool.Count);
    }

    [Fact]
    public void Utf16StringPool_GrowsPastInitialCapacityAndStillDeduplicates()
    {
        Utf16StringPool pool = new Utf16StringPool();
        List<string> firstPass = new List<string>();

        // Well past the initial 256-slot table's own load-factor growth
        // point, so this exercises Grow's rehash rather than just the
        // straight-line insert path.
        for (int nameIndex = 0; nameIndex < 1000; ++nameIndex)
        {
            firstPass.Add(pool.GetOrAdd($"Some.Namespace.Type{nameIndex}Exception".AsSpan()));
        }

        Assert.Equal(1000, pool.Count);

        for (int nameIndex = 0; nameIndex < 1000; ++nameIndex)
        {
            Assert.Same(firstPass[nameIndex], pool.GetOrAdd($"Some.Namespace.Type{nameIndex}Exception".AsSpan()));
        }

        Assert.Equal(1000, pool.Count);
    }

    [Fact]
    public void Utf16StringPool_MatchesOnFullContentNotPrefix()
    {
        Utf16StringPool pool = new Utf16StringPool();

        string shorter = pool.GetOrAdd("System.Exception".AsSpan());
        string longer = pool.GetOrAdd("System.ExceptionExtra".AsSpan());

        Assert.NotSame(shorter, longer);
        Assert.Equal("System.Exception", shorter);
        Assert.Equal("System.ExceptionExtra", longer);
    }

    [Fact]
    public void EventOverview_CountForEventFindsAMatchAndReportsZeroForAnythingElse()
    {
        List<EventTypeCount> eventTypes = new List<EventTypeCount>();
        eventTypes.Add(new EventTypeCount("Microsoft-DotNETCore-SampleProfiler", "ThreadSample", 0, 16244062));
        eventTypes.Add(new EventTypeCount("Microsoft-Windows-DotNETRuntime", "GCStart", 1, 69));

        EventOverview overview = new EventOverview(16244131, eventTypes);

        Assert.Equal(16244062, overview.CountForEvent("Microsoft-DotNETCore-SampleProfiler", 0));
        Assert.Equal(69, overview.CountForEvent("Microsoft-Windows-DotNETRuntime", 1));

        // Right provider, wrong id / right id, wrong provider - both must miss.
        Assert.Equal(0, overview.CountForEvent("Microsoft-DotNETCore-SampleProfiler", 1));
        Assert.Equal(0, overview.CountForEvent("Microsoft-Windows-DotNETRuntime", 0));
        Assert.Equal(0, overview.CountForEvent("Some-Other-Provider", 0));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
