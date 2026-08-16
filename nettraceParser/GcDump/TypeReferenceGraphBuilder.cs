////////////////////////////////////////////////////////////////////////////////
// Module: TypeReferenceGraphBuilder.cs
//
// Notes:
// Collapses the object-level reference graph into a TYPE-level one: for every
// ordered pair of types, how many references run between their instances and
// how many bytes those references point at.
//
// WHY TYPE-LEVEL. The drill-down view wants "what does Dictionary point at,
// and what points at Dictionary" expandable to arbitrary depth. Done at the
// object level that is a tree with as many nodes as the heap has objects,
// which neither survives being sent to a webview nor is readable once it
// arrives. Collapsed by type it becomes a graph with a few hundred thousand
// edges at most, is fully expandable in the webview with no round trips, and
// is what someone reading it actually wants to know - the individual
// Dictionary instance is rarely the point, the relationship is.
//
// COST. One pass over every edge - the largest loop in the whole tool at
// ~30M iterations for a 10M-object heap. Two things keep it honest:
//
//   - The dictionary key is a single packed long, not a (int, int) tuple and
//     emphatically not an interpolated string. Distinct type PAIRS number in
//     the hundreds of thousands while the loop runs tens of millions of
//     times, which is precisely the case CLAUDE.md's hot-loop-dictionary-key
//     rule is about: the string form is built once per surviving edge, at
//     export time, not once per traversal.
//   - Self-edges between instances of the same type are counted but never
//     dominate, so no special casing is needed.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class TypeReferenceGraphBuilder
{
    public static void Build(HeapGraph graph, GcDumpAnalysis analysis)
    {
        int nodeCount = graph.NodeCount;

        Dictionary<long, int> edgeSlotByTypePair = new Dictionary<long, int>();
        List<TypeReferenceEdge> edges = new List<TypeReferenceEdge>();

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            int fromTypeIndex = graph.NodeTypeIndex[nodeIndex];
            int edgeEnd = graph.ChildStart[nodeIndex + 1];

            for (int edgeIndex = graph.ChildStart[nodeIndex]; edgeIndex < edgeEnd; ++edgeIndex)
            {
                int targetNode = graph.ChildTarget[edgeIndex];
                int toTypeIndex = graph.NodeTypeIndex[targetNode];

                long typePairKey = ((long)fromTypeIndex << 32) | (uint)toTypeIndex;

                int edgeSlot;

                if (!edgeSlotByTypePair.TryGetValue(typePairKey, out edgeSlot))
                {
                    edgeSlot = edges.Count;
                    edgeSlotByTypePair.Add(typePairKey, edgeSlot);

                    TypeReferenceEdge newEdge = new TypeReferenceEdge();
                    newEdge.FromTypeIndex = fromTypeIndex;
                    newEdge.ToTypeIndex = toTypeIndex;
                    edges.Add(newEdge);
                }

                // List<T> of a struct exposes no by-ref indexer, so the
                // running totals are read, updated and written back. This is
                // a copy of a 24-byte struct per edge; the alternative -
                // parallel long[] arrays grown by hand - was not worth the
                // extra bookkeeping given the dictionary probe on the same
                // line dominates this loop's cost.
                TypeReferenceEdge edge = edges[edgeSlot];
                ++edge.ReferenceCount;
                edge.ReferencedBytes += graph.NodeSize[targetNode];
                edges[edgeSlot] = edge;
            }

            if ((nodeIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                ProgressReporter.ReportFraction((double)nodeIndex / nodeCount);
            }
        }

        analysis.OutgoingEdges = edges;

        // The incoming view is the same edge list read the other way round.
        // Materialized as a second sorted list rather than recomputed, so the
        // exporter can slice each type's rows out of one contiguous run.
        List<TypeReferenceEdge> incoming = new List<TypeReferenceEdge>(edges);
        incoming.Sort(CompareByToTypeThenBytes);
        analysis.IncomingEdges = incoming;

        edges.Sort(CompareByFromTypeThenBytes);
    }

    private static int CompareByFromTypeThenBytes(TypeReferenceEdge left, TypeReferenceEdge right)
    {
        if (left.FromTypeIndex != right.FromTypeIndex)
        {
            return left.FromTypeIndex.CompareTo(right.FromTypeIndex);
        }

        return right.ReferencedBytes.CompareTo(left.ReferencedBytes);
    }

    private static int CompareByToTypeThenBytes(TypeReferenceEdge left, TypeReferenceEdge right)
    {
        if (left.ToTypeIndex != right.ToTypeIndex)
        {
            return left.ToTypeIndex.CompareTo(right.ToTypeIndex);
        }

        return right.ReferencedBytes.CompareTo(left.ReferencedBytes);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
