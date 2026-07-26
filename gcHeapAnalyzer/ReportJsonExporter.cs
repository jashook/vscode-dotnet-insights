////////////////////////////////////////////////////////////////////////////////
// Module: ReportJsonExporter.cs
//
// Notes:
// Serializes a FragmentationReport to JSON using System.Text.Json.Nodes,
// following the same pattern as GcJsonExporter.cs in nettraceParser.
//
// JSON shape (all byte fields are raw bytes, not scaled):
//   {
//     processId, processName, captureTimeUtc,
//     summary: { totalCommittedBytes, totalObjectBytes, totalFreeBytes,
//                fragmentationPct, pinnedObjectCount, segmentCount },
//     generations: [
//       { generation, label, committedBytes, objectBytes, freeBytes,
//         fragmentationPct, segmentCount, freeChunkCount }   // indices 0-4
//     ],
//     freeChunks: {
//       totalCount, totalFreeBytes,
//       histogram: [{ label, minBytes, maxBytes, count, totalBytes }],
//       largeChunks: [{ address, sizeBytes, generation }]
//     },
//     pinnedObjects: [{ typeName, generation, count, totalBytes }],
//     topLohTypes:   [{ typeName, count, totalBytes }]
//   }
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.GcHeapAnalyzer {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;
using System.Text.Json.Nodes;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ReportJsonExporter
{
    public static string ToJson(FragmentationReport report)
    {
        JsonObject root = new JsonObject();
        root["processId"] = report.ProcessId;
        root["processName"] = report.ProcessName;
        root["captureTimeUtc"] = report.CaptureTimeUtc;

        root["summary"] = SerializeSummary(report.Summary);
        root["generations"] = SerializeGenerations(report.Generations);
        root["freeChunks"] = SerializeFreeChunks(report.FreeChunks);
        root["pinnedObjects"] = SerializePinnedObjects(report.PinnedObjects);
        root["topLohTypes"] = SerializeLohTypes(report.TopLohTypes);

        return root.ToJsonString();
    }

    private static JsonObject SerializeSummary(HeapSummary summary)
    {
        JsonObject obj = new JsonObject();
        obj["totalCommittedBytes"] = summary.TotalCommittedBytes;
        obj["totalObjectBytes"] = summary.TotalObjectBytes;
        obj["totalFreeBytes"] = summary.TotalFreeBytes;
        obj["fragmentationPct"] = summary.FragmentationPct;
        obj["pinnedObjectCount"] = summary.PinnedObjectCount;
        obj["segmentCount"] = summary.SegmentCount;
        return obj;
    }

    private static JsonArray SerializeGenerations(GenerationStats[] generations)
    {
        JsonArray arr = new JsonArray();

        for (int genIndex = 0; genIndex < generations.Length; ++genIndex)
        {
            GenerationStats gen = generations[genIndex];
            JsonObject obj = new JsonObject();
            obj["generation"] = gen.Generation;
            obj["label"] = gen.Label;
            obj["committedBytes"] = gen.CommittedBytes;
            obj["objectBytes"] = gen.ObjectBytes;
            obj["freeBytes"] = gen.FreeBytes;
            obj["fragmentationPct"] = gen.FragmentationPct;
            obj["segmentCount"] = gen.SegmentCount;
            obj["freeChunkCount"] = gen.FreeChunkCount;
            arr.Add(obj);
        }

        return arr;
    }

    private static JsonObject SerializeFreeChunks(FreeChunkReport freeChunks)
    {
        JsonObject obj = new JsonObject();
        obj["totalCount"] = freeChunks.TotalCount;
        obj["totalFreeBytes"] = freeChunks.TotalFreeBytes;

        JsonArray histogramArray = new JsonArray();
        for (int bucketIndex = 0; bucketIndex < freeChunks.Histogram.Length; ++bucketIndex)
        {
            FreeChunkBucket bucket = freeChunks.Histogram[bucketIndex];
            JsonObject bucketObj = new JsonObject();
            bucketObj["label"] = bucket.Label;
            bucketObj["minBytes"] = bucket.MinBytes;
            bucketObj["maxBytes"] = bucket.MaxBytes == long.MaxValue ? -1 : bucket.MaxBytes;
            bucketObj["count"] = bucket.Count;
            bucketObj["totalBytes"] = bucket.TotalBytes;
            histogramArray.Add(bucketObj);
        }

        obj["histogram"] = histogramArray;

        JsonArray largeChunksArray = new JsonArray();
        for (int chunkIndex = 0; chunkIndex < freeChunks.LargeChunks.Count; ++chunkIndex)
        {
            LargeFreeChunk chunk = freeChunks.LargeChunks[chunkIndex];
            JsonObject chunkObj = new JsonObject();
            chunkObj["address"] = chunk.Address;
            chunkObj["sizeBytes"] = chunk.SizeBytes;
            chunkObj["generation"] = chunk.Generation;
            largeChunksArray.Add(chunkObj);
        }

        obj["largeChunks"] = largeChunksArray;
        return obj;
    }

    private static JsonArray SerializePinnedObjects(List<PinnedTypeStat> pinnedObjects)
    {
        JsonArray arr = new JsonArray();

        for (int pinnedIndex = 0; pinnedIndex < pinnedObjects.Count; ++pinnedIndex)
        {
            PinnedTypeStat stat = pinnedObjects[pinnedIndex];
            JsonObject obj = new JsonObject();
            obj["typeName"] = stat.TypeName;
            obj["generation"] = stat.Generation;
            obj["count"] = stat.Count;
            obj["totalBytes"] = stat.TotalBytes;
            arr.Add(obj);
        }

        return arr;
    }

    private static JsonArray SerializeLohTypes(List<LohTypeStat> lohTypes)
    {
        JsonArray arr = new JsonArray();

        for (int lohIndex = 0; lohIndex < lohTypes.Count; ++lohIndex)
        {
            LohTypeStat stat = lohTypes[lohIndex];
            JsonObject obj = new JsonObject();
            obj["typeName"] = stat.TypeName;
            obj["count"] = stat.Count;
            obj["totalBytes"] = stat.TotalBytes;
            arr.Add(obj);
        }

        return arr;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapAnalyzer)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
