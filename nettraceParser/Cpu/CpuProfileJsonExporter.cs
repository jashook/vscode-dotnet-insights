////////////////////////////////////////////////////////////////////////////////
// Module: CpuProfileJsonExporter.cs
//
// Notes:
// Builds the "Profile" view's JSON payload from a List<SampleEvent> (raw
// per-sample stacks, already resolved - see SampleProfileEventProjector.cs):
// a ranked "hot methods" table (self/total sample counts, PerfView's "By
// Name" semantics), a single whole-capture flame tree (root-to-leaf, one
// node per distinct call-chain prefix actually observed), and one
// expandable caller tree per ranked hot method (the same drilldown UI
// Gc/AllocationJsonExporter.cs and Exceptions/ExceptionJsonExporter.cs
// already give their own ranked tables).
//
// Three ways of looking at the same data, built from the same resolved
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
//   - hotMethodDrillDown: one caller tree PER ranked hot method, parallel to
//     hotMethods (same index order) - "who called this hot method, and
//     through what chains". Folds frameIds in their own natural leaf-first
//     order (matching BuildCallerTree's convention, unlike flameTree above)
//     via a single pass over the already-deduped stack cache - see
//     BuildHotMethodCallerTrees' own comment.
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

using DotnetInsights.NetTrace.Progress;
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

    // Per-hot-method caller-tree node budget - smaller than the
    // whole-capture FlameTreeNodeBudget since up to MaxRankedMethods of
    // these trees are built per export, not just one; mirrors
    // AllocationSummaryBuilder.DrillDownTreeNodeBudgetPerType's own
    // reasoning of sizing a budget per-tree rather than reusing the single
    // whole-capture constant.
    private const int HotMethodDrillDownNodeBudget = 1000;

    private const int NoStackFrameId = -1;


    // Local split of THIS method's own onProgress fraction across its two
    // full per-sample passes (the main loop below, then WriteTimeline's
    // own second pass) - the remaining tail (hot-method ranking, flame-
    // tree/drill-down writing) is comparatively small (bounded by
    // MaxRankedMethods/node-budget caps, not sample count) and left
    // unattributed, absorbed by the caller's own phase-completion snap
    // (see ProgressReporter.CompletePhase's own comment on why a small
    // remainder doesn't need internal tracking).
    // The cheap time-range pre-scan below (see Write) owns the first slice,
    // then the single per-sample loop owns the rest. There used to be a
    // SECOND full per-sample loop here (WriteTimeline's own) taking
    // [MainLoop, Timeline); it's gone - see Write's own comment.
    private const double TimeRangeScanProgressFractionEnd = 0.1;
    private const double MainLoopProgressFractionEnd = 0.9;

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
    // sample) - see cachedByStackIndex's own comment in Write below.
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

        // How many samples in the WHOLE capture share this exact distinct
        // stack - incremented once per sample regardless of whether that
        // sample was the one that first cached this entry (see Write's main
        // loop). Lets BuildHotMethodCallerTrees fold every hot method's own
        // caller tree from a single pass over the already-
        // deduped entries (633,378 on a real 26.1M-sample capture) instead
        // of re-scanning every raw sample per hot method.
        public int SampleCount;
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

        // How many DISTINCT raw stacks pass through this exact node -
        // incremented once per distinct stack (not once per sample sharing
        // it), unlike TotalSamples. Used by the "(N call paths)" hint the
        // Hot Methods drill-down UI shows (same feature drillDownStats.js/
        // exceptionDrillDownStats.js already have) - not read by the flame
        // graph's own rendering, but harmless (and genuinely informative)
        // to populate for both trees this node type is used for (see
        // BuildHotMethodCallerTrees).
        public long DistinctStackCount;

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

    // onProgress: THIS METHOD's own 0.0-1.0 completion fraction - null (the
    // default) for every caller except GcJsonExporter.WriteToFile's --json
    // mode dispatch (see that method's own comment on why this file is one
    // of only two JSON sub-writers with internal fine-grained tracking,
    // rather than just a start/complete pair). Subdivided locally, below,
    // across this method's own two full per-sample passes (this one and
    // WriteTimeline's) - the caller only ever sees ONE combined 0.0-1.0
    // fraction for the whole method, same "callee doesn't need to know
    // about the caller's own global weighting" contract as every other
    // onProgress parameter in this codebase (see NettraceFile.Read's own
    // comment on the convention).
    //
    // Writes the "cpuProfile" object (start-to-end, including its own
    // enclosing braces) directly to writer - callers just do
    // writer.WritePropertyName("cpuProfile"); CpuProfileJsonExporter.Write(writer, ...);
    // Returns the computed sample timeline so Binary/CpuBinarySections.cs can
    // encode the SAME values this call just wrote as JSON (null when the
    // capture had too few samples for a timeline, exactly the case where the
    // JSON omits "sampleTimeline" too). Returned rather than stashed on a
    // static: xUnit runs test classes in parallel, so a static would be
    // cross-contaminated by any other test exporting a different capture.
    public static SampleTimeline Write(Utf8JsonWriter writer, List<SampleEvent> sampleEvents, StackTable stackTable, MethodSymbolTable symbolTable, Action<double> onProgress = null)
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
            writer.WriteNumber("distinctStackCount", 0);
            writer.WriteNumber("totalChildCount", 0);
            writer.WritePropertyName("children");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WritePropertyName("methodNames");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName("hotMethodDrillDown");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();

            // No samples means no timeline - the zeroed shape above omits
            // "sampleTimeline" entirely, so the binary container omits the
            // matching section rather than writing an empty one.
            return null;
        }

        MethodNameInterner methodNameInterner = new MethodNameInterner();

        // Keyed by Stack array REFERENCE (ReferenceEqualityComparer), not
        // content - same measured reasoning as
        // AllocationJsonExporter.Write's own stack cache: EventBlock.cs
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
        // Indexed by StackIndex (see StackTable.cs) - dense, so a plain array
        // replaces what used to be a Dictionary keyed by array identity.
        // distinctStackIndices records, in first-seen order, which entries
        // were actually filled, so BuildHotMethodCallerTrees can still walk
        // only the stacks this capture really used rather than the whole
        // table.
        CachedStackFrames[] cachedByStackIndex = new CachedStackFrames[stackTable.Count];
        List<int> distinctStackIndices = new List<int>();

        // Array-indexed rather than hashed - this is looked up once per
        // sample (16.24M times on a real 3.23GB capture), where
        // Dictionary<Int32,__Canon>.FindValue measured 13.4% of this whole
        // phase. See Cpu/FrameIdTable.cs.
        FrameIdTable<HotMethodStats> statsByFrameId = new FrameIdTable<HotMethodStats>();

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
        // scratch buffer needs no zeroing at all - only the portion
        // actually written this call is ever touched, unlike a hash
        // table's whole backing store.
        //
        // The dedup that fills this buffer was itself sort-based until the
        // 2026-08-15 profile showed Array.Sort<int> at 16.5% of this phase;
        // it's now a stamp set (Cpu/FrameIdSet.cs), which keeps the
        // no-zeroing property and drops the sort entirely.
        int[] distinctFramesScratch = new int[64];
        FrameIdSet distinctFrames = new FrameIdSet();

        FlameTreeNodePool nodePool = new FlameTreeNodePool();
        ChildBufferPool bufferPool = new ChildBufferPool();
        FlameTreeNode root = nodePool.Rent();

        Span<SampleEvent> sampleEventsSpan = CollectionsMarshal.AsSpan(sampleEvents);

        // Time range first, in its own pass, so the timeline's buckets are
        // known BEFORE the main loop below and can be filled there. This
        // pre-scan reads one double per sample and touches no dictionary, and
        // it replaces a second full per-sample loop (WriteTimeline's) that
        // repeated the identity-hash stack lookup for every sample all over
        // again purely to recover each sample's own leaf frame.
        double minRelativeMSec = double.MaxValue;
        double maxRelativeMSec = double.MinValue;

        for (int sampleIndex = 0; sampleIndex < sampleEventsSpan.Length; ++sampleIndex)
        {
            if (onProgress != null && (sampleIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress((sampleIndex / (double)sampleEventsSpan.Length) * TimeRangeScanProgressFractionEnd);
            }

            double relativeMSec = sampleEventsSpan[sampleIndex].RelativeMSec;

            if (relativeMSec < minRelativeMSec)
            {
                minRelativeMSec = relativeMSec;
            }

            if (relativeMSec > maxRelativeMSec)
            {
                maxRelativeMSec = relativeMSec;
            }
        }

        // Cap bucket count so tiny captures don't produce mostly-zero arrays,
        // and so 100 is a useful maximum for large ones (unchanged from when
        // WriteTimeline computed this itself).
        int timelineBucketCount = sampleEvents.Count < 100 ? sampleEvents.Count : 100;

        double timelineTotalDurationMSec = maxRelativeMSec - minRelativeMSec;
        if (timelineTotalDurationMSec <= 0.0)
        {
            timelineTotalDurationMSec = 1.0;
        }

        double timelineBucketDurationMSec = timelineBucketCount > 0 ? timelineTotalDurationMSec / timelineBucketCount : 0.0;

        int[] samplesByBucket = timelineBucketCount > 0 ? new int[timelineBucketCount] : null;

        // Per-LEAF-frame bucket histograms, accumulated in the main loop and
        // reordered into rank order once the ranking exists. Keyed by frame id
        // rather than rank because rank isn't known until every sample has
        // been counted - and it's bounded by the capture's distinct leaf
        // methods (2,675 on a real capture) x 100 buckets, i.e. ~1MB, not by
        // sample count.
        FrameIdTable<int[]> selfBucketsByLeafFrameId = new FrameIdTable<int[]>();

        for (int sampleIndex = 0; sampleIndex < sampleEventsSpan.Length; ++sampleIndex)
        {
            if (onProgress != null && (sampleIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress(TimeRangeScanProgressFractionEnd + ((sampleIndex / (double)sampleEventsSpan.Length) * (MainLoopProgressFractionEnd - TimeRangeScanProgressFractionEnd)));
            }

            ref readonly SampleEvent sampleEvent = ref sampleEventsSpan[sampleIndex];

            int bucketIndex = -1;
            if (samplesByBucket != null)
            {
                bucketIndex = (int)((sampleEvent.RelativeMSec - minRelativeMSec) / timelineBucketDurationMSec);
                if (bucketIndex >= timelineBucketCount)
                {
                    bucketIndex = timelineBucketCount - 1;
                }

                ++samplesByBucket[bucketIndex];
            }

            int stackIndex = sampleEvent.StackIndex;
            long[] stackFrames = stackTable.FramesAt(stackIndex);

            if (stackFrames.Length == 0)
            {
                FlameTreeNode noStackChild = root.GetOrAddChild(NoStackFrameId, nodePool);
                ++noStackChild.TotalSamples;
                continue;
            }

            // One array index, no hashing and no cache to tune. This used to
            // be a Dictionary<long[], _> keyed by the stack ARRAY, whose
            // ReferenceEqualityComparer made RuntimeHelpers.GetHashCode the
            // single largest cost in this whole phase (71-79% of its CPU
            // samples on a real 3.23GB/16.24M-sample capture). Two attempts to
            // soften that - a bigger sticky cache in front of it, and a
            // content-derived hash - are recorded in CLAUDE.md as measured
            // failures; carrying a dense index from the parser (see
            // StackTable.cs) removes the lookup instead of optimizing it.
            CachedStackFrames cached = cachedByStackIndex[stackIndex];
            bool isNewDistinctStack = cached == null;

            if (isNewDistinctStack)
            {
                int[] resolvedFrameIds = new int[stackFrames.Length];
                for (int frameIndex = 0; frameIndex < stackFrames.Length; ++frameIndex)
                {
                    resolvedFrameIds[frameIndex] = symbolTable.ResolveId(stackFrames[frameIndex], sampleEvent.RelativeMSec);
                }

                // Computed once per distinct stack - see distinctFramesScratch's
                // own comment above for why this must not run per-sample.
                // The dedup itself is a stamp set (see Cpu/FrameIdSet.cs),
                // which replaced a sort-then-compact pass over this same
                // buffer: sorting cost O(depth log depth) per distinct stack
                // and measured 16.5% of this whole phase on a real 3.23GB
                // capture, where a stamp set is O(depth) with no comparisons
                // and no zeroing. The buffer still grows (never shrinks) to
                // the deepest stack seen, and still needs no clearing between
                // stacks - only its [0, distinctCount) prefix is ever read.
                if (distinctFramesScratch.Length < resolvedFrameIds.Length)
                {
                    distinctFramesScratch = new int[resolvedFrameIds.Length];
                }

                // First-occurrence (leaf-first) order rather than the sorted
                // order this used to produce - DistinctFrameIds is only ever
                // iterated to increment totals, never compared by position or
                // searched, so the order genuinely doesn't matter (the field's
                // own comment below already said so).
                distinctFrames.StartNewSet();
                int distinctCount = 0;
                for (int frameIndex = 0; frameIndex < resolvedFrameIds.Length; ++frameIndex)
                {
                    if (distinctFrames.Add(resolvedFrameIds[frameIndex]))
                    {
                        distinctFramesScratch[distinctCount] = resolvedFrameIds[frameIndex];
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

                cachedByStackIndex[stackIndex] = cached;
                distinctStackIndices.Add(stackIndex);
            }

            // Every sample sharing this distinct stack counts toward it,
            // regardless of which sample first caused it to be cached -
            // BuildHotMethodCallerTrees needs this weight to fold the
            // already-deduped cache into per-hot-method trees without
            // re-scanning every raw sample.
            ++cached.SampleCount;

            int[] frameIds = cached.FrameIds;

            // Hot methods: self (leaf, frameIds[0]) + inclusive (every
            // distinct frame id this stack touches - see
            // CachedStackFrames.DistinctFrameIds, computed once per
            // distinct stack, not once per sample).
            HotMethodStats leafStats = GetOrAddStats(statsByFrameId, frameIds[0]);
            ++leafStats.SelfSamples;

            // Timeline: this sample's own self time, in its own time bucket.
            // Accumulated HERE, off the leaf frame this loop already resolved,
            // rather than in a second per-sample pass that had to re-find the
            // same leaf through the stack dictionary all over again.
            if (bucketIndex >= 0)
            {
                int[] selfBuckets = selfBucketsByLeafFrameId.Get(frameIds[0]);
                if (selfBuckets == null)
                {
                    selfBuckets = new int[timelineBucketCount];
                    selfBucketsByLeafFrameId.Set(frameIds[0], selfBuckets);
                }

                ++selfBuckets[bucketIndex];
            }

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

                // Only the sample that first resolves a distinct stack
                // walks its path counted here - every later sample sharing
                // the same cached stack takes the isNewDistinctStack=false
                // branch above and skips this, so each node counts each
                // distinct stack passing through it exactly once.
                if (isNewDistinctStack)
                {
                    ++current.DistinctStackCount;
                }
            }
        }

        if (Environment.GetEnvironmentVariable("NETTRACE_DEBUG") != null)
        {
            Console.Error.WriteLine(
                $"CpuProfileJsonExporter (debug): samples={sampleEvents.Count} distinctStacks={distinctStackIndices.Count} " +
                $"distinctFrameIds={statsByFrameId.Count} flameTreeNodesBuilt={nodePool.RentCount}");
        }

        writer.WriteNumber("totalSampleCount", sampleEvents.Count);

        List<KeyValuePair<int, HotMethodStats>> rankedHotMethods = WriteHotMethods(writer, statsByFrameId, symbolTable, methodNameInterner);

        writer.WritePropertyName("flameTree");
        MarkIncludedNodes(root, FlameTreeNodeBudget, bufferPool);
        root.Included = true;
        WriteFlameTreeNode(writer, NoStackFrameId, root, symbolTable, methodNameInterner, bufferPool);

        // One caller tree per ranked hot method (same expandable-caller-
        // stack UI drillDownStats.js/exceptionDrillDownStats.js already
        // give allocations/exceptions), built from a single pass over
        // the already-deduped distinct stacks rather than
        // rescanning every raw sample per hot method - see
        // BuildHotMethodCallerTrees' own comment.
        FrameIdTable<FlameTreeNode> hotMethodTreeRoots = BuildHotMethodCallerTrees(rankedHotMethods, cachedByStackIndex, distinctStackIndices, nodePool);

        writer.WritePropertyName("hotMethodDrillDown");
        writer.WriteStartArray();
        for (int rankIndex = 0; rankIndex < rankedHotMethods.Count; ++rankIndex)
        {
            int frameId = rankedHotMethods[rankIndex].Key;
            FlameTreeNode methodRoot = hotMethodTreeRoots.Get(frameId);
            MarkIncludedNodes(methodRoot, HotMethodDrillDownNodeBudget, bufferPool);
            methodRoot.Included = true;
            WriteFlameTreeNode(writer, frameId, methodRoot, symbolTable, methodNameInterner, bufferPool);
        }

        writer.WriteEndArray();

        // Written only now, AFTER flameTree AND hotMethodDrillDown, not
        // right after flameTree as this used to - methodNameInterner.Intern
        // is also called from inside WriteFlameTreeNode itself (see that
        // method's own Intern call), and hotMethodDrillDown's own trees can
        // legitimately walk into caller frames that never appeared in the
        // whole-capture flameTree above (a per-METHOD caller chain isn't
        // bounded by the same global, best-first FlameTreeNodeBudget
        // selection flameTree's own nodes are subject to - see
        // HotMethodDrillDownNodeBudget's own separate, per-method budget).
        // Utf8JsonWriter is forward-only - it can't retroactively add
        // entries to an already-written array - so writing methodNames
        // here BEFORE hotMethodDrillDown silently left every name interned
        // DURING that loop out of the array entirely, while
        // hotMethodDrillDown's own "frame" fields still referenced their
        // now out-of-bounds indices. Confirmed via a real production
        // capture (asset-delivery-api-10-aug-2026-0003.nettrace,
        // 19.7M samples): 11 nodes across just ONE hot method's own
        // drill-down tree (System.Threading.Thread.Sleep) referenced frame
        // indices 882-890 against a methodNames array only 882 entries
        // long (valid indices 0-881) - the frontend's
        // currentCpuMethodNames[node["frame"]] lookup
        // (cpuDrillDownStats.js's renderCpuCallerRow) resolved to
        // `undefined` for those nodes, and calling .indexOf(...) on that
        // threw partway through buildAndExpandCpuMethodRow - aborting the
        // whole click handler before it ever reached the
        // methodRow.classList.add('expanded') line, so the row's own
        // toggle silently did nothing instead of expanding OR showing any
        // visible error. Whether a given hot method's row hits this bug at
        // all depends entirely on whether ITS OWN caller chain happens to
        // reach a frame the whole-capture flameTree never separately
        // included - not every row, which is exactly why this looked
        // row-specific rather than like a systemic failure.
        writer.WritePropertyName("methodNames");
        writer.WriteStartArray();
        for (int nameIndex = 0; nameIndex < methodNameInterner.NamesInOrder.Count; ++nameIndex)
        {
            writer.WriteStringValue(methodNameInterner.NamesInOrder[nameIndex]);
        }
        writer.WriteEndArray();

        SampleTimeline sampleTimeline = BuildTimeline(writer, rankedHotMethods, selfBucketsByLeafFrameId, samplesByBucket, timelineBucketCount, timelineBucketDurationMSec, timelineTotalDurationMSec, minRelativeMSec);

        writer.WriteEndObject();

        return sampleTimeline;
    }

    // Writes "sampleTimeline" - bucketed sample counts (total and per ranked
    // hot method's self time) for the client-side timeline chart and its
    // zoom filter. Uses a second pass over sampleEvents (after ranking is
    // complete) rather than a single-pass accumulation, since the ranked hot
    // method set isn't known until after the first pass. For the captures
    // this data is most useful on (tens of thousands to a few million
    // samples), the second pass's cost is negligible compared to the first.
    // The computed timeline, decoupled from how it gets serialized. Exists so
    // the JSON writer below and Binary/CpuBinarySections.cs's own writer are
    // fed by ONE computation rather than two that could drift - the whole
    // migration off JSON depends on being able to emit both from the same run
    // and diff them.
    public sealed class SampleTimeline
    {
        public double MinRelativeMSec;
        public double TotalDurationMSec;
        public double BucketDurationMSec;
        public int BucketCount;
        public int[] SamplesByBucket;
        public int[][] MethodSelfByBucket;
    }

    // Assembles the timeline from counts the main per-sample loop already
    // accumulated (see Write). This used to be a second full pass over every
    // sample - 16.24M of them on a real 3.23GB capture - whose only job was to
    // recover each sample's leaf frame, which it did by re-probing the
    // stack->frames dictionary keyed by ARRAY IDENTITY. That probe's
    // RuntimeHelpers.GetHashCode was measured as 71% of this entire export
    // phase's CPU samples. The main loop already has the leaf frame in hand,
    // so the counting moved there and this method now touches nothing
    // per-sample at all.
    private static SampleTimeline BuildTimeline(
        Utf8JsonWriter writer,
        List<KeyValuePair<int, HotMethodStats>> rankedHotMethods,
        FrameIdTable<int[]> selfBucketsByLeafFrameId,
        int[] samplesByBucket,
        int bucketCount,
        double bucketDurationMSec,
        double totalDurationMSec,
        double minRelativeMSec)
    {
        if (bucketCount < 1 || samplesByBucket == null)
        {
            return null;
        }

        // Reordered from frame-id keyed to RANK order, which is the order the
        // JSON (and the webview reading it) expects - parallel to hotMethods.
        // A ranked method with no recorded samples in any bucket can't
        // normally happen (it ranked because it had self samples), but an
        // empty row is written rather than a null so the array stays
        // rectangular.
        int methodCount = rankedHotMethods.Count;
        int[][] methodSelfByBucket = new int[methodCount][];
        for (int rankIndex = 0; rankIndex < methodCount; ++rankIndex)
        {
            int[] selfBuckets = selfBucketsByLeafFrameId.Get(rankedHotMethods[rankIndex].Key);
            methodSelfByBucket[rankIndex] = selfBuckets ?? new int[bucketCount];
        }

        SampleTimeline timeline = new SampleTimeline();
        timeline.MinRelativeMSec = minRelativeMSec;
        timeline.TotalDurationMSec = totalDurationMSec;
        timeline.BucketDurationMSec = bucketDurationMSec;
        timeline.BucketCount = bucketCount;
        timeline.SamplesByBucket = samplesByBucket;
        timeline.MethodSelfByBucket = methodSelfByBucket;

        WriteTimelineJson(writer, timeline);

        return timeline;
    }

    private static void WriteTimelineJson(Utf8JsonWriter writer, SampleTimeline timeline)
    {
        writer.WritePropertyName("sampleTimeline");
        writer.WriteStartObject();
        writer.WriteNumber("minRelativeMSec", timeline.MinRelativeMSec);
        writer.WriteNumber("totalDurationMSec", timeline.TotalDurationMSec);
        writer.WriteNumber("bucketDurationMSec", timeline.BucketDurationMSec);
        writer.WriteNumber("bucketCount", timeline.BucketCount);

        writer.WritePropertyName("samplesByBucket");
        writer.WriteStartArray();
        for (int bucketIndex = 0; bucketIndex < timeline.BucketCount; ++bucketIndex)
        {
            writer.WriteNumberValue(timeline.SamplesByBucket[bucketIndex]);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("methodSelfByBucket");
        writer.WriteStartArray();
        for (int methodIndex = 0; methodIndex < timeline.MethodSelfByBucket.Length; ++methodIndex)
        {
            writer.WriteStartArray();
            for (int bucketIndex = 0; bucketIndex < timeline.BucketCount; ++bucketIndex)
            {
                writer.WriteNumberValue(timeline.MethodSelfByBucket[methodIndex][bucketIndex]);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static HotMethodStats GetOrAddStats(FrameIdTable<HotMethodStats> statsByFrameId, int frameId)
    {
        HotMethodStats stats = statsByFrameId.Get(frameId);
        if (stats == null)
        {
            stats = new HotMethodStats();
            statsByFrameId.Set(frameId, stats);
        }

        return stats;
    }

    // "hotMethods": ranked by SelfSamples descending, capped at
    // MaxRankedMethods - mirrors AllocationSummaryBuilder's topTypes
    // (ranked, capped, no truncation flag needed since the ranking itself,
    // not a specific row, is what's capped). Returns the same capped,
    // ranked list so Write can reuse it (rather than re-ranking a second
    // time) to decide which hot methods get their own caller-tree drilldown
    // via BuildHotMethodCallerTrees.
    private static List<KeyValuePair<int, HotMethodStats>> WriteHotMethods(Utf8JsonWriter writer, FrameIdTable<HotMethodStats> statsByFrameId, MethodSymbolTable symbolTable, MethodNameInterner methodNameInterner)
    {
        List<int> frameIds = statsByFrameId.Keys;
        List<KeyValuePair<int, HotMethodStats>> ranked = new List<KeyValuePair<int, HotMethodStats>>(frameIds.Count);
        for (int keyIndex = 0; keyIndex < frameIds.Count; ++keyIndex)
        {
            ranked.Add(new KeyValuePair<int, HotMethodStats>(frameIds[keyIndex], statsByFrameId.Get(frameIds[keyIndex])));
        }

        // Frame id breaks ties, so the top-N cutoff is deterministic. Without
        // it, methods tied on SelfSamples were ordered by whatever order
        // statsByFrameId happened to enumerate, which is insertion order -
        // meaning a change to the order frames are first SEEN (as the
        // 2026-08-15 dedup rewrite in Write did) silently swaps which tied
        // methods make the cut. Verified against a real 3.23GB capture: the
        // counts themselves were identical for all 197 methods present in
        // both outputs, and only 3 entries sitting exactly on the cutoff
        // (SelfSamples == 10) changed places. Frame ids are minted in symbol
        // resolution order, so this is stable for a given capture.
        ranked.Sort(static (left, right) =>
        {
            int bySelfSamples = right.Value.SelfSamples.CompareTo(left.Value.SelfSamples);
            if (bySelfSamples != 0)
            {
                return bySelfSamples;
            }

            return left.Key.CompareTo(right.Key);
        });

        int rankedCount = ranked.Count < MaxRankedMethods ? ranked.Count : MaxRankedMethods;
        ranked.RemoveRange(rankedCount, ranked.Count - rankedCount);

        writer.WritePropertyName("hotMethods");
        writer.WriteStartArray();

        for (int rankIndex = 0; rankIndex < ranked.Count; ++rankIndex)
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

        return ranked;
    }

    // Folds the already-deduped distinct stacks into one
    // caller-tree per ranked hot method, in a single pass over the cache -
    // NOT one pass per hot method (633,378 distinct stacks x up to 200
    // ranked methods would be a real, measurable cost on a large capture).
    // Each distinct stack is dispatched to at most one tree: the one whose
    // hot method is that stack's own leaf (frameIds[0]) - a sample can only
    // be "self time" for the method that was actually executing, so this
    // matches hotMethods' own SelfSamples ranking exactly, and a stack that
    // doesn't lead any ranked method's own leaf contributes to no tree.
    //
    // Folds each stack in frameIds' own natural, leaf-first order (the hot
    // method itself is the tree's own root, then each successive frameId is
    // that frame's immediate caller) - the OPPOSITE direction from the
    // whole-capture flameTree above, but the SAME convention
    // AllocationJsonExporter.BuildCallerTree already uses for exactly this
    // "who called this" question.
    private static FrameIdTable<FlameTreeNode> BuildHotMethodCallerTrees(List<KeyValuePair<int, HotMethodStats>> rankedHotMethods, CachedStackFrames[] cachedByStackIndex, List<int> distinctStackIndices, FlameTreeNodePool nodePool)
    {
        FrameIdTable<FlameTreeNode> treeRootByLeafFrameId = new FrameIdTable<FlameTreeNode>();
        for (int rankIndex = 0; rankIndex < rankedHotMethods.Count; ++rankIndex)
        {
            treeRootByLeafFrameId.Set(rankedHotMethods[rankIndex].Key, nodePool.Rent());
        }

        for (int distinctIndex = 0; distinctIndex < distinctStackIndices.Count; ++distinctIndex)
        {
            CachedStackFrames cached = cachedByStackIndex[distinctStackIndices[distinctIndex]];
            int[] frameIds = cached.FrameIds;

            FlameTreeNode treeRoot = treeRootByLeafFrameId.Get(frameIds[0]);
            if (treeRoot == null)
            {
                continue;
            }

            FlameTreeNode current = treeRoot;
            current.TotalSamples += cached.SampleCount;
            ++current.DistinctStackCount;

            for (int frameIndex = 1; frameIndex < frameIds.Length; ++frameIndex)
            {
                current = current.GetOrAddChild(frameIds[frameIndex], nodePool);
                current.TotalSamples += cached.SampleCount;
                ++current.DistinctStackCount;
            }
        }

        return treeRootByLeafFrameId;
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
        writer.WriteNumber("distinctStackCount", node.DistinctStackCount);

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
