////////////////////////////////////////////////////////////////////////////////
// Module: GcJsonExporter.cs
//
// Notes:
// Serializes a List<GcEvent> into exactly the JSON shape the VS Code
// extension's DotnetInsightsGcSnapshotEditor.gcDataFromXml already produces
// (dotnetInsights/src/DotnetInsightsGcSnapshotEditor.ts) - field names and
// nesting copied from that function directly so the extension's existing
// chart-rendering code needs no changes to consume nettrace-derived data.
//
// Uses System.Text.Json.Nodes (BCL, no new dependency) rather than hand-built
// interpolated strings like HelperClasses.cs's ToJsonString() methods -
// deliberate for this one case, since the nesting here (Heaps -> Generations
// -> per-field) is deep enough that a string-building bug would be easy to
// introduce and hard to spot on the consuming (TypeScript) side.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Gc {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcJsonExporter
{
    public static void WriteToFile(string outputPath, List<GcEvent> gcEvents, string processName)
    {
        JsonArray gcDataArray = new JsonArray();

        for (int gcIndex = 0; gcIndex < gcEvents.Count; ++gcIndex)
        {
            GcEvent gcEvent = gcEvents[gcIndex];
            string reasonName = gcEvent.Reason.ToString();

            JsonObject data = new JsonObject();
            data["Gen0MinSize"] = gcEvent.FinalYoungestDesired;
            data["generation"] = gcEvent.Generation;
            data["GenerationSize0"] = gcEvent.GenerationSize0;
            data["GenerationSize1"] = gcEvent.GenerationSize1;
            data["GenerationSize2"] = gcEvent.GenerationSize2;
            data["GenerationSizeLOH"] = gcEvent.GenerationSize3;
            data["Id"] = gcEvent.Id;
            // ISO-8601 (round-trip format) - directly parseable by JS's `new Date(...)`.
            // Converted to the machine's local timezone (gcEvent.Timestamp is UTC) so the
            // offset in the string reflects wall-clock time here, not UTC.
            data["DateTime"] = gcEvent.Timestamp.ToLocalTime().ToString("o");
            data["kind"] = reasonName;
            data["NumHeaps"] = gcEvent.NumHeaps;
            data["PauseDurationMSec"] = gcEvent.PauseDurationMSec;
            data["PauseEndRelativeMSec"] = gcEvent.PauseEndRelativeMSec;
            data["PauseStartRelativeMSec"] = gcEvent.PauseStartRelativeMSec;
            data["Reason"] = reasonName;
            data["TotalHeapSize"] = gcEvent.TotalHeapSize;
            data["TotalPromoted"] = gcEvent.TotalPromotedSize0;
            data["TotalPromotedLOH"] = gcEvent.TotalPromotedSize3;
            data["TotalPromotedSize0"] = gcEvent.TotalPromotedSize0;
            data["TotalPromotedSize1"] = gcEvent.TotalPromotedSize1;
            data["TotalPromotedSize2"] = gcEvent.TotalPromotedSize2;
            // Matches an existing quirk in gcDataFromXml where Type reuses the
            // Reason text rather than a true concurrent/non-concurrent label -
            // kept for consistency with what the renderer already expects.
            data["Type"] = reasonName;
            data["GCDurationMSec"] = gcEvent.PauseDurationMSec;

            JsonArray heapsArray = new JsonArray();
            for (int heapIndex = 0; heapIndex < gcEvent.Heaps.Count; ++heapIndex)
            {
                ClrGcHeap heap = gcEvent.Heaps[heapIndex];
                JsonObject generationsObject = new JsonObject();

                for (int genIndex = 0; genIndex < heap.Generations.Length; ++genIndex)
                {
                    ClrGcGeneration gen = heap.Generations[genIndex];

                    JsonObject genObject = new JsonObject();
                    genObject["Fragmentation"] = gen.Fragmentation;
                    genObject["FreeListSpaceAfter"] = gen.FreeListSpaceAfter;
                    genObject["FreeListSpaceBefore"] = gen.FreeListSpaceBefore;
                    genObject["FreeObjSpaceAfter"] = gen.FreeObjSpaceAfter;
                    genObject["FreeObjSpaceBefore"] = gen.FreeObjSpaceBefore;
                    genObject["Id"] = genIndex;
                    genObject["In"] = gen.In;
                    genObject["NewAllocation"] = gen.NewAllocation;
                    genObject["NonePinnedSurv"] = gen.NonePinnedSurv;
                    genObject["ObjSizeAfter"] = gen.ObjSizeAfter;
                    genObject["ObjSpaceBefore"] = gen.ObjSpaceBefore;
                    genObject["Out"] = gen.Out;
                    genObject["PinnedSurv"] = gen.PinnedSurv;
                    genObject["SizeAfter"] = gen.SizeAfter;
                    genObject["SizeBefore"] = gen.SizeBefore;
                    genObject["SurvRate"] = gen.SurvRate;

                    generationsObject[genIndex.ToString()] = genObject;
                }

                JsonObject heapObject = new JsonObject();
                heapObject["Generations"] = generationsObject;
                heapsArray.Add(heapObject);
            }

            data["Heaps"] = heapsArray;

            JsonObject entry = new JsonObject();
            entry["data"] = data;
            gcDataArray.Add(entry);
        }

        JsonObject root = new JsonObject();
        root["processName"] = processName;
        root["allocations"] = new JsonArray();
        root["gcData"] = gcDataArray;

        File.WriteAllText(outputPath, root.ToJsonString());
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Gc)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
