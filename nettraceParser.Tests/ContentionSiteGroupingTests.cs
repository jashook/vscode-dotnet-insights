////////////////////////////////////////////////////////////////////////////////
// Module: ContentionSiteGroupingTests.cs
//
// Notes:
// Pins how the Contention view's ranked "sites" table attributes a wait.
//
// The rule is deliberately NOT "group by the stack's leaf frame", which is
// what every other drill-down in this codebase does (allocation, exception)
// and what this exporter originally did. Contention stacks are structurally
// different: an allocation stack bottoms out in the method that allocated
// and an exception stack in the method that threw, but EVERY contention
// stack bottoms out in the same generic runtime lock primitive. Grouping by
// leaf therefore collapsed the whole table - measured on a real capture,
// 10,126 of 10,533 contentions (96%) landed on a single
// "System.Threading.Monitor.Enter_Slowpath" row, which is true and tells you
// nothing about which lock or where. These tests pin the replacement (first
// frame below the primitives) so a well-meaning "make contention consistent
// with the other drill-downs" change can't silently reintroduce it.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ContentionSiteGroupingTests
{
    private const int PointerSize = 8;

    private static byte[] MakeMethodDCStartVerbosePayload(long methodId, long moduleId, long methodStartAddress, int methodSize, string methodName)
    {
        return new PayloadBuilder()
            .WriteAddress(methodId, 8)
            .WriteAddress(moduleId, 8)
            .WriteAddress(methodStartAddress, 8)
            .WriteInt32(methodSize)
            .WriteInt32(0x06000001)
            .WriteInt32(0)
            .WriteUnicodeString("")
            .WriteUnicodeString(methodName)
            .WriteUnicodeString("sig")
            .ToArray();
    }

    private static EventRecord MakeRundownEvent(long methodId, long startAddress, int size, string name)
    {
        byte[] payload = MakeMethodDCStartVerbosePayload(methodId, moduleId: 2, startAddress, size, name);

        return new EventRecord("Microsoft-Windows-DotNETRuntimeRundown", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stack: Array.Empty<long>(), fields: null, payload, payloadOffset: 0, payload.Length);
    }

    // 0x1000/0x2000 are lock primitives (skipped when attributing a site),
    // 0x3000/0x4000/0x5000 are real application frames.
    private static MethodSymbolTable MakeSymbolTable()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRundownEvent(methodId: 1, startAddress: 0x1000, size: 0x100, name: "System.Threading.Monitor.Enter_Slowpath"),
            MakeRundownEvent(methodId: 2, startAddress: 0x2000, size: 0x100, name: "System.Threading.Monitor.Enter"),
            MakeRundownEvent(methodId: 3, startAddress: 0x3000, size: 0x100, name: "MyApp.CacheLookup"),
            MakeRundownEvent(methodId: 4, startAddress: 0x4000, size: 0x100, name: "MyApp.WriteBuffer"),
            MakeRundownEvent(methodId: 5, startAddress: 0x5000, size: 0x100, name: "MyApp.RequestHandler"),
        };

        return MethodSymbolTable.Build(events, pointerSize: PointerSize, qpcFrequency: 0, referenceQpc: 0);
    }

    private static ContentionEvent MakeEvent(double relativeMSec, double durationMSec, long[] stack, long threadId)
    {
        return new ContentionEvent(relativeMSec, durationMSec, ClrContentionFlags.Managed, threadId, stack, lockId: 0xAA, associatedObjectId: 0, ownerThreadId: 0);
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
    public void Write_SitesAreAttributedBelowTheLockPrimitiveNotToTheLeafFrame()
    {
        // Two waits sharing the same primitive leaf but different real
        // callers must rank as TWO sites, not collapse into one row named
        // after the primitive.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 5.0, new long[] { 0x1000, 0x3000, 0x5000 }, threadId: 1),
            MakeEvent(20.0, 7.0, new long[] { 0x1000, 0x4000, 0x5000 }, threadId: 2),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement topSites = document.RootElement.GetProperty("topSites");

        Assert.Equal(2, topSites.GetArrayLength());

        List<string> siteNames = new List<string>();
        foreach (JsonElement site in topSites.EnumerateArray())
        {
            siteNames.Add(site.GetProperty("SiteName").GetString());
        }

        Assert.Contains("MyApp.WriteBuffer", siteNames);
        Assert.Contains("MyApp.CacheLookup", siteNames);
        Assert.DoesNotContain("System.Threading.Monitor.Enter_Slowpath", siteNames);
    }

    [Fact]
    public void Write_ConsecutiveLockPrimitiveFramesAreAllSkipped()
    {
        // A real stack can carry several primitive frames before reaching
        // application code (Enter -> Enter_Slowpath -> ...), so skipping
        // only the first one would still name the site after a primitive.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 5.0, new long[] { 0x1000, 0x2000, 0x3000 }, threadId: 1),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement topSites = document.RootElement.GetProperty("topSites");

        Assert.Equal("MyApp.CacheLookup", topSites[0].GetProperty("SiteName").GetString());
    }

    [Fact]
    public void Write_StackOfNothingButPrimitivesFallsBackToLeafRatherThanBeingDropped()
    {
        // Nothing in the stack is application code. The wait is still real
        // and must still be attributed somewhere - losing it would make the
        // ranked table disagree with the capture's own total.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 5.0, new long[] { 0x1000, 0x2000 }, threadId: 1),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("topSites").GetArrayLength());
        Assert.Equal("System.Threading.Monitor.Enter_Slowpath", root.GetProperty("topSites")[0].GetProperty("SiteName").GetString());
        Assert.Equal(5.0, root.GetProperty("topSites")[0].GetProperty("TotalWaitMSec").GetDouble(), 3);
    }

    [Fact]
    public void Write_RankedSiteWaitStillSumsToTheCaptureTotal()
    {
        // Regrouping must move waits between rows, never lose or duplicate
        // them - the whole point is a better breakdown of the SAME total.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 5.0, new long[] { 0x1000, 0x3000 }, threadId: 1),
            MakeEvent(20.0, 7.0, new long[] { 0x1000, 0x4000 }, threadId: 2),
            MakeEvent(30.0, 3.0, new long[] { 0x1000, 0x2000 }, threadId: 3),
            MakeEvent(40.0, 1.0, Array.Empty<long>(), threadId: 4),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement root = document.RootElement;

        double summedSiteWait = 0;
        foreach (JsonElement site in root.GetProperty("topSites").EnumerateArray())
        {
            summedSiteWait += site.GetProperty("TotalWaitMSec").GetDouble();
        }

        Assert.Equal(root.GetProperty("totalContentionWaitMSec").GetDouble(), summedSiteWait, 3);
        Assert.Equal(16.0, summedSiteWait, 3);
    }

    [Fact]
    public void Write_SiteDrillDownTreeStartsAtTheSiteNotAtTheLockPrimitive()
    {
        // The tree's first row should be the frame the site row names -
        // replaying the shared primitive prefix would put an identical,
        // uninformative row at the top of every tree.
        List<ContentionEvent> events = new List<ContentionEvent>
        {
            MakeEvent(10.0, 5.0, new long[] { 0x1000, 0x3000, 0x5000 }, threadId: 1),
        };

        JsonDocument document = WriteAndParse(events);
        JsonElement root = document.RootElement;

        JsonElement firstChild = root.GetProperty("siteDrillDown")[0].GetProperty("children")[0];
        string frameName = root.GetProperty("methodNames")[firstChild.GetProperty("frame").GetInt32()].GetString();

        Assert.Equal("MyApp.CacheLookup", frameName);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
