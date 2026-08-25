////////////////////////////////////////////////////////////////////////////////
// Module: ThreadingRules.cs
//
// Notes:
// Three rules over the thread classification.
//
// These read ThreadActivityProfiler's roles rather than re-deriving anything,
// and that is the whole point. Deciding whether a thread is stuck is genuinely
// hard - the header of Threading/ThreadActivityProfiler.cs exists because
// several plausible ways of doing it were tried and measured wrong against
// real service captures - and a rule that second-guessed it here would be
// re-opening a question that has already been settled with data.
//
// So the load-bearing distinction, which the roles already encode: a POOL
// WORKER parked outside the pool's own park is the finding, while a dedicated
// application thread that looks identical is benign. Four Kafka consumers
// running a synchronous Consume inside an ExecuteAsync, each pinned for a
// whole 300-second capture, are behaviourally indistinguishable from a benign
// drain worker parked on an empty queue - the difference is entirely whose
// thread they are standing on. ThreadingPoolStarvationRule therefore counts
// BlockedPoolWorker and nothing else.
//
// ONE OF THESE RULES IS A NAME HEURISTIC, and it says so in its own output.
// See ThreadingSyncOverAsyncRule for why it is included anyway and what keeps
// it honest.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights.Rules {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Analysis;
using DotnetInsights.NetTrace.Threading;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

internal static class ThreadingRuleGate
{
    public const string NoThreadProfiles = "this capture produced no per-thread CPU sample profiles";

    public static bool HasThreads(CaptureAnalysis analysis)
    {
        return analysis.Threading.ThreadCount > 0;
    }

    // Keyed on the enum ordinal carried in RoleId, never on the display name.
    // NameForRole's strings are display text and are free to be reworded; a
    // rule matching on them would stop firing silently the first time one is,
    // which is the kind of break that shows up as "the tool stopped finding
    // starvation" months later.
    public static bool IsBlockedPoolWorker(ThreadRecord thread)
    {
        return thread.RoleId == (int)ThreadActivityRole.BlockedPoolWorker;
    }

