////////////////////////////////////////////////////////////////////////////////
// Module: CpuProfileJsonExporter.cs
//
// Notes:
// Builds the "Profile" view's JSON payload from a List<SampleEvent> (raw
// per-sample stacks, already resolved - see SampleProfileEventProjector.cs):
// a ranked "hot methods" table (self/total sample counts, PerfView's "By
// Name" semantics) plus a single whole-capture flame tree (root-to-leaf,
// one node per distinct call-chain prefix actually observed).
//
// Two ways of looking at the same data, built from the same resolved
// frameIds per sample:
//   - hotMethods: FLAT ranking by self-sample count (frameIds[0], the
//     leaf/currently-executing frame) - "what was actually running", not
//     "what was on the stack". totalSamples per method is inclusive - every
//     DISTINCT frame id anywhere in that sample's stack counts once
//     (dedup'd per-sample so a recursive method like
//     testApps/CpuLoadGenerator's own SlowRecursiveWork doesn't inflate its
//     own inclusive count by appearing at multiple depths in the same
//     sample).
//   - flameTree: HIERARCHICAL, root-to-leaf. Stack/frameIds are leaf-first
//     (index 0 = innermost frame - see EventRecord.Stack's own comment), so
//     building a root-to-leaf tree means folding each sample's frameIds in
//     REVERSE - the opposite direction from Gc/AllocationJsonExporter.cs's
//     BuildCallerTree, which deliberately builds leaf-first (its own tree
//     answers "who allocated, then who called them"; this one answers "what
//     was the call chain, top to bottom" - the standard flame graph shape).
//
// Reuses the same overall shape Gc/AllocationJsonExporter.cs already
// established for exactly this class of problem (interned method names,
// a pooled small-map tree node with a single-child fast path, a global
// best-first node budget to bound output size on a combinatorially large
// tree) - see that file's own header/DrillDownTreeNode comments for the
// full measured rationale. This is simpler than that file in one respect:
// there is only ever ONE tree here (the whole capture), not one per (type,
// time-bucket) cell plus one per type plus an "loh" duplicate of both - so
// there's no per-scope pool-reset/multi-tree bookkeeping to replicate.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Cpu {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class CpuProfileJsonExporter
{
    // Mirrors AllocationSummaryBuilder.TopTypesLimit's own reasoning (a
    // ranked table beyond a couple hundred rows stops being scannable) -
    // picked a little larger since a real program's distinct hot LEAF
    // methods commonly outnumbers its distinct allocated TYPES.
    private const int MaxRankedMethods = 200;

    // Mirrors AllocationSummaryBuilder.DrillDownTreeChildrenLimit - same
    // "per-node breadth cap alone doesn't bound total output size, but a
    // node with more than this many distinct children is already
    // unreadable as a flat list of boxes" reasoning.
    private const int FlameTreeChildrenLimit = 50;

    // Mirrors AllocationSummaryBuilder.DrillDownTreeNodeBudgetPerType (the
    // closest existing analog - a single whole-capture tree, not one of
    // many narrow per-cell trees) - there's only one tree in this export
    // (see this file's header comment), so it can afford a single, more
    // generous budget rather than needing separate per-cell/per-type
    // constants.
    private const int FlameTreeNodeBudget = 5000;

    private const int NoStackFrameId = -1;

    // Deduplicates resolved method-name strings across every stack this
    // exporter writes into a single shared pool, referenced from
    // hotMethods/flameTree by integer index - same measured reasoning as
    // AllocationSummaryBuilder's own MethodNameInterner (a long capture's
    // stack data can recur across many thousands of samples sharing the
    // same handful of hot call paths).
    private class MethodNameInterner
    {
        private readonly Dictionary<string, int> indexByName = new Dictionary<string, int>();
        public readonly List<string> NamesInOrder = new List<string>();

        public int Intern(string name)
        {
            int index;
            if (!indexByName.TryGetValue(name, out index))
            {
                index = NamesInOrder.Count;
                indexByName[name] = index;
                NamesInOrder.Add(name);
            }

            return index;
        }
    }

    private class HotMethodStats
    {
        public int SelfSamples;
        public int TotalSamples;
    }

    // Resolved frames for one distinct raw Stack array, cached together so
    // both are computed exactly once per distinct stack (not once per
    // sample) - see frameIdCache's own comment in Write below.
    //
    // A prior version of this class cached fully-resolved HotMethodStats/
    // FlameTreeNode OBJECT REFERENCES instead of ids, specifically to
    // eliminate the statsByFrameId/FlameTreeNode.GetOrAddChild lookups this
    // file's per-sample loop otherwise repeats for every sample sharing an
    // already-cached stack. Measured via dotnet-trace against the same real
    // 1.57GB/26.1M-sample capture used throughout this file's other
    // comments: it DID reduce Dictionary<Int32,__Canon>.FindValue's own
    // self-time (statsByFrameId), but the extra HotMethodStats[]/
    // FlameTreeNode[] array allocated per distinct stack (633,378 of them)
    // raised GC pressure enough to erase that gain and then some -
    // gcCounts went from [1,1,1] to [3,2,2] and a previously-~0ms GC pause
    // grew to ~1.5s, for no net wall-clock improvement (if anything,
    // slightly worse). Reverted back to this simpler, ids-only version,
    // which measured strictly better on every axis (jsonExport time, GC
    // pause, and GC count) - a real, if disappointing, example of a change
    // that looks like a win by one self-profile line but is a net loss
    // once its own cost (here, allocation/GC pressure) is measured too.
    private class CachedStackFrames
    {
        public int[] FrameIds;

        // frameIds, deduped (order irrelevant - only used to walk "which
        // frame ids does this stack touch, once each" for the inclusive/
        // totalSamples count). Computed once per distinct stack for the
        // same reason FrameIds itself is - see Write's own comment.
        public int[] DistinctFrameIds;
    }

    // Root-to-leaf call tree node - see this file's header comment for why
    // this folds sample stacks in the opposite direction from
    // AllocationJsonExporter.cs's DrillDownTreeNode. Same small-map fast
    // path (firstChild/moreChildren) as that type, for the same reason: the
    // overwhelming majority of nodes in a real call-stack tree have exactly
    // one child (a non-branching chain), and allocating a full Dictionary
    // for that common case was a measured, real cost there.
    private class FlameTreeNode
    {
        public long TotalSamples;
        public bool Included;

        private bool hasFirstChild;
        private int firstChildFrameId;
        private FlameTreeNode firstChild;
        private Dictionary<int, FlameTreeNode> moreChildren;

        public int ChildCount
        {
            get { return (this.hasFirstChild ? 1 : 0) + (this.moreChildren != null ? this.moreChildren.Count : 0); }
        }

        public FlameTreeNode GetOrAddChild(int frameId, FlameTreeNodePool pool)
        {
            if (this.hasFirstChild && this.firstChildFrameId == frameId)
            {
                return this.firstChild;
            }

            if (!this.hasFirstChild)
            {
                this.hasFirstChild = true;
                this.firstChildFrameId = frameId;
                this.firstChild = pool.Rent();
                return this.firstChild;
            }

            if (this.moreChildren == null)
            {
                this.moreChildren = new Dictionary<int, FlameTreeNode>(4);
            }

            FlameTreeNode child;
            if (!this.moreChildren.TryGetValue(frameId, out child))
            {
                child = pool.Rent();
                this.moreChildren[frameId] = child;
            }

            return child;
        }

        public void Reset()
        {
            this.TotalSamples = 0;
            this.Included = false;
            this.hasFirstChild = false;
            this.firstChildFrameId = 0;
            this.firstChild = null;

            if (this.moreChildren != null)
            {
                this.moreChildren.Clear();
            }
        }

        public bool TryGetOnlyChild(out int frameId, out FlameTreeNode child)
        {
            if (this.hasFirstChild && this.moreChildren == null)
            {
                frameId = this.firstChildFrameId;
                child = this.firstChild;
                return true;
            }

            frameId = 0;
            child = null;
            return false;
        }

        public void CollectChildren(List<KeyValuePair<int, FlameTreeNode>> buffer)
        {
            if (this.hasFirstChild)
            {
                buffer.Add(new KeyValuePair<int, FlameTreeNode>(this.firstChildFrameId, this.firstChild));
            }

            if (this.moreChildren != null)
            {
                foreach (KeyValuePair<int, FlameTreeNode> pair in this.moreChildren)
                {
                    buffer.Add(pair);
                }
            }
        }
    }

    // Arena/pool for FlameTreeNode, mirroring
    // AllocationJsonExporter.DrillDownTreeNodePool's own measured rationale
    // (avoids one `new` per distinct call-chain-prefix node on a capture
    // with many thousands of samples). This export only ever builds one
    // tree, so - unlike that pool - nothing here needs a ResetForNextTree.
    private class FlameTreeNodePool
    {
        // Diagnostic only (see the NETTRACE_DEBUG print in Write) - not
        // read for pooling/reuse itself, see this class's own header
        // comment on why this export never needed a ResetForNextTree.
        public int RentCount { get; private set; }

        public FlameTreeNode Rent()
        {
            ++this.RentCount;
            return new FlameTreeNode();
        }
    }

    // LIFO free list for the transient child-sorting buffers
    // MarkIncludedNodes/EnqueueTopChildren/WriteFlameTreeChildren each need
    // - mirrors AllocationJsonExporter.ChildBufferPool's own reasoning
    // (recursive writes need a parent's buffer to stay alive while a
    // child's own buffer is rented, so a flat rent-and-reset arena doesn't
    // work here the way FlameTreeNodePool's does).
    private class ChildBufferPool
    {
        private readonly List<List<KeyValuePair<int, FlameTreeNode>>> freeBuffers = new List<List<KeyValuePair<int, FlameTreeNode>>>();

        public List<KeyValuePair<int, FlameTreeNode>> Rent(int capacityHint)
        {
            if (this.freeBuffers.Count > 0)
            {
                List<KeyValuePair<int, FlameTreeNode>> buffer = this.freeBuffers[this.freeBuffers.Count - 1];
                this.freeBuffers.RemoveAt(this.freeBuffers.Count - 1);
                buffer.Clear();

                if (buffer.Capacity < capacityHint)
                {
                    buffer.Capacity = capacityHint;
                }

                return buffer;
            }

            return new List<KeyValuePair<int, FlameTreeNode>>(capacityHint);
        }

        public void Return(List<KeyValuePair<int, FlameTreeNode>> buffer)
        {
            this.freeBuffers.Add(buffer);
        }
    }

    // Writes the "cpuProfile" object (start-to-end, including its own
    // enclosing braces) directly to writer - callers just do
    // writer.WritePropertyName("cpuProfile"); CpuProfileJsonExporter.Write(writer, ...);
    public static void Write(Utf8JsonWriter writer, List<SampleEvent> sampleEvents, MethodSymbolTable symbolTable)
    {
        writer.WriteStartObject();

        if (sampleEvents.Count == 0)
        {
            writer.WriteNumber("totalSampleCount", 0);
            writer.WritePropertyName("hotMethods");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName("flameTree");
            writer.WriteStartObject();
            writer.WriteNumber("frame", NoStackFrameId);
            writer.WriteNumber("totalSamples", 0);
            writer.WriteNumber("totalChildCount", 0);
            writer.WritePropertyName("children");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WritePropertyName("methodNames");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }

        MethodNameInterner methodNameInterner = new MethodNameInterner();

        // Keyed by Stack array REFERENCE (ReferenceEqualityComparer), not
        // content - same measured reasoning as
        // AllocationJsonExporter.Write's own frameIdCache: EventBlock.cs
        // hands back the SAME long[] instance for every sample that
        // resolved against the same current StackBlock entry, so a real
        // capture's frequent runs of consecutive identical-stack samples
        // (a hot, tight loop being sampled repeatedly) resolve their
        // frames exactly once instead of once per sample.
        //
        // Deliberately caches just frame ids, not resolved HotMethodStats/
        // FlameTreeNode object references - see CachedStackFrames' own
        // comment for why a fancier version of this cache measured WORSE
        // (higher GC pressure from the extra arrays that approach needed,
        // outweighing its own reduced Dictionary.FindValue self-time).
        Dictionary<long[], CachedStackFrames> frameIdCache = new Dictionary<long[], CachedStackFrames>(ReferenceEqualityComparer.Instance);

        Dictionary<int, HotMethodStats> statsByFrameId = new Dictionary<int, HotMethodStats>();

        // Reused only while computing a NEW distinct stack's own
        // DistinctFrameIds (see CachedStackFrames) - NOT once per sample.
        // Measured via dotnet-trace, profiling nettraceParser's own process
        // against a real 1.57GB/26.1M-sample capture: calling
        // HashSet<int>.Clear() once per SAMPLE (the original version of
        // this method) was a real, large cost - Clear() zeroes the whole
        // internal bucket array on every call, proportional to the
        // HashSet's peak capacity, not its actual content, and that peak
        // capacity only ever grows across the whole loop (one deep stack
        // early on permanently raises the cost of every later Clear(),
        // even for an unrelated shallow stack). Deduping is a pure
        // function of a stack's own FrameIds, so computing it once per
        // distinct stack - amortized across however many samples share
        // that exact stack (commonly enormous: a busy-waiting thread
        // sampled repeatedly resolves to the SAME raw Stack array
        // reference for its whole idle stretch - confirmed on this same
        // capture, where ~74% of samples sat in one thread-pool wait loop)
        // - turns a 26.1M-call cost into a per-distinct-stack one.
        //
        // Still not a HashSet, even at that reduced call count: on the same
        // real capture, 633,378 distinct stacks were resolved, so
        // HashSet<int>.Clear()'s own "zeroes the whole bucket array every
        // call" cost was still the single largest remaining self-time cost
        // in the whole export (confirmed via dotnet-trace: a direct
        // CpuProfileJsonExporter.Write -> Buffer._ZeroMemory chain, no
        // Utf8JsonWriter/flush frames involved). A reused plain int[]
        // scratch buffer, sorted (Array.Sort) then dedup-compacted in
        // place, needs no zeroing at all - only the portion actually
        // written this call is ever touched, unlike a hash table's whole
        // backing store.
        int[] distinctFramesScratch = new int[64];

        FlameTreeNodePool nodePool = new FlameTreeNodePool();
        ChildBufferPool bufferPool = new ChildBufferPool();
        FlameTreeNode root = nodePool.Rent();

        Span<SampleEvent> sampleEventsSpan = CollectionsMarshal.AsSpan(sampleEvents);
        for (int sampleIndex = 0; sampleIndex < sampleEventsSpan.Length; ++sampleIndex)
        {
            ref readonly SampleEvent sampleEvent = ref sampleEventsSpan[sampleIndex];

            if (sampleEvent.Stack.Length == 0)
            {
                FlameTreeNode noStackChild = root.GetOrAddChild(NoStackFrameId, nodePool);
                ++noStackChild.TotalSamples;
                continue;
            }

            CachedStackFrames cached;
            if (!frameIdCache.TryGetValue(sampleEvent.Stack, out cached))
            {
                int[] resolvedFrameIds = new int[sampleEvent.Stack.Length];
                for (int frameIndex = 0; frameIndex < sampleEvent.Stack.Length; ++frameIndex)
                {
                    resolvedFrameIds[frameIndex] = symbolTable.ResolveId(sampleEvent.Stack[frameIndex], sampleEvent.RelativeMSec);
                }

                // Computed once per distinct stack - see distinctFramesScratch's
                // own comment above for why this must not run per-sample,
                // and why it's a sorted scratch buffer rather than a
                // HashSet. Grows (never shrinks) the same way the old
                // HashSet's capacity did, but growth here costs nothing
                // extra beyond the copy itself - no zeroing, since
                // Array.Sort only ever touches the [0, length) prefix
                // that's about to be overwritten with real content anyway.
                if (distinctFramesScratch.Length < resolvedFrameIds.Length)
                {
                    distinctFramesScratch = new int[resolvedFrameIds.Length];
                }

                Array.Copy(resolvedFrameIds, distinctFramesScratch, resolvedFrameIds.Length);
                Array.Sort(distinctFramesScratch, 0, resolvedFrameIds.Length);

                // Compact adjacent duplicates in place (standard sorted-
                // array dedup) - safe despite reading and writing the same
                // buffer, since the write index never exceeds the read
                // index it's currently at.
                int distinctCount = 0;
                for (int sortedIndex = 0; sortedIndex < resolvedFrameIds.Length; ++sortedIndex)
                {
                    if (distinctCount == 0 || distinctFramesScratch[distinctCount - 1] != distinctFramesScratch[sortedIndex])
                    {
                        distinctFramesScratch[distinctCount] = distinctFramesScratch[sortedIndex];
                        ++distinctCount;
                    }
                }

                cached = new CachedStackFrames();
                cached.FrameIds = resolvedFrameIds;

                // A non-recursive stack (no method appears twice) is the
                // common case in real captures, and for it the "distinct"
                // set is byte-for-byte identical to FrameIds itself, just
                // possibly reordered - which doesn't matter, since
                // DistinctFrameIds is only ever iterated to increment
                // totals, never compared by position. Reusing the SAME
                // array reference (rather than allocating and copying a
                // second one that would hold the exact same values) roughly
                // halves this loop's per-distinct-stack allocation count -
                // measured via dotnet-trace against the same real 1.57GB/
                // 26.1M-sample capture as the fixes above: 633,378 distinct
                // stacks were resolved, each previously paying for two
                // arrays where one would do whenever it wasn't recursive.
                if (distinctCount == resolvedFrameIds.Length)
                {
                    cached.DistinctFrameIds = resolvedFrameIds;
                }
                else
                {
                    cached.DistinctFrameIds = new int[distinctCount];
                    Array.Copy(distinctFramesScratch, cached.DistinctFrameIds, distinctCount);
                }

                frameIdCache[sampleEvent.Stack] = cached;
            }

            int[] frameIds = cached.FrameIds;

            // Hot methods: self (leaf, frameIds[0]) + inclusive (every
            // distinct frame id this stack touches - see
            // CachedStackFrames.DistinctFrameIds, computed once per
            // distinct stack, not once per sample).
            HotMethodStats leafStats = GetOrAddStats(statsByFrameId, frameIds[0]);
            ++leafStats.SelfSamples;

            int[] distinctFrameIds = cached.DistinctFrameIds;
            for (int distinctIndex = 0; distinctIndex < distinctFrameIds.Length; ++distinctIndex)
            {
                ++GetOrAddStats(statsByFrameId, distinctFrameIds[distinctIndex]).TotalSamples;
            }

            // Flame tree: root-to-leaf, i.e. REVERSE of frameIds' own
            // leaf-first order - see this file's header comment.
            FlameTreeNode current = root;
            for (int frameIndex = frameIds.Length - 1; frameIndex >= 0; --frameIndex)
            {
                current = current.GetOrAddChild(frameIds[frameIndex], nodePool);
                ++current.TotalSamples;
            }
        }

        if (Environment.GetEnvironmentVariable("NETTRACE_DEBUG") != null)
        {
            Console.Error.WriteLine(
                $"CpuProfileJsonExporter (debug): samples={sampleEvents.Count} distinctStacks={frameIdCache.Count} " +
                $"distinctFrameIds={statsByFrameId.Count} flameTreeNodesBuilt={nodePool.RentCount}");
        }

        writer.WriteNumber("totalSampleCount", sampleEvents.Count);

        WriteHotMethods(writer, statsByFrameId, symbolTable, methodNameInterner);

        writer.WritePropertyName("flameTree");
        MarkIncludedNodes(root, FlameTreeNodeBudget, bufferPool);
        root.Included = true;
        WriteFlameTreeNode(writer, NoStackFrameId, root, symbolTable, methodNameInterner, bufferPool);

        writer.WritePropertyName("methodNames");
        writer.WriteStartArray();
        for (int nameIndex = 0; nameIndex < methodNameInterner.NamesInOrder.Count; ++nameIndex)
        {
            writer.WriteStringValue(methodNameInterner.NamesInOrder[nameIndex]);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static HotMethodStats GetOrAddStats(Dictionary<int, HotMethodStats> statsByFrameId, int frameId)
    {
        HotMethodStats stats;
        if (!statsByFrameId.TryGetValue(frameId, out stats))
        {
            stats = new HotMethodStats();
            statsByFrameId[frameId] = stats;
        }

        return stats;
    }

    // "hotMethods": ranked by SelfSamples descending, capped at
    // MaxRankedMethods - mirrors AllocationSummaryBuilder's topTypes
    // (ranked, capped, no truncation flag needed since the ranking itself,
    // not a specific row, is what's capped).
    private static void WriteHotMethods(Utf8JsonWriter writer, Dictionary<int, HotMethodStats> statsByFrameId, MethodSymbolTable symbolTable, MethodNameInterner methodNameInterner)
    {
        List<KeyValuePair<int, HotMethodStats>> ranked = new List<KeyValuePair<int, HotMethodStats>>(statsByFrameId.Count);
        foreach (KeyValuePair<int, HotMethodStats> entry in statsByFrameId)
        {
            ranked.Add(entry);
        }

        ranked.Sort((left, right) => right.Value.SelfSamples.CompareTo(left.Value.SelfSamples));

        int rankedCount = ranked.Count < MaxRankedMethods ? ranked.Count : MaxRankedMethods;

        writer.WritePropertyName("hotMethods");
        writer.WriteStartArray();

        for (int rankIndex = 0; rankIndex < rankedCount; ++rankIndex)
        {
            int frameId = ranked[rankIndex].Key;
            HotMethodStats stats = ranked[rankIndex].Value;

            string frameName = frameId == NoStackFrameId ? "<no stack captured>" : symbolTable.NameForId(frameId);
            int methodNameIndex = methodNameInterner.Intern(frameName);

            writer.WriteStartObject();
            writer.WriteNumber("frame", methodNameIndex);
            writer.WriteNumber("selfSamples", stats.SelfSamples);
            writer.WriteNumber("totalSamples", stats.TotalSamples);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // Same global best-first budget approach as
    // AllocationJsonExporter.MarkIncludedNodes - see that method's own
    // comment for the full measured rationale (an uncapped-depth tree with
    // only a per-node breadth cap can still blow up combinatorially, and a
    // fixed per-node cap can't tell "one huge branch, deep" from "many tiny
    // branches, shallow"). Root's own direct children (a capture's
    // top-level entry points - Main, thread-pool worker dispatch, etc.) are
    // always included unconditionally up to FlameTreeChildrenLimit, before
    // budget is spent on anything deeper - same reasoning as that method's
    // own comment on why top-level rows must never be starved by one
    // dominant branch.
    private static void MarkIncludedNodes(FlameTreeNode root, int budget, ChildBufferPool bufferPool)
    {
        PriorityQueue<FlameTreeNode, long> candidates = new PriorityQueue<FlameTreeNode, long>(budget);

        List<KeyValuePair<int, FlameTreeNode>> topLevelPairs = bufferPool.Rent(root.ChildCount);
        root.CollectChildren(topLevelPairs);
        topLevelPairs.Sort((left, right) => right.Value.TotalSamples.CompareTo(left.Value.TotalSamples));
        int topLevelCount = topLevelPairs.Count < FlameTreeChildrenLimit ? topLevelPairs.Count : FlameTreeChildrenLimit;

        for (int topLevelIndex = 0; topLevelIndex < topLevelCount; ++topLevelIndex)
        {
            FlameTreeNode node = topLevelPairs[topLevelIndex].Value;
            node.Included = true;
            EnqueueTopChildren(candidates, node, bufferPool);
        }

        bufferPool.Return(topLevelPairs);

        int remaining = budget;
        while (candidates.Count > 0 && remaining > 0)
        {
            FlameTreeNode node = candidates.Dequeue();
            node.Included = true;
            --remaining;

            EnqueueTopChildren(candidates, node, bufferPool);
        }
    }

    private static void EnqueueTopChildren(PriorityQueue<FlameTreeNode, long> candidates, FlameTreeNode node, ChildBufferPool bufferPool)
    {
        int onlyFrameId;
        FlameTreeNode onlyChild;
        if (node.TryGetOnlyChild(out onlyFrameId, out onlyChild))
        {
            candidates.Enqueue(onlyChild, -onlyChild.TotalSamples);
            return;
        }

        int totalChildCount = node.ChildCount;
        if (totalChildCount == 0)
        {
            return;
        }

        List<KeyValuePair<int, FlameTreeNode>> children = bufferPool.Rent(totalChildCount);
        node.CollectChildren(children);
        children.Sort((left, right) => right.Value.TotalSamples.CompareTo(left.Value.TotalSamples));

        int childCount = children.Count < FlameTreeChildrenLimit ? children.Count : FlameTreeChildrenLimit;
        for (int childIndex = 0; childIndex < childCount; ++childIndex)
        {
            FlameTreeNode child = children[childIndex].Value;
            candidates.Enqueue(child, -child.TotalSamples);
        }

        bufferPool.Return(children);
    }

    // Writes one node as { frame, totalSamples, totalChildCount, children }
    // - frame is an integer index into the shared "methodNames" pool (see
    // MethodNameInterner), NoStackFrameId (-1) for the synthetic root/
    // no-stack-captured placeholder. totalChildCount is the TRUE distinct
    // child count before FlameTreeChildrenLimit/node.Included capping,
    // letting a consumer tell a node's children list was truncated the same
    // way AllocationJsonExporter's own totalChildCount does.
    private static void WriteFlameTreeNode(Utf8JsonWriter writer, int frameId, FlameTreeNode node, MethodSymbolTable symbolTable, MethodNameInterner methodNameInterner, ChildBufferPool bufferPool)
    {
        string frameName = frameId == NoStackFrameId ? "<no stack captured>" : symbolTable.NameForId(frameId);
        int methodNameIndex = methodNameInterner.Intern(frameName);

        writer.WriteStartObject();
        writer.WriteNumber("frame", methodNameIndex);
        writer.WriteNumber("totalSamples", node.TotalSamples);

        int totalChildCount = node.ChildCount;
        writer.WriteNumber("totalChildCount", totalChildCount);
        writer.WritePropertyName("children");
        writer.WriteStartArray();

        int onlyFrameId;
        FlameTreeNode onlyChild;
        if (node.TryGetOnlyChild(out onlyFrameId, out onlyChild))
        {
            if (onlyChild.Included)
            {
                WriteFlameTreeNode(writer, onlyFrameId, onlyChild, symbolTable, methodNameInterner, bufferPool);
            }
        }
        else if (totalChildCount > 0)
        {
            List<KeyValuePair<int, FlameTreeNode>> children = bufferPool.Rent(totalChildCount);
            node.CollectChildren(children);
            children.Sort((left, right) => right.Value.TotalSamples.CompareTo(left.Value.TotalSamples));

            int childCount = children.Count < FlameTreeChildrenLimit ? children.Count : FlameTreeChildrenLimit;
            for (int childIndex = 0; childIndex < childCount; ++childIndex)
            {
                int childFrameId = children[childIndex].Key;
                FlameTreeNode child = children[childIndex].Value;

                if (!child.Included)
                {
                    continue;
                }

                WriteFlameTreeNode(writer, childFrameId, child, symbolTable, methodNameInterner, bufferPool);
            }

            bufferPool.Return(children);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        // Utf8JsonWriter never auto-flushes on its own - without this, its
        // internal ArrayBufferWriter<byte> has to keep doubling
        // (System.Array.Resize) all the way up to this whole export's
        // entire remaining output size before a single byte reaches disk,
        // since Dispose() is the only other place a flush would happen -
        // same root cause and fix as AllocationJsonExporter.cs's own
        // WriteCellDrillDown/WriteTypeDrillDown (see that file's comment on
        // its own writer.Flush() calls). Confirmed via dotnet-trace
        // profiling nettraceParser's own process against a real 1.57GB/
        // 26.1M-sample capture: Array.Resize + Buffer._ZeroMemory beneath
        // exactly this call site (via Utf8JsonWriter.Grow ->
        // ArrayBufferWriter<byte>.GetMemory) accounted for ~72% of the
        // WHOLE process's sampled CPU time - by far the single largest cost
        // in this entire tool, worse than actually decoding the file.
        // Flushing per node (not just per top-level branch) bounds the
        // writer's own buffer to roughly one node's worth of JSON at a
        // time, which FileStream's own buffer (see
        // GcJsonExporter.WriteToFile) then absorbs without a syscall per
        // flush - same reasoning as AllocationJsonExporter's own comment,
        // just applied at finer (per-node, not per-cell) granularity since
        // a single flame-tree node has no further internal structure to
        // flush partway through the way a whole drill-down cell does.
        writer.Flush();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Cpu)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
