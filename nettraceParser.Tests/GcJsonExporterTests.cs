////////////////////////////////////////////////////////////////////////////////
// Module: GcJsonExporterTests.cs
//
// Notes:
// Verifies that GcJsonExporter.WriteToFile produces JSON entries that include
// the PinnedObjectCount and GlobalMechanisms fields added alongside the
// fragmentation-percentage chart feature, and that both are serialized with
// the correct types (integer, not string).
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class GcJsonExporterTests
{
    private static GcEvent MakeCompletedGcEvent(int id, int pinnedObjectCount, GCGlobalMechanisms globalMechanisms)
    {
        GcEvent gcEvent = new GcEvent();
        gcEvent.Id = id;
        gcEvent.Generation = 0;
        gcEvent.Reason = GCReason.AllocSmall;
        gcEvent.HasEnd = true;
        gcEvent.HasHeapStats = true;
        gcEvent.HasGlobalHeapHistory = true;
        gcEvent.PinnedObjectCount = pinnedObjectCount;
        gcEvent.GlobalMechanisms = globalMechanisms;
        gcEvent.Timestamp = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
        return gcEvent;
    }

    private static JsonObject WriteAndParse(List<GcEvent> gcEvents)
    {
        string outputPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        // ticks is now a binary sidecar file (see AllocationJsonExporter.cs's
        // WriteTicks) - this file's tests don't assert on ticks directly
        // (allocationEvents is always empty here), so the temp file just
        // needs a valid path to write to and cleanup.
        string ticksBinaryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".bin");

        try
        {
            GcJsonExporter.WriteToFile(outputPath, gcEvents, new List<AllocationEvent>(), new List<ExceptionEvent>(), new EventOverview(0, new List<EventTypeCount>()), MethodSymbolTable.Build(new List<EventRecord>(), 8, 0, 0), processName: "test-process", ticksBinaryPath);
            string json = File.ReadAllText(outputPath);
            return (JsonObject)JsonNode.Parse(json);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            if (File.Exists(ticksBinaryPath))
            {
                File.Delete(ticksBinaryPath);
            }
        }
    }

    [Fact]
    public void WriteToFile_IncludesPinnedObjectCountFieldInGcDataEntry()
    {
        List<GcEvent> gcEvents = new List<GcEvent>
        {
            MakeCompletedGcEvent(id: 1, pinnedObjectCount: 42, globalMechanisms: GCGlobalMechanisms.Concurrent)
        };

        JsonObject root = WriteAndParse(gcEvents);

        JsonArray gcData = (JsonArray)root["gcData"];
        JsonObject entry = (JsonObject)gcData[0];
        JsonObject data = (JsonObject)entry["data"];

        Assert.NotNull(data["PinnedObjectCount"]);
        Assert.Equal(42, (int)data["PinnedObjectCount"]);
    }

    [Fact]
    public void WriteToFile_IncludesGlobalMechanismsAsIntegerInGcDataEntry()
    {
        GCGlobalMechanisms mechanisms = GCGlobalMechanisms.Concurrent | GCGlobalMechanisms.Compaction;

        List<GcEvent> gcEvents = new List<GcEvent>
        {
            MakeCompletedGcEvent(id: 1, pinnedObjectCount: 0, globalMechanisms: mechanisms)
        };

        JsonObject root = WriteAndParse(gcEvents);

        JsonArray gcData = (JsonArray)root["gcData"];
        JsonObject entry = (JsonObject)gcData[0];
        JsonObject data = (JsonObject)entry["data"];

        Assert.NotNull(data["GlobalMechanisms"]);
        int serializedValue = (int)data["GlobalMechanisms"];
        Assert.Equal((int)mechanisms, serializedValue);
        // Compaction bit (0x2) must survive the round-trip.
        Assert.True((serializedValue & (int)GCGlobalMechanisms.Compaction) != 0);
    }

    [Fact]
    public void WriteToFile_BothNewFieldsDefaultToZeroWhenNotExplicitlySet()
    {
        GcEvent gcEvent = new GcEvent();
        gcEvent.Id = 1;
        gcEvent.Generation = 0;
        gcEvent.Reason = GCReason.AllocSmall;
        gcEvent.HasEnd = true;
        gcEvent.Timestamp = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
        // PinnedObjectCount and GlobalMechanisms intentionally left at default (0).

        JsonObject root = WriteAndParse(new List<GcEvent> { gcEvent });

        JsonArray gcData = (JsonArray)root["gcData"];
        JsonObject data = (JsonObject)((JsonObject)gcData[0])["data"];

        Assert.Equal(0, (int)data["PinnedObjectCount"]);
        Assert.Equal(0, (int)data["GlobalMechanisms"]);
    }

    [Fact]
    public void WriteToFile_SerializesMultipleGcEntriesEachWithOwnPinnedCount()
    {
        List<GcEvent> gcEvents = new List<GcEvent>
        {
            MakeCompletedGcEvent(id: 1, pinnedObjectCount: 10, globalMechanisms: GCGlobalMechanisms.None),
            MakeCompletedGcEvent(id: 2, pinnedObjectCount: 25, globalMechanisms: GCGlobalMechanisms.Compaction)
        };

        JsonObject root = WriteAndParse(gcEvents);

        JsonArray gcData = (JsonArray)root["gcData"];
        Assert.Equal(2, gcData.Count);
        Assert.Equal(10, (int)((JsonObject)((JsonObject)gcData[0])["data"])["PinnedObjectCount"]);
        Assert.Equal(25, (int)((JsonObject)((JsonObject)gcData[1])["data"])["PinnedObjectCount"]);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