    // The leaf of this thread's highest-share stack that is NOT the thread
    // pool's own park, or null when every stack it was sampled in was the
    // park. Null is a real answer and is skipped rather than substituted: a
    // worker whose recorded stacks are all the park has nothing to name, and
    // inventing the park as its blocking site would be worse than saying
    // nothing.
    public static string DominantNonParkLeafFrame(ThreadRecord thread)
    {
        for (int stackRank = 0; stackRank < thread.TopStacks.Count; ++stackRank)
        {
            string leafFrame = thread.TopStacks[stackRank].LeafFrame;

            if (leafFrame.Length > 0 && !ThreadActivityProfiler.IsPoolParkFrame(leafFrame))
            {
                return leafFrame;
            }
        }

        return null;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ThreadingPoolStarvationRule : InsightRule
{
    public const int CriticalWorkerCount = 3;

    public override string Id => "threading/pool-starvation";

    public override string Category => "Threading";

    public override string Threshold =>
        "any thread classified as a blocked pool worker - a thread-pool worker held somewhere other than the pool's own park ("
        + CriticalWorkerCount + " or more is critical)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!ThreadingRuleGate.HasThreads(analysis))
        {
            return this.NotApplicable(ThreadingRuleGate.NoThreadProfiles);
        }

        int blockedWorkers = analysis.Threading.BlockedPoolWorkerCount;

        string measured = FormatCount(blockedWorkers) + " blocked pool workers of "
            + FormatCount(analysis.Threading.ThreadCount) + " threads";

        if (blockedWorkers == 0)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Blocked pool workers", FormatCount(blockedWorkers), blockedWorkers));
        evidence.Add(new InsightEvidence("Threads observed", FormatCount(analysis.Threading.ThreadCount), analysis.Threading.ThreadCount));
        evidence.Add(new InsightEvidence("Idle pool workers", FormatCount(analysis.Threading.IdlePoolWorkerCount), analysis.Threading.IdlePoolWorkerCount));

        // The stacks are the finding. A count says the pool is starved; the
        // stack says what it is starved ON, and that is what somebody can act
        // on this afternoon.
        //
        // Ranked on each thread's dominant NON-PARK stack, not on its dominant
        // stack. A blocked worker still parks between the blocking calls, and
        // on a real capture that park is frequently its single most-sampled
        // stack even though it failed the park-share gate on the balance -
        // measured on a 518-thread service capture, where reporting the top
        // stack named System.Threading.LowLevelLifoSemaphore.WaitForSignal for
        // four of six blocked workers. That frame is the pool's own park: it
        // is the one place in the process that is definitionally not the
        // problem, and pointing a reader at it wastes the whole finding.
        Dictionary<string, int> countByBlockingFrame = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int threadIndex = 0; threadIndex < analysis.Threading.Threads.Count; ++threadIndex)
        {
            ThreadRecord thread = analysis.Threading.Threads[threadIndex];

            if (!ThreadingRuleGate.IsBlockedPoolWorker(thread))
            {
                continue;
            }

            string blockingFrame = ThreadingRuleGate.DominantNonParkLeafFrame(thread);

            if (blockingFrame == null)
            {
                continue;
            }

            countByBlockingFrame.TryGetValue(blockingFrame, out int existing);
            countByBlockingFrame[blockingFrame] = existing + 1;
        }

        List<KeyValuePair<string, int>> rankedFrames = new List<KeyValuePair<string, int>>(countByBlockingFrame);
        rankedFrames.Sort(CompareFrameCountsDescending);

        for (int frameIndex = 0; frameIndex < rankedFrames.Count && frameIndex < 4; ++frameIndex)
        {
            evidence.Add(new InsightEvidence(
                "Blocked in",
                rankedFrames[frameIndex].Key + " (" + rankedFrames[frameIndex].Value + " thread" + (rankedFrames[frameIndex].Value == 1 ? "" : "s") + ")",
                rankedFrames[frameIndex].Value));
        }

        string leadFrame = rankedFrames.Count > 0 ? rankedFrames[0].Key : "an unresolved frame";

        // The pool's own hill-climbing agreeing is a second, independent
        // signal - worth saying out loud when it is there, because two
        // signals agreeing is what separates a finding from a threshold that
        // happened to trip.
        string corroboration = analysis.Threading.StallDrivenAdjustmentCount > 0
            ? " The thread pool's own hill-climbing agrees: it made "
              + FormatCount(analysis.Threading.StallDrivenAdjustmentCount)
              + " starvation-driven adjustments during this capture."
            : "";

        return this.Fire(
            blockedWorkers >= CriticalWorkerCount ? InsightSeverity.Critical : InsightSeverity.Warning,
            InsightConfidence.High,
            FormatCount(blockedWorkers) + " thread-pool workers are blocked outside the pool's own park, mostly in " + leadFrame,
            "A pool worker parked in the pool's semaphore is idle capacity and costs nothing. These are parked "
            + "somewhere else, which means the pool cannot reuse them: it responds by injecting more threads, "
            + "slowly, one at a time. Work queued behind that injection waits."
            + corroboration
            + " Note that a dedicated application thread sitting in the same call would be benign - what makes this "
            + "a finding is that these are the pool's threads.",
            1.0 + (double)blockedWorkers / CriticalWorkerCount,
            evidence,
            new List<string>
            {
                "Find the synchronous call in " + leadFrame + " and make it asynchronous, or move it off the thread pool onto a dedicated thread.",
                "Look for .Result, .Wait() or .GetAwaiter().GetResult() on the path into it."
            },
            new List<InsightLink> { new InsightLink("threading", "Threading view") },
            measured);
    }

