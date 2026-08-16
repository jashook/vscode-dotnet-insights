////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpJsonExporter.cs
//
// Notes:
// Writes the analysis out for the VS Code extension to render.
//
// TYPE NAME INTERNING. Every structure here refers to types by index into a
// single `typeNames`/`typeModules` pool rather than by name. A heap's type
// names are long ("System.Collections.Generic.Dictionary<System.String,
// System.Object>" and worse), each type appears in the census once but in the
// reference graph and root-path trie many times over, and repeating the
// strings inline would dominate the payload. This repo has already been
// bitten by exactly that: a real 1.17GB capture failed to open because
// repeated frame strings blew past Node's maximum string length (see
// CLAUDE.md's method-name interning history). Pooling here is that lesson
// applied up front rather than after a bug report.
//
// The pool is DENSE and covers only the types that actually appear - a heap
// can carry tens of thousands of type-table entries that no live object uses,
// and shipping their names would be pure weight.
//
// Written with Utf8JsonWriter straight to the file rather than through a
// JsonNode tree, per CLAUDE.md's JSON rule - the census and the edge list are
// both firmly in "thousands+ of entries" territory.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpJsonExporter
{
    public static void WriteToFile(string outputPath, GcDumpFile file, GcDumpAnalysis analysis)
    {
        HeapGraph graph = file.Graph;

        // Original type index -> dense pool index, built over everything that
        // will actually be emitted.
        Dictionary<int, int> poolIndexByTypeIndex = new Dictionary<int, int>();
        List<int> poolTypeIndices = new List<int>();

        for (int censusIndex = 0; censusIndex < analysis.Census.Count; ++censusIndex)
        {
            InternType(analysis.Census[censusIndex].TypeIndex, poolIndexByTypeIndex, poolTypeIndices);
        }

        List<TypeReferenceEdge> outgoingToEmit = SelectEdgesPerType(analysis.OutgoingEdges, true);
        List<TypeReferenceEdge> incomingToEmit = SelectEdgesPerType(analysis.IncomingEdges, false);

        for (int edgeIndex = 0; edgeIndex < outgoingToEmit.Count; ++edgeIndex)
        {
            InternType(outgoingToEmit[edgeIndex].FromTypeIndex, poolIndexByTypeIndex, poolTypeIndices);
            InternType(outgoingToEmit[edgeIndex].ToTypeIndex, poolIndexByTypeIndex, poolTypeIndices);
        }

        for (int edgeIndex = 0; edgeIndex < incomingToEmit.Count; ++edgeIndex)
        {
            InternType(incomingToEmit[edgeIndex].FromTypeIndex, poolIndexByTypeIndex, poolTypeIndices);
            InternType(incomingToEmit[edgeIndex].ToTypeIndex, poolIndexByTypeIndex, poolTypeIndices);
        }

        for (int pathIndex = 0; pathIndex < analysis.RootPaths.Count; ++pathIndex)
        {
            InternType(analysis.RootPaths[pathIndex].TypeIndex, poolIndexByTypeIndex, poolTypeIndices);
        }

        using (FileStream outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(outputStream))
            {
                writer.WriteStartObject();

                WriteMetadata(writer, file.Metadata);
                WriteSummary(writer, graph, analysis);
                WriteTypePool(writer, graph, poolTypeIndices);
                WriteCensus(writer, analysis, poolIndexByTypeIndex);
                WriteEdges(writer, "outgoingReferences", outgoingToEmit, poolIndexByTypeIndex);
                WriteEdges(writer, "incomingReferences", incomingToEmit, poolIndexByTypeIndex);
                WriteRootPaths(writer, analysis, poolIndexByTypeIndex);

                writer.WriteEndObject();
            }
        }
    }

    private static void InternType(int typeIndex, Dictionary<int, int> poolIndexByTypeIndex, List<int> poolTypeIndices)
    {
        if (poolIndexByTypeIndex.ContainsKey(typeIndex))
        {
            return;
        }

        poolIndexByTypeIndex.Add(typeIndex, poolTypeIndices.Count);
        poolTypeIndices.Add(typeIndex);
    }

    private static void WriteMetadata(Utf8JsonWriter writer, GcDumpMetadata metadata)
    {
        writer.WriteStartObject("metadata");

        writer.WriteString("processName", metadata.ProcessName ?? "");
        writer.WriteNumber("processId", metadata.ProcessId);
        writer.WriteString("machineName", metadata.MachineName ?? "");
        writer.WriteString("creationTool", metadata.CreationTool ?? "");

        // Local time, in the same ISO 8601 shape the .nettrace path emits -
        // the webview renders this string directly, so emitting UTC here
        // would show the user a wall-clock time that never happened on their
        // machine (see CLAUDE.md's DateTime note).
        if (metadata.TimeCollectedTicks > 0)
        {
            DateTime collectedLocal = new DateTime(metadata.TimeCollectedTicks, DateTimeKind.Utc).ToLocalTime();
            writer.WriteString("collectedDateTime", collectedLocal.ToString("o"));
        }
        else
        {
            writer.WriteString("collectedDateTime", "");
        }

        writer.WriteNumber("totalProcessCommit", metadata.TotalProcessCommit);
        writer.WriteNumber("totalProcessWorkingSet", metadata.TotalProcessWorkingSet);
        writer.WriteBoolean("isSampled", metadata.IsSampled);
        writer.WriteNumber("countMultiplier", metadata.AverageCountMultiplier);
        writer.WriteNumber("sizeMultiplier", metadata.AverageSizeMultiplier);

        writer.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter writer, HeapGraph graph, GcDumpAnalysis analysis)
    {
        writer.WriteStartObject("summary");

        writer.WriteNumber("totalBytes", analysis.TotalLiveBytes);
        writer.WriteNumber("totalObjects", analysis.TotalLiveObjects);
        writer.WriteNumber("typeCount", analysis.Census.Count);
        writer.WriteNumber("referenceCount", graph.EdgeCount);
        writer.WriteNumber("unreachableObjects", analysis.UnreachableObjects);
        writer.WriteNumber("unreachableBytes", analysis.UnreachableBytes);

        writer.WriteEndObject();
    }

    private static void WriteTypePool(Utf8JsonWriter writer, HeapGraph graph, List<int> poolTypeIndices)
    {
        writer.WriteStartArray("typeNames");

        for (int poolIndex = 0; poolIndex < poolTypeIndices.Count; ++poolIndex)
        {
            writer.WriteStringValue(graph.TypeNames[poolTypeIndices[poolIndex]] ?? "");
        }

        writer.WriteEndArray();

        writer.WriteStartArray("typeModules");

        for (int poolIndex = 0; poolIndex < poolTypeIndices.Count; ++poolIndex)
        {
            writer.WriteStringValue(graph.TypeModuleNames[poolTypeIndices[poolIndex]] ?? "");
        }

        writer.WriteEndArray();
    }

    private static void WriteCensus(Utf8JsonWriter writer, GcDumpAnalysis analysis, Dictionary<int, int> poolIndexByTypeIndex)
    {
        writer.WriteStartArray("types");

        for (int censusIndex = 0; censusIndex < analysis.Census.Count; ++censusIndex)
        {
            TypeCensusEntry entry = analysis.Census[censusIndex];

            writer.WriteStartObject();
            writer.WriteNumber("t", poolIndexByTypeIndex[entry.TypeIndex]);
            writer.WriteNumber("c", entry.InstanceCount);
            writer.WriteNumber("b", entry.ExclusiveBytes);
            writer.WriteNumber("r", entry.RetainedBytes);
            writer.WriteNumber("m", entry.MaxInstanceRetainedBytes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteEdges(Utf8JsonWriter writer, string propertyName, List<TypeReferenceEdge> edges, Dictionary<int, int> poolIndexByTypeIndex)
    {
        writer.WriteStartArray(propertyName);

        for (int edgeIndex = 0; edgeIndex < edges.Count; ++edgeIndex)
        {
            TypeReferenceEdge edge = edges[edgeIndex];

            writer.WriteStartObject();
            writer.WriteNumber("f", poolIndexByTypeIndex[edge.FromTypeIndex]);
            writer.WriteNumber("t", poolIndexByTypeIndex[edge.ToTypeIndex]);
            writer.WriteNumber("n", edge.ReferenceCount);
            writer.WriteNumber("b", edge.ReferencedBytes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRootPaths(Utf8JsonWriter writer, GcDumpAnalysis analysis, Dictionary<int, int> poolIndexByTypeIndex)
    {
        writer.WriteStartArray("rootPaths");

        for (int pathIndex = 0; pathIndex < analysis.RootPaths.Count; ++pathIndex)
        {
            RootPathNode node = analysis.RootPaths[pathIndex];

            writer.WriteStartObject();
            writer.WriteNumber("p", node.ParentIndex);
            writer.WriteNumber("t", poolIndexByTypeIndex[node.TypeIndex]);
            writer.WriteNumber("d", node.Depth);
            writer.WriteNumber("c", node.InstanceCount);
            writer.WriteNumber("b", node.Bytes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // Which rootPaths entry starts each type's tree, so the renderer can
        // jump straight to it. Emitted as pairs rather than an object keyed by
        // number, since JSON object keys would be strings the webview then has
        // to parse back into indices.
        writer.WriteStartArray("rootPathIndexByType");

        foreach (KeyValuePair<int, int> pair in analysis.RootPathIndexByType)
        {
            writer.WriteStartObject();
            writer.WriteNumber("t", poolIndexByTypeIndex[pair.Key]);
            writer.WriteNumber("i", pair.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // Both edge lists are already sorted by their own key type and then by
    // bytes descending (see TypeReferenceGraphBuilder), so trimming each type
    // to its top rows is a single linear scan with a per-type counter - no
    // grouping structure, and no re-sort.
    private static List<TypeReferenceEdge> SelectEdgesPerType(List<TypeReferenceEdge> sortedEdges, bool keyOnFromType)
    {
        List<TypeReferenceEdge> selected = new List<TypeReferenceEdge>();

        int currentKeyType = -1;
        int emittedForCurrentType = 0;

        for (int edgeIndex = 0; edgeIndex < sortedEdges.Count; ++edgeIndex)
        {
            TypeReferenceEdge edge = sortedEdges[edgeIndex];
            int keyType = keyOnFromType ? edge.FromTypeIndex : edge.ToTypeIndex;

            if (keyType != currentKeyType)
            {
                currentKeyType = keyType;
                emittedForCurrentType = 0;
            }

            if (emittedForCurrentType >= GcDumpAnalysisLimits.MaxReferenceEdgesPerType)
            {
                continue;
            }

            selected.Add(edge);
            ++emittedForCurrentType;
        }

        return selected;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
