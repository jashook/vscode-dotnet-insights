////////////////////////////////////////////////////////////////////////////////
// Module: EventOverviewBuilderTests.cs
//
// Notes:
// Synthetic-record tests for Overview/EventOverviewBuilder.cs's counting and
// display-name resolution - see that file's own header comment for the
// friendly-name/fallback rules being verified here.
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.Overview;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventOverviewBuilderTests
{
    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";
    private const string ClrRundownProviderName = "Microsoft-Windows-DotNETRuntimeRundown";

    private static EventRecord MakeRecord(string providerName, string eventName, int eventId)
    {
        return new EventRecord(providerName, eventName, eventId, version: 1, timeStampRelativeQpc: 0, threadId: 0, stack: System.Array.Empty<long>(), fields: null, payloadBuffer: System.Array.Empty<byte>(), payloadOffset: 0, payloadLength: 0);
    }

    [Fact]
    public void Build_CountsTotalEventsAcrossEveryRecord()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRecord(ClrProviderName, "", 1),
            MakeRecord(ClrProviderName, "", 1),
            MakeRecord(ClrProviderName, "", 2),
        };

        EventOverview overview = EventOverviewBuilder.Build(events);

        Assert.Equal(3, overview.TotalEventCount);
    }

    [Fact]
    public void Build_GroupsByProviderAndEventIdWithCorrectCounts()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRecord(ClrProviderName, "", 1),
            MakeRecord(ClrProviderName, "", 1),
            MakeRecord(ClrProviderName, "", 1),
            MakeRecord(ClrProviderName, "", 80),
        };

        EventOverview overview = EventOverviewBuilder.Build(events);

        Assert.Equal(2, overview.EventTypes.Count);

        // Names are TraceEvent's own "Task/Opcode" form (see
        // ClrEventNames.cs) so they match what PerfView/dotnet-trace show
        // for the same event, rather than a naming scheme private to this
        // repo.
        EventTypeCount gcStart = overview.EventTypes.Find(e => e.EventId == 1);
        Assert.Equal(3, gcStart.Count);
        Assert.Equal("GC/Start", gcStart.DisplayName);

        EventTypeCount exceptionThrown = overview.EventTypes.Find(e => e.EventId == 80);
        Assert.Equal(1, exceptionThrown.Count);
        Assert.Equal("Exception/Start", exceptionThrown.DisplayName);
    }

    // The rundown provider is a genuinely different id space from the
    // runtime provider - id 144 is "Method/UnloadVerbose" on one and
    // "Method/DCStopVerbose" on the other. A single shared lookup table
    // would silently mislabel every rundown event in a real capture (where
    // rundown events are frequently the single highest-count rows).
    [Fact]
    public void Build_ResolvesRundownProviderIdsAgainstTheirOwnDistinctIdSpace()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRecord(ClrRundownProviderName, "", 144),
            MakeRecord(ClrProviderName, "", 144),
        };

        EventOverview overview = EventOverviewBuilder.Build(events);

        EventTypeCount rundownEntry = overview.EventTypes.Find(e => e.ProviderName == ClrRundownProviderName);
        EventTypeCount runtimeEntry = overview.EventTypes.Find(e => e.ProviderName == ClrProviderName);

        Assert.Equal("Method/DCStopVerbose", rundownEntry.DisplayName);
        Assert.Equal("Method/UnloadVerbose", runtimeEntry.DisplayName);
        Assert.NotEqual(rundownEntry.DisplayName, runtimeEntry.DisplayName);
    }

    // Spot-checks a spread of ids across the generated table (GC, loader,
    // exception-handling, tiered compilation) - these are exactly the rows
    // that dominated a real capture's Overview and previously rendered as
    // bare "EventID {n}".
    [Theory]
    [InlineData(10, "GC/AllocationTick")]
    [InlineData(30, "GC/SetGCHandle")]
    [InlineData(31, "GC/DestoryGCHandle")]
    [InlineData(203, "GC/Join")]
    [InlineData(204, "GC/PerHeapHistory")]
    [InlineData(205, "GC/GlobalHeapHistory")]
    [InlineData(250, "ExceptionCatch/Start")]
    [InlineData(256, "Exception/Stop")]
    [InlineData(143, "Method/LoadVerbose")]
    [InlineData(280, "TieredCompilation/Settings")]
    public void Build_ResolvesRealRuntimeEventIdsSeenInProductionCaptures(int eventId, string expectedName)
    {
        EventOverview overview = EventOverviewBuilder.Build(new List<EventRecord> { MakeRecord(ClrProviderName, "", eventId) });

        Assert.Equal(expectedName, overview.EventTypes[0].DisplayName);
    }

    [Theory]
    [InlineData(150, "Method/ILToNativeMapDCStop")]
    [InlineData(152, "Loader/DomainModuleDCStop")]
    [InlineData(154, "Loader/ModuleDCStop")]
    [InlineData(156, "Loader/AssemblyDCStop")]
    [InlineData(158, "Loader/AppDomainDCStop")]
    public void Build_ResolvesRealRundownEventIdsSeenInProductionCaptures(int eventId, string expectedName)
    {
        EventOverview overview = EventOverviewBuilder.Build(new List<EventRecord> { MakeRecord(ClrRundownProviderName, "", eventId) });

        Assert.Equal(expectedName, overview.EventTypes[0].DisplayName);
    }

    [Fact]
    public void Build_FallsBackToEventIdLabelForUnknownClrEvents()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRecord(ClrProviderName, "", 999),
        };

        EventOverview overview = EventOverviewBuilder.Build(events);

        Assert.Equal("EventID 999", overview.EventTypes[0].DisplayName);
    }

    [Fact]
    public void Build_PrefersRealEventNameOverFriendlyLookupOrFallback()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRecord("Microsoft-DotNETCore-EventPipe", "ProcessInfo", 1),
        };

        EventOverview overview = EventOverviewBuilder.Build(events);

        Assert.Equal("ProcessInfo", overview.EventTypes[0].DisplayName);
    }

    [Fact]
    public void Build_SortsByCountDescending()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRecord(ClrProviderName, "", 1),
            MakeRecord(ClrProviderName, "", 2),
            MakeRecord(ClrProviderName, "", 2),
            MakeRecord(ClrProviderName, "", 2),
            MakeRecord(ClrProviderName, "", 80),
            MakeRecord(ClrProviderName, "", 80),
        };

        EventOverview overview = EventOverviewBuilder.Build(events);

        Assert.Equal(3, overview.EventTypes[0].Count);
        Assert.Equal(2, overview.EventTypes[1].Count);
        Assert.Equal(1, overview.EventTypes[2].Count);
    }

    [Fact]
    public void Build_HandlesEmptyEventList()
    {
        EventOverview overview = EventOverviewBuilder.Build(new List<EventRecord>());

        Assert.Equal(0, overview.TotalEventCount);
        Assert.Empty(overview.EventTypes);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
