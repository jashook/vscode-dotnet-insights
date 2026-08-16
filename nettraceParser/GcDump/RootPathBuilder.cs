////////////////////////////////////////////////////////////////////////////////
// Module: RootPathBuilder.cs
//
// Notes:
// Answers "why is this still alive": for each of the heaviest types, the
// chains of references that lead from a GC root down to its instances,
// aggregated so that thousands of instances sharing a chain collapse into one
// branch with a count.
//
// Depth 0 of the trie is the type being explained; each step DOWN the trie is
// one step CLOSER to a root. So reading a branch top-to-bottom answers "what
// is holding this, and what is holding that".
//
// SHAPE OF THE COMPUTATION. DominatorTreeBuilder already produced a
// breadth-first parent for every reachable node, so a single instance's path
// is just a pointer walk - no search here at all. The whole cost is one pass
// over the nodes plus a bounded walk per sampled instance.
//
// Three caps keep this bounded on a heap of any size, all in
// GcDumpAnalysisLimits: how many types get a tree, how many instances of each
// are sampled, and how deep a walk goes. The sampling one is the interesting
// one - root paths converge extremely hard in practice (a million strings in
// a cache overwhelmingly share one chain), so the twenty-thousandth instance
// of a type essentially never introduces a branch the first few hundred did
// not already establish. What it WOULD do is turn a bounded pass into a
// tens-of-millions-of-walks pass for no change in the rendered answer.
//
// Because of that sampling, InstanceCount on a trie node is a count of
// SAMPLED instances, not of all of them. The exporter carries each type's
// sample count alongside its total so the UI can present the branch
// proportions honestly rather than implying exact instance counts.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class RootPathBuilder
{
    public static void Build(HeapGraph graph, DominatorResult dominators, GcDumpAnalysis analysis)
    {
        int nodeCount = graph.NodeCount;
        int typeCount = graph.TypeCount;

        // typeIndex -> its depth-0 trie slot, or -1 when the type is not one
        // of the interesting ones. A flat array rather than a dictionary:
        // this is probed once per node over the whole heap.
        int[] trieSlotByType = new int[typeCount];

        for (int typeIndex = 0; typeIndex < typeCount; ++typeIndex)
        {
            trieSlotByType[typeIndex] = -1;
        }

        List<RootPathNode> trie = new List<RootPathNode>();
        Dictionary<int, int> rootPathIndexByType = new Dictionary<int, int>();

        for (int interestingIndex = 0; interestingIndex < analysis.InterestingTypeIndices.Count; ++interestingIndex)
        {
            int typeIndex = analysis.InterestingTypeIndices[interestingIndex];

            RootPathNode depthZero = new RootPathNode();
            depthZero.ParentIndex = -1;
            depthZero.TypeIndex = typeIndex;
            depthZero.Depth = 0;

            trieSlotByType[typeIndex] = trie.Count;
            rootPathIndexByType.Add(typeIndex, trie.Count);
            trie.Add(depthZero);
        }

        // Child lookup keyed by (parent trie slot, type index) packed into one
        // long - the same hot-loop key discipline TypeReferenceGraphBuilder
        // uses, for the same reason.
        Dictionary<long, int> childSlotByParentAndType = new Dictionary<long, int>();

        int[] sampledInstancesByType = new int[typeCount];
        long[] totalInstancesByType = new long[typeCount];

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            if (nodeIndex == graph.RootNodeIndex)
            {
                continue;
            }

            int typeIndex = graph.NodeTypeIndex[nodeIndex];
            int depthZeroSlot = trieSlotByType[typeIndex];

            if (depthZeroSlot < 0)
            {
                continue;
            }

            ++totalInstancesByType[typeIndex];

            if (sampledInstancesByType[typeIndex] >= GcDumpAnalysisLimits.MaxInstancesPerTypeForPaths)
            {
                continue;
            }

            // An unreachable object has no path to a root by definition -
            // including it would invent one.
            if (dominators.BreadthFirstParent[nodeIndex] < 0)
            {
                continue;
            }

            ++sampledInstancesByType[typeIndex];

            long nodeBytes = graph.NodeSize[nodeIndex];

            RootPathNode depthZeroNode = trie[depthZeroSlot];
            ++depthZeroNode.InstanceCount;
            depthZeroNode.Bytes += nodeBytes;
            trie[depthZeroSlot] = depthZeroNode;

            int currentSlot = depthZeroSlot;
            int currentNode = nodeIndex;

            for (int depth = 1; depth <= GcDumpAnalysisLimits.MaxRootPathDepth; ++depth)
            {
                int parentNode = dominators.BreadthFirstParent[currentNode];

                // The root is its own parent - that is the walk's terminator.
                if (parentNode < 0 || parentNode == currentNode)
                {
                    break;
                }

                int parentTypeIndex = graph.NodeTypeIndex[parentNode];
                long childKey = ((long)currentSlot << 32) | (uint)parentTypeIndex;

                int childSlot;

                if (!childSlotByParentAndType.TryGetValue(childKey, out childSlot))
                {
                    if (trie.Count >= GcDumpAnalysisLimits.MaxRootPathTrieNodes)
                    {
                        break;
                    }

                    RootPathNode childNode = new RootPathNode();
                    childNode.ParentIndex = currentSlot;
                    childNode.TypeIndex = parentTypeIndex;
                    childNode.Depth = depth;

                    childSlot = trie.Count;
                    childSlotByParentAndType.Add(childKey, childSlot);
                    trie.Add(childNode);
                }

                RootPathNode existing = trie[childSlot];
                ++existing.InstanceCount;
                existing.Bytes += nodeBytes;
                trie[childSlot] = existing;

                currentSlot = childSlot;
                currentNode = parentNode;

                if (parentNode == graph.RootNodeIndex)
                {
                    break;
                }
            }

            if ((nodeIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                ProgressReporter.ReportFraction((double)nodeIndex / nodeCount);
            }
        }

        analysis.RootPaths = trie;
        analysis.RootPathIndexByType = rootPathIndexByType;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
