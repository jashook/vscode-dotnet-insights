////////////////////////////////////////////////////////////////////////////////
// Module: ContentionJsonExporter.cs
//
// Notes:
// Serializes a List<ContentionEvent> into the "contentionSummary" shape the
// VS Code extension's ContentionRenderer.ts consumes: totalContentionCount/
// totalContentionWaitMSec, a topSites ranking (by total wait time, not count),
// and a per-site folded caller-stack tree (siteDrillDown) plus a time-bucketed
// waitMSecByBucket timeline, so the ranked-sites table can expand to show
// caller chains and the timeline can zoom the ranked list.
//
// Structurally mirrors ExceptionJsonExporter.cs (see that file's header
// comment for the full rationale: plain new'd nodes + running WriteBudget
// counter, no DrillDownTreeNodePool complexity needed at contention volumes).
// The primary metric is totalWaitMSec (double) rather than count (int): sites
// are ranked by total milliseconds of lock wait, and tree nodes carry both
// contentionCount and totalWaitMSec.
//
// Sites are aggregated by the resolved leaf frame id of each event's Stack
// (frame index 0, the innermost/direct lock-acquisition frame) - the same
// fold-by-leaf-frame approach ExceptionJsonExporter uses for exception types.
// Events with no stack get a synthetic "<no stack captured>" leaf, same
// convention as ExceptionJsonExporter and AllocationJsonExporter.
//
// Writes directly into a Utf8JsonWriter, per CLAUDE.md's JSON-serialization
// rule and this codebase's established precedent.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Contention {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ContentionJsonExporter
{
    private const int TopSitesLimit = 50;
    private const int DrillDownTreeChildrenLimit = 12;
    private const int DrillDownTreeNodeBudgetPerSite = 300;

    // Reserved frame id for the "<no stack captured>" sentinel - same
    // convention as ExceptionJsonExporter and AllocationJsonExporter.
    private const int NoStackFrameId = -1;
    private const string NoStackLeafName = "<no stack captured>";

    private const int MaxTimelineBuckets = 100;

    // Lock Timeline view: EVERY contended lock is exported, sorted by total
    // wait time descending, and the UI chooses how many tracks to actually
    // draw (its own Top-N control, which includes "All"). Exporting the full
    // set costs far less than it looks: a segment is one contended wait, and
    // every wait belongs to exactly one lock, so the total segment count
    // across all locks is bounded by the capture's own contention count
    // (10,533 on the reference capture) rather than by the lock count. A
    // "top 40 only" cut would have saved nothing on segments while making
    // the long tail permanently unreachable - and the tail is exactly where
    // a rare-but-seconds-long lock hides.
    //
    // Hard cap on emitted ownership segments as a safety valve for a
    // pathological capture. Segments are taken longest-wait-first within
    // each lock, and the budget is spent in rank order, so truncation drops
    // the least interesting intervals of the least contended locks first.
    private const int MaxOwnershipSegments = 60000;

    // Global node budget for the per-lock caller trees, also spent in rank
    // order. Bounded in practice by the number of DISTINCT contention stacks
    // (3,852 on the reference capture), which folding collapses heavily -
    // this cap only matters if a capture has an unusually wide spread of
    // distinct stacks across many locks.
    private const int MaxLockDrillDownNodes = 40000;
    private const int LockDrillDownNodeBudgetPerLock = 300;

    // How many individual worst-case waits to surface. Small on purpose -
    // this is a "jump straight to the stalls" list, not a data dump.
    private const int LongestWaitsLimit = 25;

    // Frames to skip when naming a lock after the code that contends it.
    // Every contention stack bottoms out in the same generic runtime
    // lock-acquisition frame (Monitor.Enter_Slowpath on every single lock in
    // the reference capture), so the LEAF is useless as an identity - naming
    // locks by it would label all 1447 of them identically. The first frame
    // BELOW those primitives is the one that actually distinguishes a lock
    // ("SslStream.DecryptData" vs "MemoryCache.TryGetValue"), which is what
    // makes a hex pointer legible without clicking into it.
    private static readonly string[] LockAcquisitionFramePrefixes = new string[]
    {
        "System.Threading.Monitor.",
        "System.Threading.Lock.",
        "System.Threading.LockHolder.",
        "System.Threading.SpinLock.",
        "System.Threading.ObjectHeader."
    };

    // Frames that identify a stack as running on a managed thread-pool
    // worker. Used to tell "this lock blocks pool threads" (which starves
    // every queued work item behind it) from "two dedicated background
    // threads contend with each other" (which costs those two threads and
    // nobody else) - the raw contention count cannot distinguish them.
    //
    // Matched anywhere in the stack, not just at the root: a pool worker's
    // stack always passes through the dispatch loop on its way to user
    // code, and the frames above it vary per work item. Deliberately
    // conservative - an unrecognized pool entry point undercounts (a real
    // pool wait looks like a non-pool one) rather than misreporting a
    // dedicated thread as a pool thread.
    private static readonly string[] WorkerThreadFrameMarkers = new string[]
    {
        "System.Threading.PortableThreadPool",
        "System.Threading.ThreadPoolWorkQueue",
        "System.Threading.ThreadPool.",
        "System.Threading._ThreadPoolWaitCallback",
        "System.Threading.TimerQueue"
    };

    private sealed class SiteStats
    {
        public int LeafFrameId;
        public string LeafFrameName;
        public int ContentionCount;
        public double TotalWaitMSec;
    }

    // One contended-wait window on a lock. A readonly struct held in a
    // List<T> (rather than indices back into the caller's Span) because a
    // Span is a ref struct and so can't be captured by the sort comparison
    // below - copying these four values per event is the cheaper tradeoff
    // against re-deriving them.
    private readonly struct OwnershipSegment
    {
        public readonly double StartMSec;
        public readonly double EndMSec;
        public readonly double DurationMSec;
        public readonly long OwnerThreadId;
        public readonly long WaiterThreadId;

        public OwnershipSegment(double startMSec, double endMSec, double durationMSec, long ownerThreadId, long waiterThreadId)
        {
            this.StartMSec = startMSec;
            this.EndMSec = endMSec;
            this.DurationMSec = durationMSec;
            this.OwnerThreadId = ownerThreadId;
            this.WaiterThreadId = waiterThreadId;
        }
    }

    private sealed class LockStats
    {
        public long LockId;
        public int ContentionCount;
        public double TotalWaitMSec;
        // The single worst wait on this lock - a lock can be unremarkable in
        // aggregate and still hold the one stall worth investigating.
        public double MaxWaitMSec;
        // Distinct participants, tracked separately because they answer
        // different questions and routinely disagree: a lock contended
        // thousands of times by TWO threads is a ping-pong between them,
        // while the same count spread across a hundred threads is a
        // serialization bottleneck for the whole pool. Owners are tracked
        // apart from waiters for the same reason - many waiters behind a
        // single persistent owner is a different shape of problem than
        // everyone fighting everyone.
        public HashSet<long> WaiterThreadIds = new HashSet<long>();
        public HashSet<long> OwnerThreadIds = new HashSet<long>();
        // Waiters whose own stack shows them to be managed thread-pool
        // workers (see IsWorkerThreadStack). This is the number that separates
        // "two background threads ping-ponging, which nobody cares about"
        // from "this lock is starving the pool" - the same contention count
        // means very different things in those two cases.
        public HashSet<long> WorkerWaiterThreadIds = new HashSet<long>();
        public int WorkerContentionCount;
        public List<OwnershipSegment> Segments = new List<OwnershipSegment>();
        // Distinct stacks contended on this lock, keyed by stack array
        // reference (same ReferenceEqualityComparer discipline the site
        // aggregation uses - see this file's own stacksByRankedSite).
        public Dictionary<long[], ContentionStackAggregate> StacksByReference = new Dictionary<long[], ContentionStackAggregate>(ReferenceEqualityComparer.Instance);
    }

    private sealed class ContentionStackAggregate
    {
        public long[] Stack;
        public int ContentionCount;
        public double TotalWaitMSec;
        public double FirstSeenRelativeMSec;
        // Where this stack's attributed site frame sits (see
        // ResolveSiteFrame). The caller tree folds from here rather than
        // from index 0, so it starts at the site the row names instead of
        // replaying the lock-primitive prefix every stack shares.
        public int SiteFrameIndex;
    }

    private sealed class ContentionTreeNode
    {
        public int ContentionCount;
        public double TotalWaitMSec;
        public int DistinctStackCount;
        public Dictionary<int, ContentionTreeNode> Children = new Dictionary<int, ContentionTreeNode>();
    }

    private sealed class WriteBudget
    {
        public int Remaining;
    }

    public static void Write(Utf8JsonWriter writer, List<ContentionEvent> contentionEvents, MethodSymbolTable symbolTable)
    {
        writer.WriteStartObject();

        writer.WriteNumber("totalContentionCount", contentionEvents.Count);

        if (contentionEvents.Count == 0)
        {
            writer.WriteNumber("totalContentionWaitMSec", 0.0);
            writer.WritePropertyName("topSites");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName("siteDrillDown");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WritePropertyName("timeline");
            writer.WriteNullValue();
            writer.WritePropertyName("lockTimeline");
            writer.WriteNullValue();
            writer.WritePropertyName("methodNames");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }

        Span<ContentionEvent> eventsSpan = CollectionsMarshal.AsSpan(contentionEvents);

        // Pass 1: compute per-site stats and track the overall time range for
        // timeline bucketing.
        Dictionary<int, SiteStats> statsByLeafFrameId = new Dictionary<int, SiteStats>();
        double totalWaitMSec = 0;
        double minRelativeMSec = double.MaxValue;
        double maxRelativeMSec = double.MinValue;

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly ContentionEvent contentionEvent = ref eventsSpan[eventIndex];

            int leafFrameId;
            string leafFrameName;
            int siteFrameIndex;

            ResolveSiteFrame(contentionEvent.Stack, contentionEvent.RelativeMSec, symbolTable, out leafFrameId, out leafFrameName, out siteFrameIndex);

            SiteStats stats;

            if (!statsByLeafFrameId.TryGetValue(leafFrameId, out stats))
            {
                stats = new SiteStats();
                stats.LeafFrameId = leafFrameId;
                stats.LeafFrameName = leafFrameName;
                statsByLeafFrameId[leafFrameId] = stats;
            }

            ++stats.ContentionCount;
            stats.TotalWaitMSec += contentionEvent.DurationMSec;
            totalWaitMSec += contentionEvent.DurationMSec;

            if (contentionEvent.RelativeMSec < minRelativeMSec)
            {
                minRelativeMSec = contentionEvent.RelativeMSec;
            }

            if (contentionEvent.RelativeMSec > maxRelativeMSec)
            {
                maxRelativeMSec = contentionEvent.RelativeMSec;
            }
        }

        writer.WriteNumber("totalContentionWaitMSec", totalWaitMSec);

        // Sort by TotalWaitMSec descending (the dimension users care about
        // most for lock contention: who's blocking the most total time).
        List<SiteStats> sortedStats = new List<SiteStats>(statsByLeafFrameId.Values);
        sortedStats.Sort((SiteStats left, SiteStats right) => right.TotalWaitMSec.CompareTo(left.TotalWaitMSec));

        int topSitesCount = sortedStats.Count < TopSitesLimit ? sortedStats.Count : TopSitesLimit;
        Dictionary<int, int> rankIndexByLeafFrameId = new Dictionary<int, int>(topSitesCount);

        for (int siteIndex = 0; siteIndex < topSitesCount; ++siteIndex)
        {
            rankIndexByLeafFrameId[sortedStats[siteIndex].LeafFrameId] = siteIndex;
        }

        writer.WritePropertyName("topSites");
        writer.WriteStartArray();

        for (int siteIndex = 0; siteIndex < topSitesCount; ++siteIndex)
        {
            SiteStats stats = sortedStats[siteIndex];
            double averageWaitMSec = stats.ContentionCount > 0 ? stats.TotalWaitMSec / stats.ContentionCount : 0.0;
            double percentOfTotal = totalWaitMSec > 0 ? stats.TotalWaitMSec * 100.0 / totalWaitMSec : 0.0;

            writer.WriteStartObject();
            writer.WriteString("SiteName", stats.LeafFrameName);
            writer.WriteNumber("ContentionCount", stats.ContentionCount);
            writer.WriteNumber("TotalWaitMSec", stats.TotalWaitMSec);
            writer.WriteNumber("AverageWaitMSec", averageWaitMSec);
            writer.WriteNumber("PercentOfTotalWait", percentOfTotal);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // Compute timeline parameters before pass 2.
        double totalDurationMSec = maxRelativeMSec - minRelativeMSec;
        bool hasTimeline = totalDurationMSec > 0;
        int bucketCount = 0;
        double bucketDurationMSec = 0;
        double[] waitMSecByBucket = null;

        if (hasTimeline)
        {
            bucketCount = contentionEvents.Count < MaxTimelineBuckets ? contentionEvents.Count : MaxTimelineBuckets;
            bucketDurationMSec = totalDurationMSec / bucketCount;
            waitMSecByBucket = new double[bucketCount];
        }

        // Pass 2: build per-site stack aggregates and fill timeline buckets.
        Dictionary<long[], ContentionStackAggregate>[] stacksByRankedSite = new Dictionary<long[], ContentionStackAggregate>[topSitesCount];

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly ContentionEvent contentionEvent = ref eventsSpan[eventIndex];

            int leafFrameId;
            string unusedFrameName;
            int siteFrameIndex;

            ResolveSiteFrame(contentionEvent.Stack, contentionEvent.RelativeMSec, symbolTable, out leafFrameId, out unusedFrameName, out siteFrameIndex);

            int siteIndex;

            if (rankIndexByLeafFrameId.TryGetValue(leafFrameId, out siteIndex))
            {
                Dictionary<long[], ContentionStackAggregate> siteStacks = stacksByRankedSite[siteIndex];

                if (siteStacks == null)
                {
                    siteStacks = new Dictionary<long[], ContentionStackAggregate>(ReferenceEqualityComparer.Instance);
                    stacksByRankedSite[siteIndex] = siteStacks;
                }

                ContentionStackAggregate aggregate;

                if (!siteStacks.TryGetValue(contentionEvent.Stack, out aggregate))
                {
                    aggregate = new ContentionStackAggregate();
                    aggregate.Stack = contentionEvent.Stack;
                    aggregate.FirstSeenRelativeMSec = contentionEvent.RelativeMSec;
                    aggregate.SiteFrameIndex = siteFrameIndex;
                    siteStacks[contentionEvent.Stack] = aggregate;
                }

                ++aggregate.ContentionCount;
                aggregate.TotalWaitMSec += contentionEvent.DurationMSec;
            }

            if (hasTimeline)
            {
                double offset = contentionEvent.RelativeMSec - minRelativeMSec;
                int bucketIndex = (int)(offset / bucketDurationMSec);

                if (bucketIndex >= bucketCount)
                {
                    bucketIndex = bucketCount - 1;
                }

                waitMSecByBucket[bucketIndex] += contentionEvent.DurationMSec;
            }
        }

        // Shared per-Write call state for the drill-down trees.
        Dictionary<long[], int[]> frameIdCache = new Dictionary<long[], int[]>(ReferenceEqualityComparer.Instance);
        List<string> methodNames = new List<string>();
        Dictionary<string, int> methodNameIndexByName = new Dictionary<string, int>();

        writer.WritePropertyName("siteDrillDown");
        writer.WriteStartArray();

        for (int siteIndex = 0; siteIndex < topSitesCount; ++siteIndex)
        {
            Dictionary<long[], ContentionStackAggregate> siteStacks = stacksByRankedSite[siteIndex];

            writer.WriteStartObject();

            if (siteStacks != null && siteStacks.Count > 0)
            {
                List<ContentionStackAggregate> stackList = new List<ContentionStackAggregate>(siteStacks.Values);

                int typeTotalCount = 0;
                double typeTotalWaitMSec = 0;

                for (int stackIndex = 0; stackIndex < stackList.Count; ++stackIndex)
                {
                    typeTotalCount += stackList[stackIndex].ContentionCount;
                    typeTotalWaitMSec += stackList[stackIndex].TotalWaitMSec;
                }

                writer.WriteNumber("contentionCount", typeTotalCount);
                writer.WriteNumber("totalWaitMSec", typeTotalWaitMSec);
                writer.WriteNumber("distinctStackCount", stackList.Count);

                ContentionTreeNode tree = BuildCallerTree(stackList, symbolTable, frameIdCache);
                WriteBudget budget = new WriteBudget();
                budget.Remaining = DrillDownTreeNodeBudgetPerSite;
                WriteCallerTreeChildren(writer, tree, symbolTable, methodNames, methodNameIndexByName, budget);
            }
            else
            {
                writer.WriteNumber("contentionCount", 0);
                writer.WriteNumber("totalWaitMSec", 0.0);
                writer.WriteNumber("distinctStackCount", 0);
                writer.WriteNumber("totalChildCount", 0);
                writer.WritePropertyName("children");
                writer.WriteStartArray();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // Timeline: null if there's no meaningful time range, otherwise the
        // per-bucket wait-time breakdown for the chart.
        writer.WritePropertyName("timeline");

        if (!hasTimeline)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("minRelativeMSec", minRelativeMSec);
            writer.WriteNumber("totalDurationMSec", totalDurationMSec);
            writer.WriteNumber("bucketDurationMSec", bucketDurationMSec);
            writer.WriteNumber("bucketCount", bucketCount);
            writer.WritePropertyName("waitMSecByBucket");
            writer.WriteStartArray();

            for (int bucketIndex = 0; bucketIndex < bucketCount; ++bucketIndex)
            {
                writer.WriteNumberValue(waitMSecByBucket[bucketIndex]);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        WriteLockTimeline(writer, eventsSpan, minRelativeMSec, maxRelativeMSec, symbolTable, frameIdCache, methodNames, methodNameIndexByName);

        writer.WritePropertyName("methodNames");
        writer.WriteStartArray();

        for (int nameIndex = 0; nameIndex < methodNames.Count; ++nameIndex)
        {
            writer.WriteStringValue(methodNames[nameIndex]);
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    // Writes the "lockTimeline" block backing the Contention view's Lock
    // Timeline tab: one track per ranked lock, each holding the ownership
    // segments observed for it.
    //
    // What a segment actually means, and why it isn't a full ownership
    // timeline: the CLR only emits contention events when a lock is
    // CONTENDED, so a lock held with no one waiting produces no events at
    // all. Each segment here is therefore one contended wait - [start, end]
    // is the window during which `waiterThreadId` was BLOCKED, and
    // `ownerThreadId` is whoever held the lock at the moment that wait
    // began. Rendering it as an ownership bar for ownerThreadId is the
    // correct reading (that thread demonstrably held the lock across that
    // window, which is why the waiter was stuck), but the gaps between
    // segments are NOT proof the lock was free - only that nobody was
    // blocked on it. The view labels this rather than implying completeness.
    //
    // ownerThreadId is 0 whenever the runtime couldn't attribute an owner
    // (~12% of waits on a real capture) and is emitted as 0 for the renderer
    // to show as "unknown" - deliberately not dropped, since a wait with an
    // unknown owner is still a real wait on that lock.
    private static void WriteLockTimeline(Utf8JsonWriter writer, Span<ContentionEvent> eventsSpan, double minRelativeMSec, double maxRelativeMSec, MethodSymbolTable symbolTable, Dictionary<long[], int[]> frameIdCache, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        Dictionary<long, LockStats> statsByLockId = new Dictionary<long, LockStats>();
        Dictionary<long[], bool> workerStackCache = new Dictionary<long[], bool>(ReferenceEqualityComparer.Instance);

        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly ContentionEvent contentionEvent = ref eventsSpan[eventIndex];

            // A V1 ContentionStart payload carries no lock identity at all
            // (see ClrContentionStart.Decode) - such an event can't be
            // placed on any lock's track, so it's skipped here rather than
            // being folded into a bogus shared "lock 0" row.
            if (contentionEvent.LockId == 0)
            {
                continue;
            }

            LockStats stats;

            if (!statsByLockId.TryGetValue(contentionEvent.LockId, out stats))
            {
                stats = new LockStats();
                stats.LockId = contentionEvent.LockId;
                statsByLockId[contentionEvent.LockId] = stats;
            }

            ++stats.ContentionCount;
            stats.TotalWaitMSec += contentionEvent.DurationMSec;
            stats.WaiterThreadIds.Add(contentionEvent.ThreadId);

            if (contentionEvent.DurationMSec > stats.MaxWaitMSec)
            {
                stats.MaxWaitMSec = contentionEvent.DurationMSec;
            }

            // Owner 0 means the runtime couldn't attribute one - counting it
            // would report a phantom extra owner on every lock that has any
            // unattributed wait (~12% of waits on a real capture).
            if (contentionEvent.OwnerThreadId != 0)
            {
                stats.OwnerThreadIds.Add(contentionEvent.OwnerThreadId);
            }

            if (IsWorkerThreadStack(contentionEvent.Stack, contentionEvent.RelativeMSec, symbolTable, workerStackCache))
            {
                stats.WorkerWaiterThreadIds.Add(contentionEvent.ThreadId);
                ++stats.WorkerContentionCount;
            }

            stats.Segments.Add(new OwnershipSegment(contentionEvent.RelativeMSec, contentionEvent.RelativeMSec + contentionEvent.DurationMSec, contentionEvent.DurationMSec, contentionEvent.OwnerThreadId, contentionEvent.ThreadId));

            // Fold this wait's own stack into the lock's distinct-stack set,
            // so the UI can answer "where in the code is this lock actually
            // contended" for whichever lock the user clicks.
            if (contentionEvent.Stack.Length > 0)
            {
                ContentionStackAggregate aggregate;

                if (!stats.StacksByReference.TryGetValue(contentionEvent.Stack, out aggregate))
                {
                    int siteFrameId;
                    string siteFrameName;
                    int siteFrameIndex;
                    ResolveSiteFrame(contentionEvent.Stack, contentionEvent.RelativeMSec, symbolTable, out siteFrameId, out siteFrameName, out siteFrameIndex);

                    aggregate = new ContentionStackAggregate();
                    aggregate.Stack = contentionEvent.Stack;
                    aggregate.FirstSeenRelativeMSec = contentionEvent.RelativeMSec;
                    // Same skip as the sites table, so a lock's tree starts
                    // at the method the lock is named after rather than at
                    // the shared primitive prefix.
                    aggregate.SiteFrameIndex = siteFrameIndex;
                    stats.StacksByReference[contentionEvent.Stack] = aggregate;
                }

                ++aggregate.ContentionCount;
                aggregate.TotalWaitMSec += contentionEvent.DurationMSec;
            }
        }

        writer.WritePropertyName("lockTimeline");

        if (statsByLockId.Count == 0)
        {
            writer.WriteNullValue();
            return;
        }

        // A contention capture's wait durations are extremely long-tailed,
        // which is exactly what makes the outliers invisible on a timeline:
        // measured on the reference capture the median wait is 0.015ms
        // against a 5.66ms maximum (374x), so across a 5-minute x-axis EVERY
        // bar - typical or pathological - is sub-pixel and gets drawn at the
        // same minimum width. Yet the top 1% of waits account for 34% of all
        // time spent blocked. Exporting the p99 threshold lets the renderer
        // mark exactly that tail, and lets the UI state the real cutoff
        // instead of an arbitrary "long wait" number.
        List<double> allWaitDurations = new List<double>();

        foreach (KeyValuePair<long, LockStats> durationEntry in statsByLockId)
        {
            List<OwnershipSegment> lockSegments = durationEntry.Value.Segments;
            for (int segmentIndex = 0; segmentIndex < lockSegments.Count; ++segmentIndex)
            {
                allWaitDurations.Add(lockSegments[segmentIndex].DurationMSec);
            }
        }

        allWaitDurations.Sort();

        double outlierThresholdMSec = 0;

        if (allWaitDurations.Count > 0)
        {
            int thresholdIndex = (int)(allWaitDurations.Count * 0.99);

            if (thresholdIndex >= allWaitDurations.Count)
            {
                thresholdIndex = allWaitDurations.Count - 1;
            }

            outlierThresholdMSec = allWaitDurations[thresholdIndex];
        }

        List<LockStats> sortedLocks = new List<LockStats>(statsByLockId.Values);
        sortedLocks.Sort((LockStats left, LockStats right) => right.TotalWaitMSec.CompareTo(left.TotalWaitMSec));

        // Every lock is emitted - see MaxOwnershipSegments' own comment for
        // why that's affordable.
        int rankedLockCount = sortedLocks.Count;

        // Budgets are shared across every lock, spent in rank order, so the
        // busiest locks keep full detail rather than every lock losing the
        // same fraction.
        int remainingSegmentBudget = MaxOwnershipSegments;
        int remainingDrillDownBudget = MaxLockDrillDownNodes;

        // Union of every thread seen blocked inside thread-pool work,
        // across all locks - lets the view mark which thread ids in its
        // filter list are pool workers (and offer "pool threads only")
        // without re-deriving it per lock in the webview.
        HashSet<long> allWorkerThreadIds = new HashSet<long>();

        foreach (KeyValuePair<long, LockStats> entry in statsByLockId)
        {
            foreach (long workerThreadId in entry.Value.WorkerWaiterThreadIds)
            {
                allWorkerThreadIds.Add(workerThreadId);
            }
        }

        writer.WriteStartObject();
        writer.WriteNumber("minRelativeMSec", minRelativeMSec);
        writer.WriteNumber("maxRelativeMSec", maxRelativeMSec);
        writer.WriteNumber("totalDistinctLockCount", statsByLockId.Count);
        writer.WriteNumber("outlierThresholdMSec", outlierThresholdMSec);
        WriteLongestWaits(writer, statsByLockId);
        writer.WritePropertyName("workerThreadIds");
        writer.WriteStartArray();

        foreach (long workerThreadId in allWorkerThreadIds)
        {
            writer.WriteNumberValue(workerThreadId);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("locks");
        writer.WriteStartArray();

        for (int lockIndex = 0; lockIndex < rankedLockCount; ++lockIndex)
        {
            LockStats stats = sortedLocks[lockIndex];

            writer.WriteStartObject();
            // Hex string, not a JSON number: a lock id is a 64-bit pointer
            // value, which loses precision past 2^53 once JSON.parse turns
            // it into a JS double. It's an opaque identity here (grouping
            // key and display label), never arithmetic, so a string is both
            // safe and directly renderable.
            writer.WriteString("lockId", "0x" + stats.LockId.ToString("X"));
            writer.WriteNumber("contentionCount", stats.ContentionCount);
            writer.WriteNumber("totalWaitMSec", stats.TotalWaitMSec);
            writer.WriteNumber("maxWaitMSec", stats.MaxWaitMSec);
            writer.WriteNumber("waiterThreadCount", stats.WaiterThreadIds.Count);
            writer.WriteNumber("ownerThreadCount", stats.OwnerThreadIds.Count);
            writer.WriteNumber("workerWaiterThreadCount", stats.WorkerWaiterThreadIds.Count);
            writer.WriteNumber("workerContentionCount", stats.WorkerContentionCount);
            // Index into the shared methodNames pool, or -1 when this lock
            // has no stack to name it after. The renderer shows this as the
            // lock's primary label and keeps lockId as the secondary
            // identifier - two distinct locks can legitimately share a name
            // (the same method locking different instances), so the hex
            // pointer stays the real identity.
            writer.WriteNumber("nameFrame", ResolveLockNameFrameIndex(stats, symbolTable, frameIdCache, methodNames, methodNameIndexByName));

            List<OwnershipSegment> segments = stats.Segments;

            // Longest waits first, so a lock truncated by the shared budget
            // keeps its visually significant bars rather than an arbitrary
            // time-ordered prefix.
            segments.Sort((OwnershipSegment left, OwnershipSegment right) => right.DurationMSec.CompareTo(left.DurationMSec));

            int segmentCount = segments.Count < remainingSegmentBudget ? segments.Count : remainingSegmentBudget;
            remainingSegmentBudget -= segmentCount;

            writer.WriteNumber("totalSegmentCount", segments.Count);
            writer.WritePropertyName("segments");
            writer.WriteStartArray();

            for (int segmentIndex = 0; segmentIndex < segmentCount; ++segmentIndex)
            {
                OwnershipSegment segment = segments[segmentIndex];

                writer.WriteStartObject();
                writer.WriteNumber("startMSec", segment.StartMSec);
                writer.WriteNumber("endMSec", segment.EndMSec);
                writer.WriteNumber("ownerThreadId", segment.OwnerThreadId);
                writer.WriteNumber("waiterThreadId", segment.WaiterThreadId);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            // Per-lock caller tree - the same node shape siteDrillDown
            // emits, deliberately, so the webview reuses
            // buildInlineContentionSiteCallerTree verbatim rather than
            // needing a second renderer. Written as null once the shared
            // node budget is spent, which the UI shows as "stack detail
            // unavailable" instead of an empty (and misleading) tree.
            writer.WritePropertyName("drillDown");

            if (stats.StacksByReference.Count > 0 && remainingDrillDownBudget > 0)
            {
                List<ContentionStackAggregate> stackList = new List<ContentionStackAggregate>(stats.StacksByReference.Values);

                ContentionTreeNode tree = BuildCallerTree(stackList, symbolTable, frameIdCache);

                WriteBudget budget = new WriteBudget();
                budget.Remaining = LockDrillDownNodeBudgetPerLock < remainingDrillDownBudget ? LockDrillDownNodeBudgetPerLock : remainingDrillDownBudget;
                int budgetBefore = budget.Remaining;

                writer.WriteStartObject();
                writer.WriteNumber("contentionCount", stats.ContentionCount);
                writer.WriteNumber("totalWaitMSec", stats.TotalWaitMSec);
                writer.WriteNumber("distinctStackCount", stackList.Count);
                WriteCallerTreeChildren(writer, tree, symbolTable, methodNames, methodNameIndexByName, budget);
                writer.WriteEndObject();

                remainingDrillDownBudget -= (budgetBefore - budget.Remaining);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();

            // Flushing once per lock bounds Utf8JsonWriter's own internal
            // buffer to roughly one lock's segments - same reasoning as
            // GcJsonExporter.WriteToFile's own per-GC flush.
            writer.Flush();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // The single worst individual waits in the capture, across every lock.
    // A ranked list of LOCKS answers "which lock costs the most in
    // aggregate"; this answers the different question "when did something
    // actually stall", which aggregation hides - a lock whose total is
    // unremarkable can still contain the one 5ms stop-the-world wait that a
    // latency investigation is looking for. Emitted with absolute times so
    // the view can jump the viewport straight to each one.
    private static void WriteLongestWaits(Utf8JsonWriter writer, Dictionary<long, LockStats> statsByLockId)
    {
        List<KeyValuePair<long, OwnershipSegment>> allSegments = new List<KeyValuePair<long, OwnershipSegment>>();

        foreach (KeyValuePair<long, LockStats> entry in statsByLockId)
        {
            List<OwnershipSegment> lockSegments = entry.Value.Segments;
            for (int segmentIndex = 0; segmentIndex < lockSegments.Count; ++segmentIndex)
            {
                allSegments.Add(new KeyValuePair<long, OwnershipSegment>(entry.Key, lockSegments[segmentIndex]));
            }
        }

        allSegments.Sort((KeyValuePair<long, OwnershipSegment> left, KeyValuePair<long, OwnershipSegment> right) => right.Value.DurationMSec.CompareTo(left.Value.DurationMSec));

        int longestCount = allSegments.Count < LongestWaitsLimit ? allSegments.Count : LongestWaitsLimit;

        writer.WritePropertyName("longestWaits");
        writer.WriteStartArray();

        for (int waitIndex = 0; waitIndex < longestCount; ++waitIndex)
        {
            long lockId = allSegments[waitIndex].Key;
            OwnershipSegment segment = allSegments[waitIndex].Value;

            writer.WriteStartObject();
            writer.WriteString("lockId", "0x" + lockId.ToString("X"));
            writer.WriteNumber("startMSec", segment.StartMSec);
            writer.WriteNumber("endMSec", segment.EndMSec);
            writer.WriteNumber("durationMSec", segment.DurationMSec);
            writer.WriteNumber("ownerThreadId", segment.OwnerThreadId);
            writer.WriteNumber("waiterThreadId", segment.WaiterThreadId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // Resolves the frame a contention should be ATTRIBUTED to, and where in
    // the stack it sits.
    //
    // Not the leaf frame, which is what this originally used. Contention
    // stacks are structurally unlike the allocation/exception stacks the
    // rest of this codebase folds: those bottom out in the method that did
    // the interesting thing (the allocation, the throw), whereas every
    // contention stack bottoms out in the same generic runtime lock
    // primitive. Attributing by leaf therefore collapsed the entire view
    // into a handful of rows - on the reference capture, 10,126 of 10,533
    // contentions (96%) landed on one row named
    // "System.Threading.Monitor.Enter_Slowpath", which is true and
    // completely useless: it says a lock was contended, not which one or
    // where. Ranking by the first frame BELOW the primitives instead yields
    // 54 real sites (SslStream.DecryptData, Http2Stream.TryEnsureHeaders,
    // ...) over the same total wait, and matches how the Lock Timeline tab
    // names its locks so the two views agree.
    //
    // siteFrameIndex is where that frame sits in the stack, so the caller
    // tree can be folded from it rather than replaying the primitive
    // prefix that every stack shares.
    private static void ResolveSiteFrame(long[] stack, double relativeMSec, MethodSymbolTable symbolTable, out int siteFrameId, out string siteFrameName, out int siteFrameIndex)
    {
        if (stack.Length == 0)
        {
            siteFrameId = NoStackFrameId;
            siteFrameName = NoStackLeafName;
            siteFrameIndex = 0;
            return;
        }

        for (int frameIndex = 0; frameIndex < stack.Length; ++frameIndex)
        {
            int frameId = symbolTable.ResolveId(stack[frameIndex], relativeMSec);
            string frameName = symbolTable.NameForId(frameId);

            if (string.IsNullOrEmpty(frameName) || IsLockAcquisitionFrame(frameName))
            {
                continue;
            }

            siteFrameId = frameId;
            siteFrameName = frameName;
            siteFrameIndex = frameIndex;
            return;
        }

        // Every frame is a lock primitive - fall back to the leaf so the
        // wait is still attributed somewhere real rather than dropped.
        siteFrameId = symbolTable.ResolveId(stack[0], relativeMSec);
        siteFrameName = symbolTable.NameForId(siteFrameId);
        siteFrameIndex = 0;
    }

    // Names a lock after the code that contends it: takes the lock's
    // heaviest stack (by wait time, matching how locks themselves are
    // ranked) and walks it leaf-first past the generic runtime
    // lock-acquisition frames, returning the first frame that actually
    // identifies the caller. Returns -1 when the lock has no stacks at all,
    // or when every frame in its dominant stack is a lock primitive - in
    // both cases the renderer falls back to showing the raw pointer.
    private static int ResolveLockNameFrameIndex(LockStats stats, MethodSymbolTable symbolTable, Dictionary<long[], int[]> frameIdCache, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        ContentionStackAggregate dominantStack = null;

        foreach (KeyValuePair<long[], ContentionStackAggregate> entry in stats.StacksByReference)
        {
            if (dominantStack == null || entry.Value.TotalWaitMSec > dominantStack.TotalWaitMSec)
            {
                dominantStack = entry.Value;
            }
        }

        if (dominantStack == null || dominantStack.Stack.Length == 0)
        {
            return -1;
        }

        int[] frameIds;

        if (!frameIdCache.TryGetValue(dominantStack.Stack, out frameIds))
        {
            frameIds = new int[dominantStack.Stack.Length];

            for (int frameIndex = 0; frameIndex < dominantStack.Stack.Length; ++frameIndex)
            {
                frameIds[frameIndex] = symbolTable.ResolveId(dominantStack.Stack[frameIndex], dominantStack.FirstSeenRelativeMSec);
            }

            frameIdCache[dominantStack.Stack] = frameIds;
        }

        for (int frameIndex = 0; frameIndex < frameIds.Length; ++frameIndex)
        {
            string frameName = symbolTable.NameForId(frameIds[frameIndex]);

            if (string.IsNullOrEmpty(frameName) || IsLockAcquisitionFrame(frameName))
            {
                continue;
            }

            return InternMethodName(frameName, methodNames, methodNameIndexByName);
        }

        return -1;
    }

    // Whether this wait happened on a managed thread-pool worker, decided
    // from the waiting thread's own stack. Cached by stack array reference
    // (stacks are interned at parse time - see EventBlock.cs) because a real
    // capture has ~10k waits over only ~4k distinct stacks, and this walks
    // every frame of one.
    private static bool IsWorkerThreadStack(long[] stack, double relativeMSec, MethodSymbolTable symbolTable, Dictionary<long[], bool> workerStackCache)
    {
        if (stack.Length == 0)
        {
            return false;
        }

        bool isPoolStack;

        if (workerStackCache.TryGetValue(stack, out isPoolStack))
        {
            return isPoolStack;
        }

        isPoolStack = false;

        for (int frameIndex = 0; frameIndex < stack.Length && !isPoolStack; ++frameIndex)
        {
            string frameName = symbolTable.NameForId(symbolTable.ResolveId(stack[frameIndex], relativeMSec));

            if (string.IsNullOrEmpty(frameName))
            {
                continue;
            }

            for (int markerIndex = 0; markerIndex < WorkerThreadFrameMarkers.Length; ++markerIndex)
            {
                if (frameName.StartsWith(WorkerThreadFrameMarkers[markerIndex], StringComparison.Ordinal))
                {
                    isPoolStack = true;
                    break;
                }
            }
        }

        workerStackCache[stack] = isPoolStack;
        return isPoolStack;
    }

    private static bool IsLockAcquisitionFrame(string frameName)
    {
        for (int prefixIndex = 0; prefixIndex < LockAcquisitionFramePrefixes.Length; ++prefixIndex)
        {
            if (frameName.StartsWith(LockAcquisitionFramePrefixes[prefixIndex], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int InternMethodName(string frameName, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        int frameNameIndex;

        if (!methodNameIndexByName.TryGetValue(frameName, out frameNameIndex))
        {
            frameNameIndex = methodNames.Count;
            methodNames.Add(frameName);
            methodNameIndexByName[frameName] = frameNameIndex;
        }

        return frameNameIndex;
    }

    // Builds the folded caller-stack tree for one contention site, accumulating
    // both contentionCount and totalWaitMSec at every node - mirrors
    // ExceptionJsonExporter.BuildCallerTree's own algorithm and rationale (fold
    // at every depth, not just the leaf, to preserve every real caller
    // permutation).
    private static ContentionTreeNode BuildCallerTree(List<ContentionStackAggregate> rawStacks, MethodSymbolTable symbolTable, Dictionary<long[], int[]> frameIdCache)
    {
        ContentionTreeNode root = new ContentionTreeNode();

        for (int stackIndex = 0; stackIndex < rawStacks.Count; ++stackIndex)
        {
            ContentionStackAggregate rawStack = rawStacks[stackIndex];
            ContentionTreeNode current = root;

            if (rawStack.Stack.Length == 0)
            {
                current = GetOrAddChild(current, NoStackFrameId);
                AccumulateTreeNode(current, rawStack);
                continue;
            }

            int[] frameIds;

            if (!frameIdCache.TryGetValue(rawStack.Stack, out frameIds))
            {
                frameIds = new int[rawStack.Stack.Length];

                for (int frameIndex = 0; frameIndex < rawStack.Stack.Length; ++frameIndex)
                {
                    frameIds[frameIndex] = symbolTable.ResolveId(rawStack.Stack[frameIndex], rawStack.FirstSeenRelativeMSec);
                }

                frameIdCache[rawStack.Stack] = frameIds;
            }

            // Starts at the stack's own site frame, not index 0 - the frames
            // below it are the generic lock primitives every contention
            // stack shares (see ResolveSiteFrame), so replaying them would
            // put an identical, uninformative "Monitor.Enter_Slowpath" row
            // at the top of every single tree before any real caller.
            for (int frameIndex = rawStack.SiteFrameIndex; frameIndex < frameIds.Length; ++frameIndex)
            {
                current = GetOrAddChild(current, frameIds[frameIndex]);
                AccumulateTreeNode(current, rawStack);
            }
        }

        return root;
    }

    private static ContentionTreeNode GetOrAddChild(ContentionTreeNode node, int frameId)
    {
        ContentionTreeNode child;

        if (!node.Children.TryGetValue(frameId, out child))
        {
            child = new ContentionTreeNode();
            node.Children[frameId] = child;
        }

        return child;
    }

    private static void AccumulateTreeNode(ContentionTreeNode node, ContentionStackAggregate rawStack)
    {
        node.ContentionCount += rawStack.ContentionCount;
        node.TotalWaitMSec += rawStack.TotalWaitMSec;
        ++node.DistinctStackCount;
    }

    // Writes node's "children" array - { frame, contentionCount, totalWaitMSec,
    // distinctStackCount, totalChildCount, children }, recursing depth-first.
    // Children are sorted by totalWaitMSec descending (primary metric) and
    // capped at DrillDownTreeChildrenLimit per node plus the WriteBudget total.
    // Mirrors ExceptionJsonExporter.WriteCallerTreeChildren's own shape, with
    // totalWaitMSec replacing count as the sort key.
    private static void WriteCallerTreeChildren(Utf8JsonWriter writer, ContentionTreeNode node, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName, WriteBudget budget)
    {
        writer.WriteNumber("totalChildCount", node.Children.Count);
        writer.WritePropertyName("children");
        writer.WriteStartArray();

        if (node.Children.Count > 0 && budget.Remaining > 0)
        {
            List<KeyValuePair<int, ContentionTreeNode>> children = new List<KeyValuePair<int, ContentionTreeNode>>(node.Children);
            children.Sort((KeyValuePair<int, ContentionTreeNode> left, KeyValuePair<int, ContentionTreeNode> right) => right.Value.TotalWaitMSec.CompareTo(left.Value.TotalWaitMSec));

            int childCount = children.Count < DrillDownTreeChildrenLimit ? children.Count : DrillDownTreeChildrenLimit;

            for (int childIndex = 0; childIndex < childCount && budget.Remaining > 0; ++childIndex)
            {
                int frameId = children[childIndex].Key;
                ContentionTreeNode child = children[childIndex].Value;
                --budget.Remaining;

                string frameName = frameId == NoStackFrameId ? NoStackLeafName : symbolTable.NameForId(frameId);
                int frameNameIndex = InternMethodName(frameName, methodNames, methodNameIndexByName);

                writer.WriteStartObject();
                writer.WriteNumber("frame", frameNameIndex);
                writer.WriteNumber("contentionCount", child.ContentionCount);
                writer.WriteNumber("totalWaitMSec", child.TotalWaitMSec);
                writer.WriteNumber("distinctStackCount", child.DistinctStackCount);
                WriteCallerTreeChildren(writer, child, symbolTable, methodNames, methodNameIndexByName, budget);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Contention)