    private static int CompareFrameCountsDescending(KeyValuePair<string, int> left, KeyValuePair<string, int> right)
    {
        int byCount = right.Value.CompareTo(left.Value);

        if (byCount != 0)
        {
            return byCount;
        }

        return string.CompareOrdinal(left.Key, right.Key);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// The pool's own bookkeeping, independent of any sample classification. Worth
// its own rule rather than being folded into the starvation rule above: a
// capture can show stall-driven injection with no blocked worker visible in
// the samples (the blocking happened between sample intervals, or the thread
// count grew before the capture started), and that is still a real signal
// that the pool could not keep up.
public sealed class ThreadingStallDrivenInjectionRule : InsightRule
{
    public const int WarningAdjustmentCount = 5;

    public override string Id => "threading/stall-driven-injection";

    public override string Category => "Threading";

    public override string Threshold =>
        "any thread-pool adjustment with a starvation reason code (" + WarningAdjustmentCount + " or more is a warning)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!analysis.Threading.HasThreadPoolData)
        {
            return this.NotApplicable("this capture recorded no thread-pool events (the ThreadPool keyword was not collected)");
        }

        int stallAdjustments = analysis.Threading.StallDrivenAdjustmentCount;

        string measured = FormatCount(stallAdjustments) + " starvation-driven adjustments of "
            + FormatCount(analysis.Threading.AdjustmentCount) + " total";

        if (stallAdjustments == 0)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Starvation-driven adjustments", FormatCount(stallAdjustments), stallAdjustments));
        evidence.Add(new InsightEvidence("Total adjustments", FormatCount(analysis.Threading.AdjustmentCount), analysis.Threading.AdjustmentCount));
        evidence.Add(new InsightEvidence("Worker threads", "min " + analysis.Threading.MinActiveWorkerThreads + ", peak " + analysis.Threading.PeakActiveWorkerThreads, analysis.Threading.PeakActiveWorkerThreads));
        evidence.Add(new InsightEvidence("Worker growth", "+" + FormatCount(analysis.Threading.WorkerThreadGrowth), analysis.Threading.WorkerThreadGrowth));

        return this.Fire(
            stallAdjustments >= WarningAdjustmentCount ? InsightSeverity.Warning : InsightSeverity.Info,
            InsightConfidence.High,
            "The thread pool injected threads " + FormatCount(stallAdjustments) + " times because queued work was not being picked up",
            "This is the runtime's own accounting, not an inference from samples: the pool's hill-climbing "
            + "algorithm recorded that work sat in the queue while every worker was busy, and responded by adding "
            + "threads. It adds them slowly - roughly one per interval - so latency spikes while the pool catches "
            + "up. The worker count went from " + analysis.Threading.MinActiveWorkerThreads + " to "
            + analysis.Threading.PeakActiveWorkerThreads + " during this capture.",
            1.0 + (double)stallAdjustments / WarningAdjustmentCount,
            evidence,
            new List<string>
            {
                "Find what the pool's workers were blocked on - the Threading view correlates stall windows with the stacks running during them.",
                "Raising ThreadPool.SetMinThreads hides the symptom; it does not remove the blocking call causing it."
            },
            new List<InsightLink> { new InsightLink("threading", "Threading view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A METHOD-NAME HEURISTIC, deliberately, and the one place in this rule set
// that uses one. Threading/ThreadActivityProfiler.cs's header exists to warn
// against exactly this technique, so the reasons it is acceptable here are
// worth stating precisely:
//
//   - It is scoped PER THREAD, never per frame. The unit is "this thread,
//     which the profiler already classified as a blocked pool worker, has
//     these frames on its dominant stack" - not "any stack containing this
//     name is suspicious". The per-frame version of this test is the one that
//     is provably wrong: Confluent.Kafka.Consumer.Consume appears both on
//     threads doing real work and on threads parked in it.
//   - It only ever EXPLAINS an existing finding. It cannot fire unless
//     ThreadingPoolStarvationRule already had a blocked pool worker to report,
//     so a false positive adds a wrong explanation to a real problem rather
//     than inventing a problem.
//   - It ships at medium confidence and says in its own detail text that it
//     is name-based, so a reader can discount it.
//
// The markers themselves are the BCL's own blocking-on-a-Task entry points,
// which is a closed set the framework controls - unlike the open-ended set of
// third-party library calls that make leaf-name classification unworkable in
// general.
public sealed class ThreadingSyncOverAsyncRule : InsightRule
{
    private static readonly string[] SyncOverAsyncFrameMarkers = new string[]
    {
        "System.Threading.Tasks.Task.Wait",
        "System.Threading.Tasks.Task.InternalWaitCore",
        "System.Threading.Tasks.Task.SpinThenBlockingWait",
        "System.Threading.Tasks.Task.get_Result",
        "System.Runtime.CompilerServices.TaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification",
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable",
        "System.Threading.Tasks.ValueTask.GetAwaiter",
        "System.Threading.ManualResetEventSlim.Wait"
    };

    public override string Id => "threading/sync-over-async";

    public override string Category => "Threading";

    public override string Threshold =>
        "a thread already classified as a blocked pool worker whose dominant stack contains a BCL blocking-on-Task frame (Task.Wait, TaskAwaiter.GetResult, Task.get_Result, ...)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!ThreadingRuleGate.HasThreads(analysis))
        {
            return this.NotApplicable(ThreadingRuleGate.NoThreadProfiles);
        }

        if (analysis.Threading.BlockedPoolWorkerCount == 0)
        {
            return this.NotApplicable("no thread in this capture is a blocked pool worker, so there is nothing for this rule to explain");
        }

        List<ThreadRecord> matches = new List<ThreadRecord>();
        List<string> matchedMarkers = new List<string>();

        for (int threadIndex = 0; threadIndex < analysis.Threading.Threads.Count; ++threadIndex)
        {
            ThreadRecord thread = analysis.Threading.Threads[threadIndex];

            if (!ThreadingRuleGate.IsBlockedPoolWorker(thread))
            {
                continue;
            }

            string marker = FindMarker(thread);

            if (marker != null)
            {
                matches.Add(thread);
                matchedMarkers.Add(marker);
            }
        }

        string measured = FormatCount(matches.Count) + " of " + FormatCount(analysis.Threading.BlockedPoolWorkerCount)
            + " blocked pool workers have a blocking-on-Task frame on their dominant stack";

        if (matches.Count == 0)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Workers blocking on a Task", FormatCount(matches.Count), matches.Count));
        evidence.Add(new InsightEvidence("Blocked pool workers", FormatCount(analysis.Threading.BlockedPoolWorkerCount), analysis.Threading.BlockedPoolWorkerCount));

        for (int matchIndex = 0; matchIndex < matches.Count && matchIndex < 4; ++matchIndex)
        {
            evidence.Add(new InsightEvidence(
                "Thread " + matches[matchIndex].ThreadId,
                matchedMarkers[matchIndex] + ", " + FormatPercent(matches[matchIndex].DominantStackShare * 100.0) + " of its samples",
                matches[matchIndex].DominantStackShare));
        }

        // Name the caller above the marker where there is one - the BCL frame
        // says HOW it blocked, the frame above it says WHERE, and only the
        // second is somewhere anyone can go and edit.
        string callerHint = FindCallerAboveMarker(matches[0]);

        if (callerHint != null)
        {
            evidence.Add(new InsightEvidence("Called from", callerHint, 0));
        }

        return this.Fire(
            InsightSeverity.Critical,
            InsightConfidence.Medium,
            FormatCount(matches.Count) + " blocked pool workers are synchronously waiting on a Task (sync-over-async)",
            "These thread-pool workers are blocked inside the BCL's own blocking-on-a-Task path - Task.Wait, "
            + ".Result, or .GetAwaiter().GetResult(). That occupies a pool thread for the whole duration of an "
            + "operation that was written to not need one, which is how a pool starves itself: the continuation "
            + "needs a worker to run on, and the worker it would have used is the one blocked waiting for it. "
            + "This finding is matched on BCL method NAMES on each thread's dominant stack, which is why it is "
            + "reported at medium confidence - the underlying blocked-worker classification it explains is not "
            + "name-based and is independent of it.",
            2.0,
            evidence,
            new List<string>
            {
                "Make the calling method async and await the task rather than blocking on it.",
                callerHint != null ? "Start at " + callerHint + "." : "Expand these threads in the Threading view to find the calling method."
            },
            new List<InsightLink> { new InsightLink("threading", "Threading view") },
            measured);
    }

    private static string FindMarker(ThreadRecord thread)
    {
        for (int frameIndex = 0; frameIndex < thread.DominantStack.Count; ++frameIndex)
        {
            string frame = thread.DominantStack[frameIndex];

            for (int markerIndex = 0; markerIndex < SyncOverAsyncFrameMarkers.Length; ++markerIndex)
            {
                if (frame.IndexOf(SyncOverAsyncFrameMarkers[markerIndex], StringComparison.Ordinal) >= 0)
                {
                    return SyncOverAsyncFrameMarkers[markerIndex];
                }
            }
        }

        return null;
    }

    // The first frame OUTSIDE the BCL above the deepest marker. Stacks here
    // are innermost-first, so walking forward walks outward toward callers.
    private static string FindCallerAboveMarker(ThreadRecord thread)
    {
        bool seenMarker = false;

        for (int frameIndex = 0; frameIndex < thread.DominantStack.Count; ++frameIndex)
        {
            string frame = thread.DominantStack[frameIndex];
            bool isMarker = false;

            for (int markerIndex = 0; markerIndex < SyncOverAsyncFrameMarkers.Length; ++markerIndex)
            {
                if (frame.IndexOf(SyncOverAsyncFrameMarkers[markerIndex], StringComparison.Ordinal) >= 0)
                {
                    isMarker = true;
                    break;
                }
            }

            if (isMarker)
            {
                seenMarker = true;
                continue;
            }

            if (seenMarker
                && !frame.StartsWith("System.", StringComparison.Ordinal)
                && !frame.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            {
                return frame;
            }
        }

        return null;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights.Rules)
