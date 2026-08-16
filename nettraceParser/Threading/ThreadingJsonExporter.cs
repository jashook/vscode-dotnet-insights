////////////////////////////////////////////////////////////////////////////////
// Module: ThreadingJsonExporter.cs
//
// Notes:
// Writes the "threadingSummary" block the Threading view consumes.
//
// The interesting part is WriteStallCorrelation. The thread-pool events cannot
// say WHERE a pool thread was stuck - 0 of the 12.2M WorkerThread/Wait events
// on the reference capture carry a stack. But the CPU sample profiler records
// (timestamp, threadId, stack) continuously, so the two can be joined on time:
// for every adjustment the runtime made because work was NOT progressing
// (Starvation / CooperativeBlocking), take the samples from just BEFORE that
// instant and ask what threads were actually doing. That turns "the pool grew"
// into "the pool grew while N threads sat in Interop+Sys.Read".
//
// Just before, not around - see AdjustmentLookbackMSec. Samples from after the
// adjustment describe the pool the decision produced rather than the one that
// forced it, which is the opposite of what this view is for.
//
// One correction the real data forced: the dominant leaf in every such window
// is LowLevelLifoSemaphore.WaitForSignal (2,397 of 3,256 samples in the first
// one examined). That is a pool worker PARKED waiting for work - the normal
// idle state, and the opposite of the thing being looked for. Counting it
// would bury the real culprits under it, so parked-worker frames are excluded
// and reported separately as an idle count.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Threading {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ThreadingJsonExporter
{
    private const int MaxStallFrames = 40;
    private const int MaxStallEvents = 100;
    private const int MaxAdjustmentsEmitted = 500;
    private const int MaxStackFramesPerEvent = 40;

    // How far BACK from an adjustment both sample-correlated views look: the
    // per-adjustment snapshot, and the aggregate "during pool stalls" frames.
    //
    // Strictly backward, never forward, and that direction is load-bearing
    // rather than a detail: both views exist to show the state that PRODUCED a
    // decision. A sample taken after the adjustment shows the state the
    // decision already caused - the injected thread is running, the work that
    // was queued has been picked up - so a window straddling the timestamp
    // answers a different question than the one being asked, while looking
    // like it answered this one.
    //
    // 3ms, chosen by sweeping the real capture rather than by feel. It is the
    // knee: everything above it buys nothing, and below it coverage collapses.
    //
    //   lookback | snapshots | threads sampled  | oldest stack
    //     1ms    |  480/500  | median 48, min 1 |  max 1.00ms
    //     2ms    |  500/500  | median 48, min 10|  max 1.98ms
    //     3ms    |  500/500  | median 48, min 43|  max 2.80ms
    //     5ms    |  500/500  | median 48, min 43|  max 2.80ms
    //    25ms    |  500/500  | median 48, min 43|  max 2.80ms
    //
    // From 3ms up the picture is byte-for-byte the same one - the extra window
    // catches no additional thread and no older stack, because every thread
    // has already been sampled within 2.8ms. The wider window was therefore
    // pure staleness risk with no coverage benefit. Below 3ms real threads
    // start dropping out, and at 1ms 20 adjustments lose their snapshot
    // entirely.
    //
    // This tracks EventPipe's SampleProfiler default of one sample per thread
    // per millisecond, so 3ms is ~3 sampling periods of slack. A capture
    // configured with a coarser interval degrades visibly rather than
    // silently: threads simply do not appear, threadsSampled falls, and an
    // adjustment with nothing in range reports a null snapshot.
    private const double AdjustmentLookbackMSec = 3.0;

    // Per-adjustment drill-down bounds. The snapshot keeps ONE stack per
    // thread (the sample nearest before the adjustment - see
    // BuildThreadSnapshots), so the natural size is the live thread count,
    // ~126 on the reference capture, collapsing to far fewer distinct stacks
    // once identical ones are grouped. These caps exist for a pathological
    // capture, not the normal one.
    private const int MaxStackGroupsPerAdjustment = 30;
    private const int MaxThreadIdsPerStackGroup = 25;

    // How far before a ThreadCreating event to look for the pool decision that
    // provides its context.
    //
    // Measured, not assumed: on the reference capture only 6 of 22 worker
    // creations have any adjustment within 100ms before them, 14 within 500ms
    // and 16 within 2s - after which the number stops growing even at 10s,
    // because the remaining 6 genuinely sit nowhere near a decision. So 2s is
    // where this stops buying anything, and a creation past it is reported as
    // having no nearby decision rather than being pinned on a stale one.
    private const double CreationAttributionWindowMSec = 2000.0;

    // A thread the POOL created, identified from the creation stack itself.
    // This is a recorded fact and the only reliable one available: the
    // adjustment counters cannot stand in for it (see
    // FindNearestPrecedingAdjustmentIndex).
    private static readonly string[] PoolWorkerCreationFramePrefixes = new string[]
    {
        "System.Threading.PortableThreadPool"
    };

    // A parked pool worker waiting to be handed work. Its presence in a stall
    // window means the pool had spare capacity, not that anything was blocked.
    private static readonly string[] ParkedWorkerFramePrefixes = new string[]
    {
        "System.Threading.LowLevelLifoSemaphore.",
        "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart"
    };

    public static void Write(Utf8JsonWriter writer, ThreadingSummary summary, List<SampleEvent> sampleEvents, StackTable stackTable, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        writer.WriteStartObject();

        writer.WriteBoolean("hasThreadPoolData", summary.HasThreadPoolData);

        if (!summary.HasThreadPoolData)
        {
            writer.WriteEndObject();
            return;
        }

        writer.WriteNumber("peakActiveWorkerThreads", summary.PeakActiveWorkerThreads);
        writer.WriteNumber("minActiveWorkerThreads", summary.MinActiveWorkerThreads);
        writer.WriteNumber("finalActiveWorkerThreads", summary.FinalActiveWorkerThreads);
        writer.WriteNumber("peakRetiredWorkerThreads", summary.PeakRetiredWorkerThreads);
        writer.WriteNumber("workerThreadStartCount", summary.WorkerThreadStartCount);
        writer.WriteNumber("workerThreadStopCount", summary.WorkerThreadStopCount);
        writer.WriteNumber("workerThreadWaitCount", summary.WorkerThreadWaitCount);
        writer.WriteNumber("threadCreationCount", summary.ThreadCreations.Count);
        writer.WriteNumber("lockCreationCount", summary.LockCreations.Count);
        writer.WriteNumber("adjustmentCount", summary.Adjustments.Count);

        WriteTimeline(writer, summary);
        WriteAdjustmentReasons(writer, summary);
        WriteAdjustments(writer, summary, sampleEvents, stackTable, symbolTable, methodNames, methodNameIndexByName);
        WriteStallCorrelation(writer, summary, sampleEvents, stackTable, symbolTable, methodNames, methodNameIndexByName);
        WriteStackedEvents(writer, "threadCreations", summary.ThreadCreations, summary.Adjustments, stackTable, symbolTable, methodNames, methodNameIndexByName);
        // Lock creations get no attribution: a lock is not created by a
        // thread-pool decision, so pairing one with the nearest adjustment
        // would be a coincidence dressed up as a cause.
        WriteStackedEvents(writer, "lockCreations", summary.LockCreations, null, stackTable, symbolTable, methodNames, methodNameIndexByName);

        writer.WriteEndObject();
    }

    private static void WriteTimeline(Utf8JsonWriter writer, ThreadingSummary summary)
    {
        writer.WritePropertyName("timeline");
        writer.WriteStartObject();
        writer.WriteNumber("minRelativeMSec", summary.MinRelativeMSec);
        writer.WriteNumber("totalDurationMSec", summary.MaxRelativeMSec - summary.MinRelativeMSec);
        writer.WriteNumber("bucketDurationMSec", summary.BucketDurationMSec);
        writer.WriteNumber("bucketCount", summary.BucketCount);

        WriteIntArray(writer, "minActiveByBucket", summary.MinActiveByBucket);
        WriteIntArray(writer, "maxActiveByBucket", summary.MaxActiveByBucket);
        WriteDoubleArray(writer, "averageActiveByBucket", summary.AverageActiveByBucket);
        WriteDoubleArray(writer, "throughputByBucket", summary.ThroughputByBucket);

        writer.WriteEndObject();
    }

    private static void WriteAdjustmentReasons(Utf8JsonWriter writer, ThreadingSummary summary)
    {
        Dictionary<int, int> countByReason = new Dictionary<int, int>();

        for (int adjustmentIndex = 0; adjustmentIndex < summary.Adjustments.Count; ++adjustmentIndex)
        {
            int reason = summary.Adjustments[adjustmentIndex].Reason;
            countByReason.TryGetValue(reason, out int count);
            countByReason[reason] = count + 1;
        }

        List<KeyValuePair<int, int>> reasons = new List<KeyValuePair<int, int>>(countByReason);
        reasons.Sort((KeyValuePair<int, int> left, KeyValuePair<int, int> right) => right.Value.CompareTo(left.Value));

        writer.WritePropertyName("adjustmentReasons");
        writer.WriteStartArray();

        for (int reasonIndex = 0; reasonIndex < reasons.Count; ++reasonIndex)
        {
            writer.WriteStartObject();
            writer.WriteNumber("reason", reasons[reasonIndex].Key);
            writer.WriteString("reasonName", ThreadAdjustmentReason.NameFor(reasons[reasonIndex].Key));
            writer.WriteNumber("count", reasons[reasonIndex].Value);
            // Flagged rather than left for the UI to re-derive: these are the
            // adjustments made because work stopped progressing, not because
            // more work arrived.
            writer.WriteBoolean("isStallDriven", ThreadAdjustmentReason.IsStallDriven(reasons[reasonIndex].Key));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // One thread's stack at the moment of one adjustment.
    private struct ThreadStackAtAdjustment
    {
        public double DistanceMSec;
        public int StackIndex;
    }

    // Identical stacks across threads, collapsed. On a real capture most of
    // the ~126 live threads are sitting in the same handful of stacks, so
    // listing them per thread would be the same few stacks repeated dozens of
    // times - the count IS the finding ("40 threads are all in SslStream.Read").
    private sealed class ThreadStackGroup
    {
        public int[] Frames;
        public bool IsParkedWorker;
        public List<long> ThreadIds = new List<long>();
    }

    // For each adjustment, what every thread was doing in the moments BEFORE
    // the runtime made that decision.
    //
    // "At the time of the decision" is taken literally in two ways. A thread
    // produces several samples inside the window, each potentially a different
    // stack, and averaging them would describe the window rather than the
    // instant - so each thread contributes exactly one stack, the LAST sample
    // before the adjustment. And the window never extends past the adjustment
    // itself, for the reason on AdjustmentLookbackMSec: samples after it show
    // the outcome, not the cause.
    //
    // Returns one entry per emitted adjustment, parallel to that slice, so a
    // caller can index it by adjustment index.
    private static List<ThreadStackGroup>[] BuildThreadSnapshots(List<ThreadPoolAdjustmentRecord> adjustments, int emittedCount, List<SampleEvent> sampleEvents, StackTable stackTable, MethodSymbolTable symbolTable, int[] threadsSampledByAdjustment, int[] parkedThreadsByAdjustment, double[] oldestSampleAgeByAdjustment)
    {
        List<ThreadStackGroup>[] groupsByAdjustment = new List<ThreadStackGroup>[emittedCount];

        if (emittedCount == 0 || sampleEvents.Count == 0)
        {
            return groupsByAdjustment;
        }

        Dictionary<long, ThreadStackAtAdjustment>[] closestByAdjustment = new Dictionary<long, ThreadStackAtAdjustment>[emittedCount];

        // Same single-ordered-pass shape as WriteStallCorrelation, and for the
        // same reason: 500 windows x 19.7M samples is not a scan anyone can
        // afford. Adjustments arrive in event order, so they are already
        // sorted by time and a cursor over them is enough.
        Span<SampleEvent> samplesSpan = CollectionsMarshal.AsSpan(sampleEvents);
        int windowCursor = 0;

        for (int sampleIndex = 0; sampleIndex < samplesSpan.Length; ++sampleIndex)
        {
            ref readonly SampleEvent sampleEvent = ref samplesSpan[sampleIndex];
            double sampleTime = sampleEvent.RelativeMSec;

            // An adjustment is retired once the samples have passed it: its
            // window is [adjustment - lookback, adjustment], so nothing later
            // can ever fall inside it.
            while (windowCursor < emittedCount && adjustments[windowCursor].RelativeMSec < sampleTime)
            {
                ++windowCursor;
            }

            if (windowCursor >= emittedCount)
            {
                break;
            }

            if (stackTable.FramesAt(sampleEvent.StackIndex).Length == 0)
            {
                continue;
            }

            // Windows overlap whenever two adjustments are closer together
            // than the lookback, so a sample can belong to several. Every
            // window from the cursor on has its adjustment at or after this
            // sample (that is what the retire loop above guarantees), so only
            // the lower bound still needs checking.
            for (int windowIndex = windowCursor; windowIndex < emittedCount; ++windowIndex)
            {
                double adjustmentTime = adjustments[windowIndex].RelativeMSec;

                if (adjustmentTime - AdjustmentLookbackMSec > sampleTime)
                {
                    break;
                }

                double distanceMSec = adjustmentTime - sampleTime;

                Dictionary<long, ThreadStackAtAdjustment> closestByThreadId = closestByAdjustment[windowIndex];

                if (closestByThreadId == null)
                {
                    closestByThreadId = new Dictionary<long, ThreadStackAtAdjustment>();
                    closestByAdjustment[windowIndex] = closestByThreadId;
                }

                if (closestByThreadId.TryGetValue(sampleEvent.ThreadId, out ThreadStackAtAdjustment existing)
                    && existing.DistanceMSec <= distanceMSec)
                {
                    continue;
                }

                ThreadStackAtAdjustment closest;
                closest.DistanceMSec = distanceMSec;
                closest.StackIndex = sampleEvent.StackIndex;
                closestByThreadId[sampleEvent.ThreadId] = closest;
            }
        }

        int[] frameBuffer = new int[MaxStackFramesPerEvent];

        for (int adjustmentIndex = 0; adjustmentIndex < emittedCount; ++adjustmentIndex)
        {
            Dictionary<long, ThreadStackAtAdjustment> closestByThreadId = closestByAdjustment[adjustmentIndex];

            if (closestByThreadId == null)
            {
                continue;
            }

            groupsByAdjustment[adjustmentIndex] = GroupThreadStacks(closestByThreadId, adjustments[adjustmentIndex].RelativeMSec, stackTable, symbolTable, frameBuffer, out int threadsSampled, out int parkedThreads);
            threadsSampledByAdjustment[adjustmentIndex] = threadsSampled;
            parkedThreadsByAdjustment[adjustmentIndex] = parkedThreads;

            // The staleness of the WORST stack in this snapshot, so the view
            // can state how old the evidence actually is instead of quoting
            // the window size and letting the reader assume the best case.
            double oldestSampleAgeMSec = 0;

            foreach (KeyValuePair<long, ThreadStackAtAdjustment> threadEntry in closestByThreadId)
            {
                if (threadEntry.Value.DistanceMSec > oldestSampleAgeMSec)
                {
                    oldestSampleAgeMSec = threadEntry.Value.DistanceMSec;
                }
            }

            oldestSampleAgeByAdjustment[adjustmentIndex] = oldestSampleAgeMSec;
        }

        return groupsByAdjustment;
    }

    private static List<ThreadStackGroup> GroupThreadStacks(Dictionary<long, ThreadStackAtAdjustment> closestByThreadId, double adjustmentTimeMSec, StackTable stackTable, MethodSymbolTable symbolTable, int[] frameBuffer, out int threadsSampled, out int parkedThreads)
    {
        List<ThreadStackGroup> groups = new List<ThreadStackGroup>();
        // Hash bucket -> indices into groups. Hashing the resolved frame ids
        // rather than formatting a string key per thread: the key is only ever
        // used to find candidates, and the frames are then compared exactly,
        // so a collision costs one extra comparison and can never merge two
        // different stacks.
        Dictionary<long, List<int>> groupIndicesByHash = new Dictionary<long, List<int>>();

        threadsSampled = 0;
        parkedThreads = 0;

        foreach (KeyValuePair<long, ThreadStackAtAdjustment> threadEntry in closestByThreadId)
        {
            ++threadsSampled;

            long[] stack = stackTable.FramesAt(threadEntry.Value.StackIndex);
            int frameCount = stack.Length < MaxStackFramesPerEvent ? stack.Length : MaxStackFramesPerEvent;

            long hash = 1469598103934665603L;

            for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
            {
                int frameId = symbolTable.ResolveId(stack[frameIndex], adjustmentTimeMSec);
                frameBuffer[frameIndex] = frameId;
                hash = (hash ^ frameId) * 1099511628211L;
            }

            bool isParkedWorker = frameCount > 0 && IsParkedWorkerFrame(symbolTable.NameForId(frameBuffer[0]));

            if (isParkedWorker)
            {
                ++parkedThreads;
            }

            int matchedGroupIndex = -1;

            if (groupIndicesByHash.TryGetValue(hash, out List<int> candidateIndices))
            {
                for (int candidateIndex = 0; candidateIndex < candidateIndices.Count; ++candidateIndex)
                {
                    if (FramesEqual(groups[candidateIndices[candidateIndex]].Frames, frameBuffer, frameCount))
                    {
                        matchedGroupIndex = candidateIndices[candidateIndex];
                        break;
                    }
                }
            }
            else
            {
                candidateIndices = new List<int>();
                groupIndicesByHash[hash] = candidateIndices;
            }

            if (matchedGroupIndex < 0)
            {
                ThreadStackGroup group = new ThreadStackGroup();
                group.Frames = new int[frameCount];
                Array.Copy(frameBuffer, group.Frames, frameCount);
                group.IsParkedWorker = isParkedWorker;

                matchedGroupIndex = groups.Count;
                groups.Add(group);
                candidateIndices.Add(matchedGroupIndex);
            }

            groups[matchedGroupIndex].ThreadIds.Add(threadEntry.Key);
        }

        // Running threads first, then by how many threads share the stack.
        // Parked workers are sorted to the bottom rather than dropped: they
        // are the pool's idle state, so a snapshot that is mostly parked
        // threads means the pool had spare capacity - a real answer to "why
        // did it add a thread", just not the one being looked for.
        groups.Sort(CompareThreadStackGroups);

        return groups;
    }

    private static int CompareThreadStackGroups(ThreadStackGroup left, ThreadStackGroup right)
    {
        if (left.IsParkedWorker != right.IsParkedWorker)
        {
            return left.IsParkedWorker ? 1 : -1;
        }

        return right.ThreadIds.Count.CompareTo(left.ThreadIds.Count);
    }

    private static bool FramesEqual(int[] groupFrames, int[] candidateFrames, int candidateFrameCount)
    {
        if (groupFrames.Length != candidateFrameCount)
        {
            return false;
        }

        for (int frameIndex = 0; frameIndex < candidateFrameCount; ++frameIndex)
        {
            if (groupFrames[frameIndex] != candidateFrames[frameIndex])
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteAdjustments(Utf8JsonWriter writer, ThreadingSummary summary, List<SampleEvent> sampleEvents, StackTable stackTable, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        int emitted = summary.Adjustments.Count < MaxAdjustmentsEmitted ? summary.Adjustments.Count : MaxAdjustmentsEmitted;

        int[] threadsSampledByAdjustment = new int[emitted];
        int[] parkedThreadsByAdjustment = new int[emitted];
        double[] oldestSampleAgeByAdjustment = new double[emitted];
        List<ThreadStackGroup>[] groupsByAdjustment = BuildThreadSnapshots(summary.Adjustments, emitted, sampleEvents, stackTable, symbolTable, threadsSampledByAdjustment, parkedThreadsByAdjustment, oldestSampleAgeByAdjustment);

        writer.WritePropertyName("adjustments");
        writer.WriteStartArray();

        for (int adjustmentIndex = 0; adjustmentIndex < emitted; ++adjustmentIndex)
        {
            ThreadPoolAdjustmentRecord adjustment = summary.Adjustments[adjustmentIndex];

            writer.WriteStartObject();
            writer.WriteNumber("relativeMSec", adjustment.RelativeMSec);
            writer.WriteNumber("newWorkerThreadCount", adjustment.NewWorkerThreadCount);
            // The change this decision made, not just the resulting count -
            // "went to 63" doesn't say whether the pool grew or shrank, which
            // is the whole point of reading an adjustment.
            writer.WriteNumber("workerThreadDelta", adjustmentIndex > 0
                ? adjustment.NewWorkerThreadCount - summary.Adjustments[adjustmentIndex - 1].NewWorkerThreadCount
                : 0);
            writer.WriteNumber("reason", adjustment.Reason);
            writer.WriteString("reasonName", ThreadAdjustmentReason.NameFor(adjustment.Reason));
            writer.WriteBoolean("isStallDriven", ThreadAdjustmentReason.IsStallDriven(adjustment.Reason));
            writer.WriteNumber("averageThroughput", adjustment.AverageThroughput);

            WriteThreadSnapshot(writer, groupsByAdjustment[adjustmentIndex], threadsSampledByAdjustment[adjustmentIndex], parkedThreadsByAdjustment[adjustmentIndex], oldestSampleAgeByAdjustment[adjustmentIndex], symbolTable, methodNames, methodNameIndexByName);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteThreadSnapshot(Utf8JsonWriter writer, List<ThreadStackGroup> groups, int threadsSampled, int parkedThreads, double oldestSampleAgeMSec, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        writer.WritePropertyName("threadSnapshot");

        if (groups == null || groups.Count == 0)
        {
            // Null rather than an empty snapshot: no CPU samples landed in
            // this adjustment's window, which is different from "every thread
            // was idle". The UI says so instead of drawing an empty table.
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("threadsSampled", threadsSampled);
        writer.WriteNumber("parkedThreadCount", parkedThreads);
        writer.WriteNumber("stackGroupCount", groups.Count);
        // Both emitted so the view can state the provenance of these stacks
        // concretely: they are CPU samples from within lookbackMSec BEFORE the
        // adjustment, the oldest of them oldestSampleAgeMSec old, not a stack
        // dump taken at the instant of the decision.
        writer.WriteNumber("lookbackMSec", AdjustmentLookbackMSec);
        writer.WriteNumber("oldestSampleAgeMSec", oldestSampleAgeMSec);

        writer.WritePropertyName("stacks");
        writer.WriteStartArray();

        int groupsToEmit = groups.Count < MaxStackGroupsPerAdjustment ? groups.Count : MaxStackGroupsPerAdjustment;

        for (int groupIndex = 0; groupIndex < groupsToEmit; ++groupIndex)
        {
            ThreadStackGroup group = groups[groupIndex];

            writer.WriteStartObject();
            writer.WriteNumber("threadCount", group.ThreadIds.Count);
            writer.WriteBoolean("isParkedWorker", group.IsParkedWorker);

            writer.WritePropertyName("threadIds");
            writer.WriteStartArray();

            int threadIdsToEmit = group.ThreadIds.Count < MaxThreadIdsPerStackGroup ? group.ThreadIds.Count : MaxThreadIdsPerStackGroup;

            for (int threadIdIndex = 0; threadIdIndex < threadIdsToEmit; ++threadIdIndex)
            {
                writer.WriteNumberValue(group.ThreadIds[threadIdIndex]);
            }

            writer.WriteEndArray();

            writer.WritePropertyName("frames");
            writer.WriteStartArray();

            for (int frameIndex = 0; frameIndex < group.Frames.Length; ++frameIndex)
            {
                writer.WriteNumberValue(InternMethodName(symbolTable.NameForId(group.Frames[frameIndex]), methodNames, methodNameIndexByName));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // Joins CPU samples to stall-driven adjustments on time - see this file's
    // header for why this exists at all.
    private static void WriteStallCorrelation(Utf8JsonWriter writer, ThreadingSummary summary, List<SampleEvent> sampleEvents, StackTable stackTable, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        List<ThreadPoolAdjustmentRecord> stallAdjustments = new List<ThreadPoolAdjustmentRecord>();

        for (int adjustmentIndex = 0; adjustmentIndex < summary.Adjustments.Count; ++adjustmentIndex)
        {
            if (ThreadAdjustmentReason.IsStallDriven(summary.Adjustments[adjustmentIndex].Reason))
            {
                stallAdjustments.Add(summary.Adjustments[adjustmentIndex]);
            }
        }

        writer.WritePropertyName("stallCorrelation");

        if (stallAdjustments.Count == 0 || sampleEvents.Count == 0)
        {
            // Null, not an empty object: "no stalls happened" and "there were
            // no CPU samples to explain them with" are both real answers, and
            // an empty table would imply the pool stalled with nothing running.
            writer.WriteNullValue();
            return;
        }

        stallAdjustments.Sort((ThreadPoolAdjustmentRecord left, ThreadPoolAdjustmentRecord right) => left.RelativeMSec.CompareTo(right.RelativeMSec));

        int windowCount = stallAdjustments.Count < MaxStallEvents ? stallAdjustments.Count : MaxStallEvents;

        Dictionary<int, int> samplesByFrameId = new Dictionary<int, int>();
        HashSet<long> threadsInWindows = new HashSet<long>();
        long totalSamplesInWindows = 0;
        long parkedWorkerSamples = 0;

        // Single ordered pass over the samples rather than one scan per
        // adjustment: 54 adjustments x 19.7M samples would be a billion
        // comparisons. Samples are already in time order (they are projected
        // in event order), so a cursor over the sorted windows is enough.
        Span<SampleEvent> samplesSpan = CollectionsMarshal.AsSpan(sampleEvents);
        int windowCursor = 0;

        for (int sampleIndex = 0; sampleIndex < samplesSpan.Length; ++sampleIndex)
        {
            ref readonly SampleEvent sampleEvent = ref samplesSpan[sampleIndex];
            double sampleTime = sampleEvent.RelativeMSec;

            // Retire windows this sample has already passed. Each window is
            // [adjustment - lookback, adjustment], so once the samples are
            // past an adjustment nothing later can fall inside it.
            while (windowCursor < windowCount && stallAdjustments[windowCursor].RelativeMSec < sampleTime)
            {
                ++windowCursor;
            }

            if (windowCursor >= windowCount)
            {
                break;
            }

            // Only the cursor's own window needs checking. Every window from
            // here on has its adjustment at or after this sample, and their
            // adjustment times only increase - so if the nearest one's
            // look-back does not reach back to this sample, no later one's
            // can either.
            bool inAnyWindow = stallAdjustments[windowCursor].RelativeMSec - AdjustmentLookbackMSec <= sampleTime;

            if (!inAnyWindow)
            {
                continue;
            }

            ++totalSamplesInWindows;
            threadsInWindows.Add(sampleEvent.ThreadId);

            long[] sampleFrames = stackTable.FramesAt(sampleEvent.StackIndex);
            if (sampleFrames.Length == 0)
            {
                continue;
            }

            int leafFrameId = symbolTable.ResolveId(sampleFrames[0], sampleTime);
            string leafName = symbolTable.NameForId(leafFrameId);

            if (IsParkedWorkerFrame(leafName))
            {
                ++parkedWorkerSamples;
                continue;
            }

            samplesByFrameId.TryGetValue(leafFrameId, out int frameCount);
            samplesByFrameId[leafFrameId] = frameCount + 1;
        }

        List<KeyValuePair<int, int>> rankedFrames = new List<KeyValuePair<int, int>>(samplesByFrameId);
        rankedFrames.Sort((KeyValuePair<int, int> left, KeyValuePair<int, int> right) => right.Value.CompareTo(left.Value));

        int frameCountToEmit = rankedFrames.Count < MaxStallFrames ? rankedFrames.Count : MaxStallFrames;

        writer.WriteStartObject();
        writer.WriteNumber("stallAdjustmentCount", stallAdjustments.Count);
        // Renamed from windowHalfWidthMSec along with the window itself: this
        // is a look-back before each adjustment now, not a half-width around
        // it, and a stale name here would have the view claim symmetry the
        // data no longer has.
        writer.WriteNumber("lookbackMSec", AdjustmentLookbackMSec);
        writer.WriteNumber("samplesInWindows", totalSamplesInWindows);
        writer.WriteNumber("threadsInWindows", threadsInWindows.Count);
        // Reported rather than silently dropped: a window that is mostly
        // parked workers means the pool had spare capacity, which is itself
        // worth knowing when reading the frames below.
        writer.WriteNumber("parkedWorkerSamples", parkedWorkerSamples);

        writer.WritePropertyName("frames");
        writer.WriteStartArray();

        for (int frameIndex = 0; frameIndex < frameCountToEmit; ++frameIndex)
        {
            string frameName = symbolTable.NameForId(rankedFrames[frameIndex].Key);

            writer.WriteStartObject();
            writer.WriteNumber("frame", InternMethodName(frameName, methodNames, methodNameIndexByName));
            writer.WriteNumber("sampleCount", rankedFrames[frameIndex].Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool IsParkedWorkerFrame(string frameName)
    {
        if (string.IsNullOrEmpty(frameName))
        {
            return false;
        }

        for (int prefixIndex = 0; prefixIndex < ParkedWorkerFramePrefixes.Length; ++prefixIndex)
        {
            if (frameName.StartsWith(ParkedWorkerFramePrefixes[prefixIndex], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPoolWorkerCreationStack(long[] stack, MethodSymbolTable symbolTable, double relativeMSec)
    {
        for (int frameIndex = 0; frameIndex < stack.Length; ++frameIndex)
        {
            string frameName = symbolTable.NameForId(symbolTable.ResolveId(stack[frameIndex], relativeMSec));

            if (frameName == null)
            {
                continue;
            }

            for (int prefixIndex = 0; prefixIndex < PoolWorkerCreationFramePrefixes.Length; ++prefixIndex)
            {
                if (frameName.StartsWith(PoolWorkerCreationFramePrefixes[prefixIndex], StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The pool decision closest in time before this thread was created.
    //
    // ThreadCreating carries no reason of its own: the runtime logs the
    // hill-climbing DECISION (an adjustment, with its reason) and creates the
    // thread as two unrelated events, with no correlation id between them. So
    // the best available context is the most recent decision - and it is
    // reported as exactly that, alongside the elapsed time, rather than as a
    // proven cause.
    //
    // An earlier version of this required the adjustment to have RAISED
    // NewWorkerThreadCount, on the reasoning that a decision which lowered it
    // cannot have created a thread. The real data killed that: the counter
    // oscillates by +/-20 between 64 and 84 on consecutive "Climbing move"
    // adjustments, so it is hill climbing's TARGET rather than a live count,
    // and a worker creation 49ms after a "-20" adjustment is perfectly normal.
    // The filter's only effect was to label threads whose own stack reads
    // PortableThreadPool.CreateWorkerThread as "not pool-driven", which the
    // stack plainly contradicts. Pool ownership now comes from the stack (see
    // IsPoolWorkerCreationStack) and the adjustment supplies only the reason.
    //
    // Returns the index into adjustments, or -1 when none falls inside the
    // window - a real answer, not a failure.
    private static int FindNearestPrecedingAdjustmentIndex(List<ThreadPoolAdjustmentRecord> adjustments, double creationTimeMSec, ref int searchCursor)
    {
        // Both lists are in time order, so the cursor only ever moves forward
        // across the whole loop - this stays linear rather than rescanning the
        // adjustment list per creation.
        while (searchCursor < adjustments.Count && adjustments[searchCursor].RelativeMSec <= creationTimeMSec)
        {
            ++searchCursor;
        }

        int nearestIndex = searchCursor - 1;

        if (nearestIndex < 0)
        {
            return -1;
        }

        if (creationTimeMSec - adjustments[nearestIndex].RelativeMSec > CreationAttributionWindowMSec)
        {
            return -1;
        }

        return nearestIndex;
    }

    private static void WriteStackedEvents(Utf8JsonWriter writer, string propertyName, List<StackedThreadingEvent> stackedEvents, List<ThreadPoolAdjustmentRecord> adjustmentsForAttribution, StackTable stackTable, MethodSymbolTable symbolTable, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();

        int adjustmentSearchCursor = 0;

        for (int eventIndex = 0; eventIndex < stackedEvents.Count; ++eventIndex)
        {
            StackedThreadingEvent stackedEvent = stackedEvents[eventIndex];

            writer.WriteStartObject();
            writer.WriteNumber("relativeMSec", stackedEvent.RelativeMSec);
            writer.WriteNumber("threadId", stackedEvent.ThreadId);
            writer.WriteString("objectId", "0x" + stackedEvent.ObjectId.ToString("X"));

            if (adjustmentsForAttribution != null)
            {
                // Whether the POOL created this thread is a fact read off the
                // stack; the adjustment below only supplies the reason.
                writer.WriteBoolean("isPoolWorker", IsPoolWorkerCreationStack(stackTable.FramesAt(stackedEvent.StackIndex), symbolTable, stackedEvent.RelativeMSec));

                int causingAdjustmentIndex = FindNearestPrecedingAdjustmentIndex(adjustmentsForAttribution, stackedEvent.RelativeMSec, ref adjustmentSearchCursor);

                if (causingAdjustmentIndex >= 0)
                {
                    ThreadPoolAdjustmentRecord causingAdjustment = adjustmentsForAttribution[causingAdjustmentIndex];

                    writer.WriteNumber("causeAdjustmentIndex", causingAdjustmentIndex);
                    writer.WriteNumber("causeReason", causingAdjustment.Reason);
                    writer.WriteString("causeReasonName", ThreadAdjustmentReason.NameFor(causingAdjustment.Reason));
                    writer.WriteBoolean("causeIsStallDriven", ThreadAdjustmentReason.IsStallDriven(causingAdjustment.Reason));
                    writer.WriteNumber("causeDelayMSec", stackedEvent.RelativeMSec - causingAdjustment.RelativeMSec);
                }
                else
                {
                    writer.WriteNumber("causeAdjustmentIndex", -1);
                }
            }

            writer.WritePropertyName("frames");
            writer.WriteStartArray();

            long[] stackedFrames = stackTable.FramesAt(stackedEvent.StackIndex);
            int frameCount = stackedFrames.Length < MaxStackFramesPerEvent ? stackedFrames.Length : MaxStackFramesPerEvent;

            for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
            {
                string frameName = symbolTable.NameForId(symbolTable.ResolveId(stackedFrames[frameIndex], stackedEvent.RelativeMSec));
                writer.WriteNumberValue(InternMethodName(frameName, methodNames, methodNameIndexByName));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static int InternMethodName(string frameName, List<string> methodNames, Dictionary<string, int> methodNameIndexByName)
    {
        string safeName = frameName ?? "<unresolved>";
        int frameNameIndex;

        if (!methodNameIndexByName.TryGetValue(safeName, out frameNameIndex))
        {
            frameNameIndex = methodNames.Count;
            methodNames.Add(safeName);
            methodNameIndexByName[safeName] = frameNameIndex;
        }

        return frameNameIndex;
    }

    private static void WriteIntArray(Utf8JsonWriter writer, string propertyName, int[] values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();

        for (int valueIndex = 0; valueIndex < values.Length; ++valueIndex)
        {
            writer.WriteNumberValue(values[valueIndex]);
        }

        writer.WriteEndArray();
    }

    private static void WriteDoubleArray(Utf8JsonWriter writer, string propertyName, double[] values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();

        for (int valueIndex = 0; valueIndex < values.Length; ++valueIndex)
        {
            writer.WriteNumberValue(values[valueIndex]);
        }

        writer.WriteEndArray();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Threading)
