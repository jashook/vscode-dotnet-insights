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
//         fragmentationPct, segmentCount, freeChunkCount,
//         histogram: [{ label, minBytes, maxBytes, count, totalBytes }] }
//                                                            // indices 0-4
//     ],
//     freeChunks: {
//       totalCount, totalFreeBytes,
//       histogram: [{ label, minBytes, maxBytes, count, totalBytes }],
//       largeChunks: [{ address, sizeBytes, generation,
//                       precedingType, precedingIsPinned,
//                       followingType, followingIsPinned }]
//     },
//     pinnedObjects: [{ typeName, generation, count, totalBytes }],
//     topLohTypes:   [{ typeName, count, totalBytes }],
//     topPohTypes:   [{ typeName, count, totalBytes }],
//     segments: [{ address, generation, committedBytes, liveBytes, occupancyPct }],
//     segmentMaps: [{ address, generation,
//                     blocks: [{ isGap, typeName, otherTypeCount, objectCount,
//                                bytes, hasPinnedObject }] }]
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
        root["topLohTypes"] = SerializeTypeStats(report.TopLohTypes);
        root["topPohTypes"] = SerializeTypeStats(report.TopPohTypes);
        root["segments"] = SerializeSegments(report.Segments);
        root["segmentMaps"] = SerializeSegmentMaps(report.SegmentMaps);

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
            obj["histogram"] = SerializeHistogram(gen.Histogram);
            arr.Add(obj);
        }

        return arr;
    }

    // Shared by freeChunks.histogram (cross-generation) and each
    // generations[] entry's own histogram (scoped to just that generation) -
    // same bucket shape either way.
    private static JsonArray SerializeHistogram(FreeChunkBucket[] histogram)
    {
        JsonArray histogramArray = new JsonArray();
        for (int bucketIndex = 0; bucketIndex < histogram.Length; ++bucketIndex)
        {
            FreeChunkBucket bucket = histogram[bucketIndex];
            JsonObject bucketObj = new JsonObject();
            bucketObj["label"] = bucket.Label;
            bucketObj["minBytes"] = bucket.MinBytes;
            bucketObj["maxBytes"] = bucket.MaxBytes == long.MaxValue ? -1 : bucket.MaxBytes;
            bucketObj["count"] = bucket.Count;
            bucketObj["totalBytes"] = bucket.TotalBytes;
            histogramArray.Add(bucketObj);
        }

        return histogramArray;
    }

    private static JsonObject SerializeFreeChunks(FreeChunkReport freeChunks)
    {
        JsonObject obj = new JsonObject();
        obj["totalCount"] = freeChunks.TotalCount;
        obj["totalFreeBytes"] = freeChunks.TotalFreeBytes;
        obj["histogram"] = SerializeHistogram(freeChunks.Histogram);

        JsonArray largeChunksArray = new JsonArray();
        for (int chunkIndex = 0; chunkIndex < freeChunks.LargeChunks.Count; ++chunkIndex)
        {
            LargeFreeChunk chunk = freeChunks.LargeChunks[chunkIndex];
            JsonObject chunkObj = new JsonObject();
            chunkObj["address"] = chunk.Address;
            chunkObj["sizeBytes"] = chunk.SizeBytes;
            chunkObj["generation"] = chunk.Generation;
            chunkObj["precedingType"] = chunk.PrecedingTypeName;
            chunkObj["precedingIsPinned"] = chunk.PrecedingIsPinned;
            chunkObj["followingType"] = chunk.FollowingTypeName;
            chunkObj["followingIsPinned"] = chunk.FollowingIsPinned;
            largeChunksArray.Add(chunkObj);
        }

        obj["largeChunks"] = largeChunksArray;
        return obj;
    }

    private static JsonArray SerializeSegments(List<SegmentOccupancy> segments)
    {
        JsonArray arr = new JsonArray();

        for (int segmentIndex = 0; segmentIndex < segments.Count; ++segmentIndex)
        {
            SegmentOccupancy segment = segments[segmentIndex];
            JsonObject obj = new JsonObject();
            obj["address"] = segment.Address;
            obj["generation"] = segment.Generation;
            obj["committedBytes"] = segment.CommittedBytes;
            obj["liveBytes"] = segment.LiveBytes;
            obj["occupancyPct"] = segment.OccupancyPct;
            arr.Add(obj);
        }

        return arr;
    }

    private static JsonArray SerializeSegmentMaps(List<SegmentMap> segmentMaps)
    {
        JsonArray arr = new JsonArray();

        for (int mapIndex = 0; mapIndex < segmentMaps.Count; ++mapIndex)
        {
            SegmentMap segmentMap = segmentMaps[mapIndex];
            JsonObject obj = new JsonObject();
            obj["address"] = segmentMap.Address;
            obj["generation"] = segmentMap.Generation;

            JsonArray blocksArray = new JsonArray();
            for (int blockIndex = 0; blockIndex < segmentMap.Blocks.Count; ++blockIndex)
            {
                SegmentBlock block = segmentMap.Blocks[blockIndex];
                JsonObject blockObj = new JsonObject();
                blockObj["isGap"] = block.IsGap;
                blockObj["typeName"] = block.TypeName;
                blockObj["otherTypeCount"] = block.OtherTypeCount;
                blockObj["objectCount"] = block.ObjectCount;
                blockObj["bytes"] = block.Bytes;
                blockObj["hasPinnedObject"] = block.HasPinnedObject;
                blocksArray.Add(blockObj);
            }

            obj["blocks"] = blocksArray;
            arr.Add(obj);
        }

        return arr;
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

    // Shared by topLohTypes and topPohTypes - both are plain type-ranked
    // lists with no generation field of their own (each list is already
    // scoped to one heap).
    private static JsonArray SerializeTypeStats(List<TypeStat> typeStats)
    {
        JsonArray arr = new JsonArray();

        for (int typeIndex = 0; typeIndex < typeStats.Count; ++typeIndex)
        {
            TypeStat stat = typeStats[typeIndex];
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
