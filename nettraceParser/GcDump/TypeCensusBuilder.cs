////////////////////////////////////////////////////////////////////////////////
// Module: TypeCensusBuilder.cs
//
// Notes:
// Rolls the per-object arrays up into one row per type: how many instances
// exist, how many bytes they occupy themselves, and how many bytes they
// retain (from DominatorTreeBuilder).
//
// This is the view `dotnet-gcdump report` prints, which makes it the natural
// place to check this whole reader against an independent implementation -
// see GcDumpReaderTests.cs. It is also a single linear pass over three flat
// arrays, so it is the cheapest of the four analyses by a wide margin.
//
// The synthetic root node is excluded. It is not an object on the heap - it
// is the graph's entry point, with every GC root hanging off it - so counting
// it would put a phantom instance of "UNDEFINED" in the census and make the
// object count disagree with dotnet-gcdump's by exactly one.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class TypeCensusBuilder
{
    public static void Build(HeapGraph graph, DominatorResult dominators, GcDumpAnalysis analysis)
    {
        int typeCount = graph.TypeCount;
        int nodeCount = graph.NodeCount;

        long[] instanceCountByType = new long[typeCount];
        long[] exclusiveBytesByType = new long[typeCount];
        long[] retainedBytesByType = new long[typeCount];
        long[] maxRetainedByType = new long[typeCount];

        long totalBytes = 0;
        long totalObjects = 0;
        long unreachableObjects = 0;
        long unreachableBytes = 0;

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            if (nodeIndex == graph.RootNodeIndex)
            {
                continue;
            }

            int typeIndex = graph.NodeTypeIndex[nodeIndex];
            int nodeSize = graph.NodeSize[nodeIndex];

            long retainedByNode = dominators.Retained[nodeIndex];

            ++instanceCountByType[typeIndex];
            exclusiveBytesByType[typeIndex] += nodeSize;

            if (retainedByNode > maxRetainedByType[typeIndex])
            {
                maxRetainedByType[typeIndex] = retainedByNode;
            }

            int dominator = dominators.ImmediateDominator[nodeIndex];

            // Only "outermost" instances contribute to the retained total -
            // see TypeCensusEntry.RetainedBytes for why summing over all of
            // them produces numbers many times the size of the heap.
            if (dominator >= 0 && graph.NodeTypeIndex[dominator] != typeIndex)
            {
                retainedBytesByType[typeIndex] += retainedByNode;
            }

            ++totalObjects;
            totalBytes += nodeSize;

            if (dominator == -1)
            {
                ++unreachableObjects;
                unreachableBytes += nodeSize;
            }

            if ((nodeIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                ProgressReporter.ReportFraction((double)nodeIndex / nodeCount);
            }
        }


        List<TypeCensusEntry> census = new List<TypeCensusEntry>();

        for (int typeIndex = 0; typeIndex < typeCount; ++typeIndex)
        {
            if (instanceCountByType[typeIndex] == 0)
            {
                continue;
            }

            TypeCensusEntry entry = new TypeCensusEntry();
            entry.TypeIndex = typeIndex;
            entry.TypeName = graph.TypeNames[typeIndex];
            entry.ModuleName = graph.TypeModuleNames[typeIndex];
            entry.InstanceCount = instanceCountByType[typeIndex];
            entry.ExclusiveBytes = exclusiveBytesByType[typeIndex];
            entry.RetainedBytes = retainedBytesByType[typeIndex];
            entry.MaxInstanceRetainedBytes = maxRetainedByType[typeIndex];

            census.Add(entry);
        }

        census.Sort(CompareByExclusiveBytesDescending);

        analysis.Census = census;
        analysis.TotalLiveBytes = totalBytes;
        analysis.TotalLiveObjects = totalObjects;
        analysis.UnreachableObjects = unreachableObjects;
        analysis.UnreachableBytes = unreachableBytes;
    }

    // A named static method rather than a lambda - this is the comparison
    // used for the whole census sort, so it is worth not allocating a
    // delegate per call and not hiding the ordering rule inside a call site.
    private static int CompareByExclusiveBytesDescending(TypeCensusEntry left, TypeCensusEntry right)
    {
        if (left.ExclusiveBytes != right.ExclusiveBytes)
        {
            return right.ExclusiveBytes.CompareTo(left.ExclusiveBytes);
        }

        return right.InstanceCount.CompareTo(left.InstanceCount);
    }

    // The types the path and reference views are computed for, ranked by what
    // they RETAIN rather than what they occupy - a cache holding a million
    // small entries is the thing worth explaining, and it barely registers on
    // exclusive bytes.
    public static List<int> SelectInterestingTypes(GcDumpAnalysis analysis, int limit)
    {
        List<TypeCensusEntry> ranked = new List<TypeCensusEntry>(analysis.Census);
        ranked.Sort(CompareByRetainedBytesDescending);

        List<int> interesting = new List<int>();

        for (int rankIndex = 0; rankIndex < ranked.Count && rankIndex < limit; ++rankIndex)
        {
            interesting.Add(ranked[rankIndex].TypeIndex);
        }

        return interesting;
    }

    private static int CompareByRetainedBytesDescending(TypeCensusEntry left, TypeCensusEntry right)
    {
        if (left.RetainedBytes != right.RetainedBytes)
        {
            return right.RetainedBytes.CompareTo(left.RetainedBytes);
        }

        return right.ExclusiveBytes.CompareTo(left.ExclusiveBytes);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
