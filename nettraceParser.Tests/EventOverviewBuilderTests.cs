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

        EventTypeCount gcStart = overview.EventTypes.Find(e => e.EventId == 1);
        Assert.Equal(3, gcStart.Count);
        Assert.Equal("GCStart", gcStart.DisplayName);

        EventTypeCount exceptionThrown = overview.EventTypes.Find(e => e.EventId == 80);
        Assert.Equal(1, exceptionThrown.Count);
        Assert.Equal("ExceptionThrown", exceptionThrown.DisplayName);
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
