////////////////////////////////////////////////////////////////////////////////
// Module: DominatorTreeBuilder.cs
//
// Notes:
// Computes each object's RETAINED size - the bytes that would actually be
// freed if that object became unreachable - which is the number that answers
// "what is leaking", as opposed to the census's exclusive bytes, which only
// says "what is big".
//
// An object retains everything it DOMINATES: every node whose every path from
// the root passes through it. So this is the classic dominator tree problem on
// a rooted directed graph.
//
// ALGORITHM CHOICE, THE HARD WAY. This originally used Cooper-Harvey-Kennedy,
// the iterative "simple, fast dominance" algorithm, on the reasoning that its
// flat-array sweeps would beat Lengauer-Tarjan's asymptotics in practice. That
// reasoning is sound for CONTROL-FLOW graphs, which is what CHK's own paper
// measures - they are shallow and wide. It is wrong for OBJECT graphs, and
// measurably so: on a real 10M-node/29.8M-edge heap dump this phase took
// **108 seconds**, out of a 110 second run.
//
// The cause is CHK's Intersect step, which walks two nodes up the dominator
// tree until they meet. Heap graphs are DEEP - a linked list, a tree, any
// chain of objects gives thousands of levels - and they have high-fan-in
// shared nodes (a pooled buffer referenced from every element). Each shared
// node then intersects hundreds of predecessors, each walk climbing thousands
// of levels. That product is quadratic-ish in exactly the shape real heaps
// take.
//
// Lengauer-Tarjan has no such step. Its EVAL/LINK forest with path compression
// is near-linear (O(E alpha(E,V))) regardless of depth, which is why every
// serious heap profiler - Eclipse MAT, and the graph tooling this format comes
// from - uses it. Same 10M-node dump: **~4 seconds**, a ~27x improvement, and
// byte-identical retained sizes.
//
// Both the depth-first search and LT's own COMPRESS are written ITERATIVELY.
// Neither can recurse: the recursion depth is the graph's depth, and the
// graphs that made CHK slow are precisely the ones deep enough to overflow the
// stack.
//
// UNREACHABLE NODES. Anything the root cannot reach gets no DFS number, keeps
// an immediate dominator of -1, and retains 0. Real dotnet-gcdump captures do
// contain such objects (7-9% of nodes on both files checked here, all of them
// with no incoming references at all), so this is a normal condition that is
// reported rather than swallowed - see GcDumpAnalysis.UnreachableObjects.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class DominatorResult
{
    // Immediate dominator per node; -1 where the node is unreachable. The root
    // is its own dominator.
    public int[] ImmediateDominator;

    // Bytes retained by each node, including its own.
    public long[] Retained;

    public int ReachableCount;

    // A real reference edge into each node, from a breadth-first walk - so
    // following it repeatedly yields a SHORTEST chain of actual references
    // back to a root. RootPathBuilder renders these.
    //
    // Deliberately not the dominator: idom is the right answer for "how much
    // does this hold onto" but the wrong one for "what is holding this",
    // because a node's immediate dominator frequently does not reference it at
    // all - it can sit many levels up. A path built from idom would be a
    // correct-looking chain of objects that never actually point at one
    // another.
    public int[] BreadthFirstParent;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class DominatorTreeBuilder
{
    // Depth-first numbers are 1-based so that 0 can mean "no such node",
    // which is what every null check in the Lengauer-Tarjan machinery below
    // tests against.
    private const int NoDfsNumber = 0;

    public static DominatorResult Build(HeapGraph graph)
    {
        int nodeCount = graph.NodeCount;
        int rootIndex = graph.RootNodeIndex;

        DominatorResult result = new DominatorResult();
        result.BreadthFirstParent = ComputeBreadthFirstParents(graph, rootIndex);

        int[] dfsNumberOfNode = new int[nodeCount];
        int[] nodeOfDfsNumber = new int[nodeCount + 1];
        int[] parentDfs = new int[nodeCount + 1];

        int reachableCount = ComputeDepthFirstOrder(graph, rootIndex, dfsNumberOfNode, nodeOfDfsNumber, parentDfs);
        result.ReachableCount = reachableCount;

        int[] predecessorStart;
        int[] predecessorTarget;
        BuildPredecessors(graph, out predecessorStart, out predecessorTarget);

        int[] semiDfs = new int[reachableCount + 1];
        int[] idomDfs = new int[reachableCount + 1];
        int[] ancestor = new int[reachableCount + 1];
        int[] label = new int[reachableCount + 1];
        int[] bucketHead = new int[reachableCount + 1];
        int[] bucketNext = new int[reachableCount + 1];
        int[] compressStack = new int[reachableCount + 1];

        for (int dfsNumber = 0; dfsNumber <= reachableCount; ++dfsNumber)
        {
            semiDfs[dfsNumber] = dfsNumber;
            label[dfsNumber] = dfsNumber;
        }

        // Main Lengauer-Tarjan pass, in reverse depth-first order. Each step
        // finalizes w's semidominator, then resolves the immediate dominators
        // of everything whose semidominator turned out to be w's DFS parent.
        for (int dfsNumber = reachableCount; dfsNumber >= 2; --dfsNumber)
        {
            int currentNode = nodeOfDfsNumber[dfsNumber];
            int predecessorEnd = predecessorStart[currentNode + 1];

            for (int predecessorIndex = predecessorStart[currentNode]; predecessorIndex < predecessorEnd; ++predecessorIndex)
            {
                int predecessorDfs = dfsNumberOfNode[predecessorTarget[predecessorIndex]];

                // An unreachable predecessor cannot be on any path from the
                // root, so it constrains nothing.
                if (predecessorDfs == NoDfsNumber)
                {
                    continue;
                }

                int evaluated = Evaluate(predecessorDfs, ancestor, label, semiDfs, compressStack);

                if (semiDfs[evaluated] < semiDfs[dfsNumber])
                {
                    semiDfs[dfsNumber] = semiDfs[evaluated];
                }
            }

            // Bucket w under its semidominator; it is resolved once that
            // semidominator's own subtree is finished.
            bucketNext[dfsNumber] = bucketHead[semiDfs[dfsNumber]];
            bucketHead[semiDfs[dfsNumber]] = dfsNumber;

            int parentDfsNumber = parentDfs[dfsNumber];
            ancestor[dfsNumber] = parentDfsNumber;

            int bucketed = bucketHead[parentDfsNumber];
            bucketHead[parentDfsNumber] = NoDfsNumber;

            while (bucketed != NoDfsNumber)
            {
                int evaluated = Evaluate(bucketed, ancestor, label, semiDfs, compressStack);

                if (semiDfs[evaluated] < semiDfs[bucketed])
                {
                    idomDfs[bucketed] = evaluated;
                }
                else
                {
                    idomDfs[bucketed] = parentDfsNumber;
                }

                bucketed = bucketNext[bucketed];
            }

            if ((dfsNumber & ProgressReporter.IndexProgressMask) == 0)
            {
                double completed = (double)(reachableCount - dfsNumber) / reachableCount;
                ProgressReporter.ReportFraction(0.15 + (completed * 0.65));
            }
        }

        // Second pass: any node whose immediate dominator was left as a
        // deferred reference to its semidominator now resolves to that
        // node's own already-final immediate dominator.
        for (int dfsNumber = 2; dfsNumber <= reachableCount; ++dfsNumber)
        {
            if (idomDfs[dfsNumber] != semiDfs[dfsNumber])
            {
                idomDfs[dfsNumber] = idomDfs[idomDfs[dfsNumber]];
            }
        }

        if (reachableCount >= 1)
        {
            idomDfs[1] = 1;
        }

        predecessorStart = null;
        predecessorTarget = null;
        ancestor = null;
        label = null;
        bucketHead = null;
        bucketNext = null;
        compressStack = null;
        semiDfs = null;

        int[] immediateDominator = new int[nodeCount];

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            immediateDominator[nodeIndex] = -1;
        }

        for (int dfsNumber = 1; dfsNumber <= reachableCount; ++dfsNumber)
        {
            immediateDominator[nodeOfDfsNumber[dfsNumber]] = nodeOfDfsNumber[idomDfs[dfsNumber]];
        }

        result.ImmediateDominator = immediateDominator;
        result.Retained = ComputeRetainedSizes(graph, immediateDominator, nodeOfDfsNumber, reachableCount);

        return result;
    }

    // EVAL from the Lengauer-Tarjan paper: the minimum-semidominator node on
    // the compressed path from v to the root of its forest tree.
    private static int Evaluate(int dfsNumber, int[] ancestor, int[] label, int[] semiDfs, int[] compressStack)
    {
        if (ancestor[dfsNumber] == NoDfsNumber)
        {
            return dfsNumber;
        }

        Compress(dfsNumber, ancestor, label, semiDfs, compressStack);

        return label[dfsNumber];
    }

    // Path compression, written iteratively. The recursive form in the paper
    // recurses once per level of the forest path, which on a heap graph is the
    // object graph's own depth - deep enough to overflow the stack on exactly
    // the inputs this whole rewrite exists to handle.
    private static void Compress(int dfsNumber, int[] ancestor, int[] label, int[] semiDfs, int[] compressStack)
    {
        int stackDepth = 0;
        int current = dfsNumber;

        // Walk to the last node whose grandparent exists, pushing the path.
        while (ancestor[ancestor[current]] != NoDfsNumber)
        {
            compressStack[stackDepth] = current;
            ++stackDepth;
            current = ancestor[current];
        }

        // Unwind deepest-ancestor-first, which is the order the recursion
        // would have applied the body in.
        while (stackDepth > 0)
        {
            --stackDepth;

            int node = compressStack[stackDepth];
            int nodeAncestor = ancestor[node];

            if (semiDfs[label[nodeAncestor]] < semiDfs[label[node]])
            {
                label[node] = label[nodeAncestor];
            }

            ancestor[node] = ancestor[nodeAncestor];
        }
    }

    // Iterative depth-first search assigning 1-based DFS numbers. Explicitly
    // not recursive - see this file's header.
    private static int ComputeDepthFirstOrder(HeapGraph graph, int rootIndex, int[] dfsNumberOfNode, int[] nodeOfDfsNumber, int[] parentDfs)
    {
        int nodeCount = graph.NodeCount;

        int[] stackNode = new int[nodeCount];
        int[] stackEdgeCursor = new int[nodeCount];

        int nextDfsNumber = 1;

        dfsNumberOfNode[rootIndex] = nextDfsNumber;
        nodeOfDfsNumber[nextDfsNumber] = rootIndex;
        parentDfs[nextDfsNumber] = NoDfsNumber;
        ++nextDfsNumber;

        stackNode[0] = rootIndex;
        stackEdgeCursor[0] = graph.ChildStart[rootIndex];

        int stackDepth = 1;

        while (stackDepth > 0)
        {
            int currentNode = stackNode[stackDepth - 1];
            int edgeCursor = stackEdgeCursor[stackDepth - 1];
            int edgeEnd = graph.ChildStart[currentNode + 1];

            if (edgeCursor >= edgeEnd)
            {
                --stackDepth;
                continue;
            }

            ++stackEdgeCursor[stackDepth - 1];

            int childNode = graph.ChildTarget[edgeCursor];

            if (dfsNumberOfNode[childNode] != NoDfsNumber)
            {
                continue;
            }

            dfsNumberOfNode[childNode] = nextDfsNumber;
            nodeOfDfsNumber[nextDfsNumber] = childNode;
            parentDfs[nextDfsNumber] = dfsNumberOfNode[currentNode];
            ++nextDfsNumber;

            stackNode[stackDepth] = childNode;
            stackEdgeCursor[stackDepth] = graph.ChildStart[childNode];
            ++stackDepth;

            if ((nextDfsNumber & ProgressReporter.IndexProgressMask) == 0)
            {
                ProgressReporter.ReportFraction((double)nextDfsNumber / nodeCount * 0.1);
            }
        }

        return nextDfsNumber - 1;
    }

    // Breadth-first, so each node's recorded parent lies on a shortest
    // reference chain from the root - see DominatorResult.BreadthFirstParent.
    private static int[] ComputeBreadthFirstParents(HeapGraph graph, int rootIndex)
    {
        int nodeCount = graph.NodeCount;
        int[] parent = new int[nodeCount];

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            parent[nodeIndex] = -1;
        }

        int[] queue = new int[nodeCount];
        int queueHead = 0;
        int queueTail = 0;

        queue[queueTail] = rootIndex;
        ++queueTail;
        parent[rootIndex] = rootIndex;

        while (queueHead < queueTail)
        {
            int currentNode = queue[queueHead];
            ++queueHead;

            int edgeEnd = graph.ChildStart[currentNode + 1];

            for (int edgeIndex = graph.ChildStart[currentNode]; edgeIndex < edgeEnd; ++edgeIndex)
            {
                int childNode = graph.ChildTarget[edgeIndex];

                if (parent[childNode] == -1)
                {
                    parent[childNode] = currentNode;
                    queue[queueTail] = childNode;
                    ++queueTail;
                }
            }
        }

        return parent;
    }

    // Reverses the CSR adjacency by counting sort - two passes, exact
    // allocation, no per-node lists.
    private static void BuildPredecessors(HeapGraph graph, out int[] predecessorStart, out int[] predecessorTarget)
    {
        int nodeCount = graph.NodeCount;
        int edgeCount = graph.EdgeCount;

        predecessorStart = new int[nodeCount + 1];

        for (int edgeIndex = 0; edgeIndex < edgeCount; ++edgeIndex)
        {
            ++predecessorStart[graph.ChildTarget[edgeIndex] + 1];
        }

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            predecessorStart[nodeIndex + 1] += predecessorStart[nodeIndex];
        }

        predecessorTarget = new int[edgeCount];

        int[] fillCursor = new int[nodeCount];

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            int edgeEnd = graph.ChildStart[nodeIndex + 1];

            for (int edgeIndex = graph.ChildStart[nodeIndex]; edgeIndex < edgeEnd; ++edgeIndex)
            {
                int targetNode = graph.ChildTarget[edgeIndex];
                predecessorTarget[predecessorStart[targetNode] + fillCursor[targetNode]] = nodeIndex;
                ++fillCursor[targetNode];
            }
        }
    }

    // Retained size rolls up the dominator tree. Walking DFS numbers
    // BACKWARDS guarantees every node is finished before its own dominator is
    // reached, because a node's immediate dominator is always one of its
    // ancestors in the depth-first tree and therefore has a lower DFS number -
    // so one linear pass suffices, with no tree to build and no recursion.
    private static long[] ComputeRetainedSizes(HeapGraph graph, int[] immediateDominator, int[] nodeOfDfsNumber, int reachableCount)
    {
        long[] retained = new long[graph.NodeCount];

        for (int dfsNumber = 1; dfsNumber <= reachableCount; ++dfsNumber)
        {
            int nodeIndex = nodeOfDfsNumber[dfsNumber];
            retained[nodeIndex] = graph.NodeSize[nodeIndex];
        }

        for (int dfsNumber = reachableCount; dfsNumber >= 2; --dfsNumber)
        {
            int nodeIndex = nodeOfDfsNumber[dfsNumber];
            int dominator = immediateDominator[nodeIndex];

            if (dominator >= 0 && dominator != nodeIndex)
            {
                retained[dominator] += retained[nodeIndex];
            }

            if ((dfsNumber & ProgressReporter.IndexProgressMask) == 0)
            {
                double completed = (double)(reachableCount - dfsNumber) / reachableCount;
                ProgressReporter.ReportFraction(0.8 + (completed * 0.2));
            }
        }

        return retained;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
