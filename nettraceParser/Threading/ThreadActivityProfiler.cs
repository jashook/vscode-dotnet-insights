////////////////////////////////////////////////////////////////////////////////
// Module: ThreadActivityProfiler.cs
//
// Notes:
// Builds one whole-capture behavioural profile per thread, so the Threading
// view can tell a thread that is BENIGNLY PARKED from one that is actually
// stuck.
//
// Why this exists. The per-adjustment snapshots and the "during pool stalls"
// table both answer "what stack was this thread in at time T", and that
// question alone cannot separate the two cases - the stack is the same either
// way. A Kafka consumer parked in a native poll, a gRPC completion thread, a
// FileSystemWatcher read loop and a pool worker genuinely blocked behind an
// SslStream read all render as one frozen stack. The first three are the
// process's steady state and appear in EVERY window whether or not anything
// is wrong; only the last is worth an engineer's afternoon.
//
// What separates them is not the stack, it is the thread's behaviour over the
// WHOLE capture, from three independent signals:
//
//   1. ThreadSampleType (SampleEvent.SampleType) - the runtime's own answer to
//      "was this thread executing managed code". This is the load-bearing one
//      and it is why the payload is now decoded at all (see
//      SampleProfileEventProjector). A thread parked in a native call reports
//      External on every sample while its managed leaf frame still reads like
//      ordinary running code: on a real 836MB service capture, six
//      Grpc.Core.Internal.GrpcThreadPool.RunHandlerLoop threads produced
//      222,240 samples each, 100% External, zero managed - and the leaf-frame
//      wait classifier scored all of them as "100% running", because
//      RunHandlerLoop is not on anyone's list of blocking primitives and never
//      could be. No list of library methods can keep up with this; the runtime
//      already knows the answer.
//
//   2. Dominant stack share - a thread parked in a blocking call sits in ONE
//      byte-identical stack. Those same gRPC threads scored 1.0000 across
//      300 seconds. A thread doing native work that happens to also be
//      External moves around.
//
//   3. The contention join - ContentionEvent already names the blocked thread
//      and the owner, so a thread the Contention view has a row for is never
//      called benign here, however still it looks. That is what makes this
//      view supplemental to that one rather than a second, weaker copy of it.
//
// Every classification is therefore derived from capture-wide aggregates,
// never from a single instant. The two benign roles are LABELLED, not dropped:
// a stall window that is mostly parked threads is a real finding ("the pool
// had spare capacity"), just not the finding the view is hunting for.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Threading {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Progress;
using DotnetInsights.NetTrace.Rundown;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// What a thread spent the capture doing. The enum's own order is the display
// order: everything the reader can safely skip sorts last.
public enum ThreadActivityRole
{
    // A thread doing real work. The default, and what a thread is called
    // whenever there is not enough evidence to say something narrower.
    ActiveThread = 0,

    // A pool worker held up somewhere other than the pool's own park. The
    // starvation case this whole view exists to surface: the pool cannot
    // reuse this worker, so hill climbing injects more threads.
    BlockedPoolWorker = 1,

    // A non-pool thread that waits a lot but demonstrably works too - or that
    // the contention data has a row for. Not benign.
    BlockedThread = 2,

    // The pool's own idle state: a worker parked in the LIFO semaphore waiting
    // to be handed work. Benign - it means spare capacity, not a blockage.
    IdlePoolWorker = 3,

    // A long-running thread parked in one unchanging call for essentially the
    // whole time it was observed, never running managed code and never
    // appearing in the contention data. Benign by construction: it looks
    // blocked at every instant because being blocked is its job. This is the
    // application/library case - a consumer loop, a native poller, a watcher.
    ParkedThread = 4,

    // A thread the RUNTIME itself creates and parks by design: the pool's gate
    // thread, its RegisterWaitForSingleObject wait threads, the timer thread.
    // Identified from the thread's own entry point rather than from its
    // behaviour, because behaviour alone gets this one wrong - see
    // RuntimeParkedThreadEntryPoints.
    RuntimeInfrastructureThread = 5
}

public sealed class ThreadActivityProfile
{
    public long ThreadId;

    public int SampleCount;

    // Samples the runtime reported as executing MANAGED code. The primary
    // "did this thread actually do anything" measure - see this file's header
    // for why the leaf frame cannot answer that.
    public int ManagedSampleCount;

    // Samples whose leaf is a known BCL/CLR blocking primitive
    // (CpuIdleWaitClassifier's list, shared with the CPU view's own time
    // breakdown so the two agree). Kept alongside ManagedSampleCount rather
    // than replaced by it because it NAMES what the thread is waiting on,
    // which is what the view displays.
    public int WaitSampleCount;

    // Leaf is specifically the thread pool's own park. A subset of
    // WaitSampleCount, and the one wait that means "idle capacity".
    public int PoolParkSampleCount;

    // Samples whose stack passes through a managed thread-pool frame. A
    // FRACTION, not a flag: an "any sample ever" test misfired on a real
    // capture, marking a gRPC handler thread a pool worker off 8 samples in
    // 222,240 because it once queued a work item.
    public int PoolWorkerSampleCount;

    // Samples whose stack roots in a runtime-owned parked thread's entry point
    // (see ThreadActivityProfiler.RuntimeParkedThreadEntryPoints).
    public int RuntimeInfrastructureSampleCount;

    public double FirstSampleMSec;
    public double LastSampleMSec;

    // Idle -> running transitions across the capture, where "idle" is the same
    // composite this file classifies on (External, or a known wait leaf).
    // The most direct measure of "does this thread ever wake up": a parked
    // thread's count stays near 0 no matter how long the capture runs.
    public int WakeCount;

    // The longest unbroken stretch, in wall-clock ms, the thread sat idle with
    // no running sample in between. "Long running parked thread" stated as a
    // number, so a thread parked for the entire capture is visibly different
    // from one that blocked for 200ms at the worst possible moment.
    public double LongestContinuousIdleMSec;

    // Joined from the contention events, not the samples - see this file's
    // header. ContentionWaitMSec is time this thread spent BLOCKED on a
    // contended lock; ContentionOwnerCount is how often it was the thread
    // somebody else was blocked behind.
    public int ContentionCount;
    public double ContentionWaitMSec;
    public int ContentionOwnerCount;

    // The stacks this thread spent the most samples in, most first. Bounded by
    // ThreadActivityProfiler.MaxTopStacksPerThread.
    public int[] TopStackIndices = Array.Empty<int>();
    public int[] TopStackSampleCounts = Array.Empty<int>();

    public ThreadActivityRole Role;

    public double ManagedFraction => this.SampleCount > 0 ? (double)this.ManagedSampleCount / this.SampleCount : 0;
    public double WaitFraction => this.SampleCount > 0 ? (double)this.WaitSampleCount / this.SampleCount : 0;
    public double PoolParkFraction => this.SampleCount > 0 ? (double)this.PoolParkSampleCount / this.SampleCount : 0;
    public double PoolWorkerFraction => this.SampleCount > 0 ? (double)this.PoolWorkerSampleCount / this.SampleCount : 0;
    public double RuntimeInfrastructureFraction => this.SampleCount > 0 ? (double)this.RuntimeInfrastructureSampleCount / this.SampleCount : 0;
    public double SampledSpanMSec => this.LastSampleMSec - this.FirstSampleMSec;

    public double DominantStackShare => this.SampleCount > 0 && this.TopStackSampleCounts.Length > 0
        ? (double)this.TopStackSampleCounts[0] / this.SampleCount
        : 0;

    // Share of the thread's samples landing in its top few stacks together.
    //
    // This, not DominantStackShare, is what the parked test keys on. A real
    // parked worker is a small LOOP, not a single frozen stack: the
    // BackgroundWorker shape found on a live capture parks on an empty
    // BlockingCollection 90.9% of the time, sleeps out its poll interval a
    // further 6.4%, and spends 1.0% in the blocking call that actually ships
    // the batch - 98.2% across three stacks, but only 90.9% in the top one.
    // Judging that thread on its top stack alone called it Blocked, which is
    // exactly wrong: none of the three stacks is doing anything.
    public double TopStacksShare
    {
        get
        {
            if (this.SampleCount <= 0)
            {
                return 0;
            }

            int topStacksSampleCount = 0;

            for (int stackRank = 0; stackRank < this.TopStackSampleCounts.Length; ++stackRank)
            {
                topStacksSampleCount += this.TopStackSampleCounts[stackRank];
            }

            return (double)topStacksSampleCount / this.SampleCount;
        }
    }

    // What share of this thread's whole observed life the contention data
    // actually accounts for. The question a raw event COUNT cannot answer:
    // 157 contentions sounds decisive until they total 8.3ms of a 300-second
    // thread.
    public double ContentionShareOfLife => this.SampledSpanMSec > 0
        ? this.ContentionWaitMSec / this.SampledSpanMSec
        : 0;

    public bool IsPoolWorker => this.PoolWorkerFraction >= ThreadActivityProfiler.PoolWorkerStackFraction;
    public bool IsRuntimeInfrastructure => this.RuntimeInfrastructureFraction >= ThreadActivityProfiler.PoolWorkerStackFraction;

    // The one bit every caller actually branches on: can the reader skip this
    // thread without missing anything.
    public bool IsBenignlyParked => this.Role == ThreadActivityRole.IdlePoolWorker
        || this.Role == ThreadActivityRole.ParkedThread
        || this.Role == ThreadActivityRole.RuntimeInfrastructureThread;
}

public sealed class ThreadActivityProfileSet
{
    // Keyed by thread id, which is what every other threading structure here
    // (SampleEvent, ContentionEvent, StackedThreadingEvent) already carries.
    public Dictionary<long, ThreadActivityProfile> ProfilesByThreadId = new Dictionary<long, ThreadActivityProfile>();

    // Sorted most-actionable-first (see ThreadActivityProfiler.Compare).
    public List<ThreadActivityProfile> Ranked = new List<ThreadActivityProfile>();

    public int BenignlyParkedThreadCount;
    public int TotalSampleCount;

    // Samples belonging to threads classified as benignly parked. This is the
    // volume of noise the classification lets the view set aside, and it is
    // reported rather than silently removed.
    public int BenignlyParkedSampleCount;

    // False when the capture's samples carry no ThreadSampleType at all, in
    // which case the managed/native signal is unavailable and NOTHING is
    // classified as benignly parked (see ClassifyRole). Surfaced so the view
    // can say why it is showing every thread rather than appearing to have
    // found nothing to filter.
    public bool HasSampleTypeData;

    public ThreadActivityProfile TryGet(long threadId)
    {
        this.ProfilesByThreadId.TryGetValue(threadId, out ThreadActivityProfile profile);
        return profile;
    }

    // Convenience for the hot paths that only need the yes/no. A thread with
    // no profile at all (it produced no CPU samples) is NOT benign: unknown
    // and harmless are different answers, and defaulting to "harmless" is how
    // a view quietly hides the one thread that mattered.
    public bool IsBenignlyParked(long threadId)
    {
        ThreadActivityProfile profile = this.TryGet(threadId);
        return profile != null && profile.IsBenignlyParked;
    }
}

public static class ThreadActivityProfiler
{
    // Frames identifying a managed thread-pool worker, matched anywhere in the
    // stack. Kept identical to ContentionJsonExporter's own
    // WorkerThreadFrameMarkers - the two views must not disagree about which
    // threads are pool workers, since the point of this profile is to be
    // readable alongside the Contention view.
    private static readonly string[] WorkerThreadFrameMarkers = new string[]
    {
        "System.Threading.PortableThreadPool",
        "System.Threading.ThreadPoolWorkQueue",
        "System.Threading.ThreadPool.",
        "System.Threading._ThreadPoolWaitCallback",
        "System.Threading.TimerQueue"
    };

    // The pool's own park - a worker sitting here is waiting to be HANDED
    // work, which is idleness, not blockage. Same list ThreadingJsonExporter
    // has always used to keep this frame out of the stall ranking; it lives
    // here now because the classification below is the primary consumer.
    private static readonly string[] PoolParkFramePrefixes = new string[]
    {
        "System.Threading.LowLevelLifoSemaphore.",
        "System.Threading.PortableThreadPool+WorkerThread.WorkerThreadStart"
    };

    // Threads the RUNTIME creates and parks by design. Each entry is a thread
    // ENTRY POINT, so matching it anywhere in a stack identifies the whole
    // thread, not a passing call.
    //
    // These are classified by identity rather than by behaviour because
    // behaviour gets them wrong, and got them wrong in exactly the most
    // alarming direction. The timer thread parks in
    // WaitHandle.WaitOneNoCheck - never in the pool's LIFO semaphore - while
    // TimerQueue is (correctly) one of the pool-worker stack markers, so on a
    // real capture it read as "a pool worker held up outside the pool's own
    // park", which is the definition of BlockedPoolWorker and the single
    // loudest label this view has. It is also not filtered out by the parked
    // test, because waking on every timer tick to compute the next due time
    // is real managed execution: 11.8% of its samples on that capture.
    //
    // The pool's actual WORKER entry point
    // (PortableThreadPool+WorkerThread.WorkerThreadStart) is deliberately NOT
    // in this list - a starved worker is the finding, not the noise.
    private static readonly string[] RuntimeParkedThreadEntryPoints = new string[]
    {
        "System.Threading.PortableThreadPool+GateThread.GateThreadStart",
        "System.Threading.PortableThreadPool+WaitThread.WaitThreadStart",
        "System.Threading.TimerQueue.TimerThread"
    };

    private const int MaxTopStacksPerThread = 3;

    // A thread is a pool worker if a majority of its samples run through a
    // pool frame. See PoolWorkerSampleCount for the "any sample ever" version
    // this replaced and the real capture that broke it.
    public const double PoolWorkerStackFraction = 0.5;

    // ---- Benign-parked gates. All four must hold. ----
    //
    // The claim being made is "you can ignore this thread", so every gate is
    // set where a false POSITIVE costs the most, and each was read off the
    // measured distribution on a real capture rather than picked for looking
    // round. Recalibrating means re-reading that distribution, not nudging
    // these.
    //
    // Managed fraction: on the 836MB ads-retrieval capture the two populations
    // separate cleanly with a wide empty gap between them -
    //
    //   working threads: 0.058, 0.074, 0.116, 0.123, 0.147, 0.150, 0.165, 1.00
    //                    ^^^ nothing at all between 0.021 and 0.058 ^^^
    //   parked  threads: 0.021, 0.011, 0.007, 0.001, 0.0004, 0.0003, 0.0
    //
    // 0.05 sits in that gap. The thread at 0.021 is a pool wait thread parked
    // in WaitHandle.WaitOneNoCheck that wakes ~0.8 times a second; the one at
    // 0.058 is a real consumer thread. A tighter 0.02 splits the parked
    // cluster itself, which is how this was found.
    private const double MaxManagedFractionForParked = 0.05;

    // Concentration gate: the thread went round one small loop and nowhere
    // else. Measured over the top THREE stacks together (TopStacksShare), not
    // the top one - see that property for the real capture that forced it.
    //
    // Measured on non-pool threads across the three reference captures: every
    // thread that should be benign sits at 0.982-1.000, and the only one above
    // this bar that should NOT be (a busy DedicatedThreadPool work loop at
    // 0.967) is excluded by MaxManagedFractionForParked instead, at 0.179.
    //
    // What this gate CANNOT do, stated because it would otherwise look like it
    // does: a thread spinning in one native call and a thread blocked in one
    // native call are indistinguishable here - both report External on every
    // sample with a stable stack, and the runtime tells us nothing more. The
    // failure is bounded and visible elsewhere: a thread genuinely burning CPU
    // shows up in the CPU view as a hot method, which is the view that
    // question belongs to.
    private const double MinTopStacksShareForParked = 0.95;


    // Below these, "it never moved" is being claimed from too little
    // evidence. A thread that produced 12 samples, or lived for 300ms, gets no
    // claim made about it in either direction.
    private const int MinSamplesForParked = 50;
    private const double MinSampledSpanMSecForParked = 1000.0;

    // A thread that spends most of its time waiting is worth a look even when
    // it is plainly not parked.
    private const double BlockedWaitFraction = 0.5;

    // How much of a thread's life the contention data must account for before
    // it counts as evidence that thread is stuck.
    //
    // This used to be "any contention event at all", which was validated only
    // against captures whose parked threads happened to have exactly zero -
    // luck, not a property. A live capture then produced a dedicated
    // BlockingCollection drain worker carrying 157 contention events worth
    // 8.3ms across a 300-second life: 0.0028%, ambient lock traffic that
    // explains none of its idleness, and enough to keep it and ~45 sibling
    // queue-drain threads out of the benign bucket.
    //
    // 1% sits in a measured gap. Across the three reference captures, non-pool
    // threads that should be benign account for 0.0000-0.33% of their own life
    // in contention; every thread that genuinely is stuck starts at 1.09%.
    // (Pool workers do not rely on this number at all - see ClassifyRole, they
    // can only ever be benign by being parked in the pool's own park.)
    private const double MinContentionShareOfLifeForEvidence = 0.01;

    // onProgress: this pass walks every CPU sample in the capture and measured
    // 341-591ms on the three reference captures - around a tenth of the whole
    // run. That is well past the point where a start/complete snap is
    // indistinguishable from tracking it (see Progress/ProgressPlan.cs), so it
    // reports from inside the loop like the CPU and allocation writers do.
    public static ThreadActivityProfileSet Build(List<SampleEvent> sampleEvents, List<ContentionEvent> contentionEvents, StackTable stackTable, MethodSymbolTable symbolTable, Action<double> onProgress = null)
    {
        ThreadActivityProfileSet profileSet = new ThreadActivityProfileSet();

        IdleWaitFrameCache idleWaitCache = new IdleWaitFrameCache(symbolTable);

        // Two whole-stack questions - "does this stack pass through the pool"
        // and "is its leaf the pool's own park" - memoized per STACK index
        // rather than recomputed per sample. The first walks up to 40 frames
        // against 5 string prefixes, exactly the shape of per-sample cost this
        // codebase has repeatedly measured and removed (see IdleWaitFrameCache's
        // own header). Stack indices are dense and deduplicated by content
        // (StackTable.GetOrAdd), so one byte per distinct stack covers the
        // whole capture: 83,344 entries on the 3.23GB reference capture.
        //
        // Memoizing by stack pins each answer to the resolution time of the
        // FIRST sample that used that stack. Sound for both questions asked
        // here, which are about a stack's shape rather than about an instant:
        // a frame does not stop being PortableThreadPool later in the capture.
        // The leaf's idle/wait classification is NOT memoized this way - it
        // goes through IdleWaitFrameCache on the sample's own resolved id,
        // matching what the CPU view's time breakdown does, so the two agree
        // sample for sample.
        byte[] stackStates = new byte[stackTable.Count];

        Dictionary<long, ThreadSampleAccumulator> accumulatorsByThreadId = new Dictionary<long, ThreadSampleAccumulator>();

        Span<SampleEvent> samplesSpan = CollectionsMarshal.AsSpan(sampleEvents);

        for (int sampleIndex = 0; sampleIndex < samplesSpan.Length; ++sampleIndex)
        {
            // Power-of-two mask rather than a modulo, and gated before the
            // null check for the same reason every other per-sample loop here
            // does it: a delegate call on each of 16.24M iterations is exactly
            // the per-iteration cost this codebase keeps finding and removing.
            if (onProgress != null && (sampleIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress((double)sampleIndex / samplesSpan.Length);
            }

            ref readonly SampleEvent sampleEvent = ref samplesSpan[sampleIndex];

            long[] frames = stackTable.FramesAt(sampleEvent.StackIndex);

            if (frames.Length == 0)
            {
                continue;
            }

            if (sampleEvent.SampleType != ThreadSampleType.Unknown)
            {
                profileSet.HasSampleTypeData = true;
            }

            if (!accumulatorsByThreadId.TryGetValue(sampleEvent.ThreadId, out ThreadSampleAccumulator accumulator))
            {
                accumulator = new ThreadSampleAccumulator(sampleEvent.ThreadId, sampleEvent.RelativeMSec);
                accumulatorsByThreadId[sampleEvent.ThreadId] = accumulator;
            }

            byte stackState = stackStates[sampleEvent.StackIndex];

            if (stackState == StackStateUnknown)
            {
                stackState = ClassifyStack(frames, symbolTable, sampleEvent.RelativeMSec);
                stackStates[sampleEvent.StackIndex] = stackState;
            }

            int leafFrameId = symbolTable.ResolveId(frames[0], sampleEvent.RelativeMSec);

            SampleClassification classification;
            classification.IsManaged = sampleEvent.SampleType == ThreadSampleType.Managed || sampleEvent.SampleType == ThreadSampleType.Unknown;
            classification.IsWaitLeaf = idleWaitCache.IsIdleWaitFrame(leafFrameId);
            classification.IsPoolPark = (stackState & StackStatePoolPark) != 0;
            classification.IsPoolWorkerStack = (stackState & StackStatePoolWorker) != 0;
            classification.IsRuntimeInfrastructureStack = (stackState & StackStateRuntimeInfrastructure) != 0;

            accumulator.Add(sampleEvent.RelativeMSec, sampleEvent.StackIndex, in classification);
        }

        for (int contentionIndex = 0; contentionIndex < contentionEvents.Count; ++contentionIndex)
        {
            ContentionEvent contentionEvent = contentionEvents[contentionIndex];

            // A contention event proves the thread existed and was blocked
            // even if the sampler never caught it, so a thread seen only here
            // still gets a profile rather than being dropped.
            ThreadSampleAccumulator waiterAccumulator = GetOrAddAccumulator(accumulatorsByThreadId, contentionEvent.ThreadId, contentionEvent.RelativeMSec);
            waiterAccumulator.AddContentionWait(contentionEvent.DurationMSec);

            if (contentionEvent.OwnerThreadId != 0)
            {
                ThreadSampleAccumulator ownerAccumulator = GetOrAddAccumulator(accumulatorsByThreadId, contentionEvent.OwnerThreadId, contentionEvent.RelativeMSec);
                ownerAccumulator.AddContentionOwnership();
            }
        }

        foreach (KeyValuePair<long, ThreadSampleAccumulator> entry in accumulatorsByThreadId)
        {
            ThreadActivityProfile profile = entry.Value.ToProfile(MaxTopStacksPerThread);
            profile.Role = ClassifyRole(profile, profileSet.HasSampleTypeData);

            profileSet.ProfilesByThreadId[entry.Key] = profile;
            profileSet.Ranked.Add(profile);

            profileSet.TotalSampleCount += profile.SampleCount;

            if (profile.IsBenignlyParked)
            {
                ++profileSet.BenignlyParkedThreadCount;
                profileSet.BenignlyParkedSampleCount += profile.SampleCount;
            }
        }

        profileSet.Ranked.Sort(Compare);

        return profileSet;
    }

    private static ThreadSampleAccumulator GetOrAddAccumulator(Dictionary<long, ThreadSampleAccumulator> accumulatorsByThreadId, long threadId, double relativeMSec)
    {
        if (!accumulatorsByThreadId.TryGetValue(threadId, out ThreadSampleAccumulator accumulator))
        {
            accumulator = new ThreadSampleAccumulator(threadId, relativeMSec);
            accumulatorsByThreadId[threadId] = accumulator;
        }

        return accumulator;
    }

    // The decision table, in one place and in priority order.
    public static ThreadActivityRole ClassifyRole(ThreadActivityProfile profile, bool hasSampleTypeData)
    {
        // Identity beats behaviour, and is checked first for that reason: a
        // thread the runtime itself created to sit in a wait is never a
        // finding, and every behavioural signal available describes it exactly
        // as it describes a thread that is genuinely stuck.
        if (profile.IsRuntimeInfrastructure)
        {
            return ThreadActivityRole.RuntimeInfrastructureThread;
        }

        // A thread whose idleness the contention data MATERIALLY accounts for
        // is never called benign, however parked it looks. Those events are
        // harder evidence than a sampled stack, and this is the join that
        // keeps the two views agreeing. Material, not merely present: see
        // MinContentionShareOfLifeForEvidence for the real thread that made
        // "any event at all" the wrong test.
        bool hasContentionEvidence = profile.ContentionCount > 0
            && profile.ContentionShareOfLife >= MinContentionShareOfLifeForEvidence;

        // With no ThreadSampleType in the capture, the managed/native signal
        // does not exist and the "it never ran" half of the parked test cannot
        // be evaluated. Rather than fall back to the leaf-frame heuristic that
        // this whole file exists because of, nothing is called parked and the
        // view degrades to showing every thread - which the profile set
        // reports (HasSampleTypeData) so it can say so.
        bool looksParked = hasSampleTypeData
            && !hasContentionEvidence
            && profile.SampleCount >= MinSamplesForParked
            && profile.SampledSpanMSec >= MinSampledSpanMSecForParked
            && profile.ManagedFraction <= MaxManagedFractionForParked
            && profile.TopStacksShare >= MinTopStacksShareForParked;

        if (looksParked)
        {
            // A POOL WORKER can only ever be benign by being parked in the
            // pool's own park. One parked anywhere else is a worker the pool
            // cannot reuse - the starvation case - and the stiller it sits the
            // worse that is, so "it looks completely parked" must not be
            // allowed to reclassify it as harmless. This is the one place
            // where looking more parked makes a thread MORE interesting, not
            // less, and getting it backwards would hide the finding this whole
            // view exists for.
            if (profile.IsPoolWorker)
            {
                return ParkShareOfWait(profile) >= ParkShareOfWaitForHealthyWorker
                    ? ThreadActivityRole.IdlePoolWorker
                    : ThreadActivityRole.BlockedPoolWorker;
            }

            // A dedicated thread parked on a work queue cannot starve the
            // managed pool at all - it is not in it. This is the shape a real
            // service has dozens of (a BlockingCollection drain, a backlog
            // processor, a poll loop), and it is exactly what the reader
            // should not be spending an afternoon on.
            return ThreadActivityRole.ParkedThread;
        }

        // Not parked. A pool worker held up somewhere other than the park is
        // the starvation case; one whose waiting IS mostly the park is just a
        // normal worker between work items.
        bool isWaitDominated = profile.WaitFraction >= BlockedWaitFraction || hasContentionEvidence;

        if (!isWaitDominated)
        {
            return ThreadActivityRole.ActiveThread;
        }

        if (profile.IsPoolWorker)
        {
            return ParkShareOfWait(profile) >= ParkShareOfWaitForHealthyWorker
                ? ThreadActivityRole.ActiveThread
                : ThreadActivityRole.BlockedPoolWorker;
        }

        return ThreadActivityRole.BlockedThread;
    }

    // Of the time this worker spent waiting, how much was the pool's own park?
    // The single question that separates a worker idling between work items
    // from one the pool cannot reuse, and it is asked the same way on both
    // paths through ClassifyRole - a parked-looking worker and a busy-looking
    // one are the same population viewed at different duty cycles, so
    // measuring them differently puts a seam through the middle of it. An
    // earlier version gated the parked path on PoolParkFraction against the
    // concentration threshold instead, which split a real capture's idle
    // workers at 0.937/0.939 versus 0.956 - three readings of the same thing.
    //
    // Zero when the worker did not register as WAITING at all, which is the
    // right answer rather than a division to guard: a pool worker sitting in a
    // native call the wait classifier does not recognise is occupied, not
    // parked, and the pool cannot reuse it either way.
    private static double ParkShareOfWait(ThreadActivityProfile profile)
    {
        return profile.WaitSampleCount > 0
            ? (double)profile.PoolParkSampleCount / profile.WaitSampleCount
            : 0;
    }

    // How much of a pool worker's waiting must be the pool's own park before
    // it reads as a normal worker between work items rather than a blocked
    // one.
    //
    // The two populations were measured on captures that each contain only one
    // of them, which is what makes the gap trustworthy rather than a split
    // through the middle of one cluster:
    //
    //   healthy (ads-retrieval, 34 workers):        0.872 .. 0.951
    //   blocked (assets-registry, 6 workers):       0.039 .. 0.729
    //
    // 0.75 sits in the empty band between 0.729 and 0.872. The six on the
    // blocked side are independently corroborated - each carries 30ms to
    // 4,909ms of real contention wait, so the Contention view has rows for all
    // of them.
    private const double ParkShareOfWaitForHealthyWorker = 0.75;

    // Most-actionable first, benign roles last. Within a role, by the evidence
    // that made it interesting, then by size.
    private static int Compare(ThreadActivityProfile left, ThreadActivityProfile right)
    {
        if (left.Role != right.Role)
        {
            return ((int)left.Role).CompareTo((int)right.Role);
        }

        if (left.ContentionWaitMSec != right.ContentionWaitMSec)
        {
            return right.ContentionWaitMSec.CompareTo(left.ContentionWaitMSec);
        }

        if (left.SampleCount != right.SampleCount)
        {
            return right.SampleCount.CompareTo(left.SampleCount);
        }

        // Thread id last, purely so the order is stable across runs of the
        // same capture - two threads with identical numbers must not swap
        // places between runs and make a diff of two exports look like a
        // change.
        return left.ThreadId.CompareTo(right.ThreadId);
    }

    public static string NameForRole(ThreadActivityRole role)
    {
        switch (role)
        {
            case ThreadActivityRole.BlockedPoolWorker:
                return "Blocked pool worker";

            case ThreadActivityRole.BlockedThread:
                return "Blocked";

            case ThreadActivityRole.IdlePoolWorker:
                return "Idle pool worker";

            case ThreadActivityRole.ParkedThread:
                return "Parked";

            case ThreadActivityRole.RuntimeInfrastructureThread:
                return "Runtime infrastructure";

            default:
                return "Active";
        }
    }

    // One line the view can show verbatim instead of making the reader infer
    // the classification from five numbers.
    public static string ExplanationForRole(ThreadActivityProfile profile, bool hasSampleTypeData)
    {
        switch (profile.Role)
        {
            case ThreadActivityRole.BlockedPoolWorker:
                return "A thread-pool worker held up somewhere other than the pool's own park, so the pool cannot reuse it and grows instead.";

            case ThreadActivityRole.BlockedThread:
                return profile.ContentionCount > 0
                    ? "Waits dominate this thread's time and the contention data has rows for it, so it is blocked on a real lock."
                    : "Waits dominate this thread's time, but it does real work too - not a thread that is simply parked.";

            case ThreadActivityRole.IdlePoolWorker:
                return "Parked in the thread pool's own semaphore waiting to be handed work. This is idle capacity, not a blockage.";

            case ThreadActivityRole.RuntimeInfrastructureThread:
                return "A thread the runtime creates and parks by design - the pool's gate thread, a RegisterWaitForSingleObject wait thread, or the timer thread. Recognised by its entry point, not by how idle it looks.";

            case ThreadActivityRole.ParkedThread:
                return "Sat in one unchanging stack for essentially its whole life, never ran managed code and never appears in the contention data - a long-running thread whose job is to wait.";

            default:
                return hasSampleTypeData
                    ? "Ran managed code often enough not to be a parked thread."
                    : "This capture's samples carry no managed/native flag, so no thread can be shown to be parked.";
        }
    }

    private const byte StackStateUnknown = 0;
    private const byte StackStateComputed = 1;
    private const byte StackStatePoolWorker = 2;
    private const byte StackStatePoolPark = 4;
    private const byte StackStateRuntimeInfrastructure = 8;

    private static byte ClassifyStack(long[] frames, MethodSymbolTable symbolTable, double relativeMSec)
    {
        byte state = StackStateComputed;

        string leafName = symbolTable.NameForId(symbolTable.ResolveId(frames[0], relativeMSec));

        if (StartsWithAny(leafName, PoolParkFramePrefixes))
        {
            state |= StackStatePoolPark;
        }

        for (int frameIndex = 0; frameIndex < frames.Length; ++frameIndex)
        {
            string frameName = symbolTable.NameForId(symbolTable.ResolveId(frames[frameIndex], relativeMSec));

            if ((state & StackStatePoolWorker) == 0 && StartsWithAny(frameName, WorkerThreadFrameMarkers))
            {
                state |= StackStatePoolWorker;
            }

            if ((state & StackStateRuntimeInfrastructure) == 0 && StartsWithAny(frameName, RuntimeParkedThreadEntryPoints))
            {
                state |= StackStateRuntimeInfrastructure;
            }
        }

        return state;
    }

    private static bool StartsWithAny(string frameName, string[] prefixes)
    {
        if (string.IsNullOrEmpty(frameName))
        {
            return false;
        }

        for (int prefixIndex = 0; prefixIndex < prefixes.Length; ++prefixIndex)
        {
            if (frameName.StartsWith(prefixes[prefixIndex], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // What one sample says about its thread. A struct passed by `in` rather
    // than four bools, so adding a fifth signal later does not mean touching
    // every call site's argument order.
    private struct SampleClassification
    {
        public bool IsManaged;
        public bool IsWaitLeaf;
        public bool IsPoolPark;
        public bool IsPoolWorkerStack;
        public bool IsRuntimeInfrastructureStack;

        // "Idle-looking": either the runtime says the thread was not running
        // managed code, or its leaf is a known blocking primitive. Union
        // rather than intersection, because each covers a case the other
        // misses - External catches the native parks no frame list knows
        // about, and a managed spin inside SpinWait is still a wait.
        public readonly bool IsIdle => !this.IsManaged || this.IsWaitLeaf;
    }

    // Mutable per-thread state for the single pass. Separate from the emitted
    // ThreadActivityProfile so the stack histogram - the one genuinely large
    // per-thread structure - is dropped once the top few are picked.
    private sealed class ThreadSampleAccumulator
    {
        private readonly long threadId;
        private readonly Dictionary<int, int> sampleCountByStackIndex = new Dictionary<int, int>();

        // Run-length front end to the histogram above, and the largest saving
        // in this pass. Samples arrive in time order, and the threads this
        // classifier cares most about are precisely the ones that emit the
        // same stack thousands of times in a row - so the dictionary is
        // touched once per RUN rather than once per sample. Exact, not an
        // approximation: the run is flushed whole whenever the stack changes,
        // and once more in ToProfile.
        //
        // Measured on the 3.23GB/16.24M-sample reference capture: this phase
        // went 750-791ms to 586-611ms (3 runs each), with byte-identical JSON
        // output for the whole document.
        //
        // One alternative was measured and DECLINED. Folding the leaf frame's
        // idle/wait answer into the per-stack `stackStates` byte - which drops
        // the per-sample MethodSymbolTable.ResolveId and IdleWaitFrameCache
        // probe entirely - saves less (750-791ms to 661-678ms) and costs more:
        // it pins each leaf's resolution to the first sample that used that
        // stack, where every other consumer of this data resolves per sample
        // against the sample's own timestamp. Paying ~70ms to keep this pass
        // agreeing with the CPU view sample for sample is the better trade.
        // The whole-stack scans above are memoized that way because they ask
        // about a stack's SHAPE, which does not move.
        private int pendingStackIndex = -1;
        private int pendingStackRunLength;

        private int sampleCount;
        private int managedSampleCount;
        private int waitSampleCount;
        private int poolParkSampleCount;
        private int poolWorkerSampleCount;
        private int runtimeInfrastructureSampleCount;

        private readonly double firstSampleMSec;
        private double lastSampleMSec;

        private int wakeCount;
        private bool previousSampleWasIdle;
        private double currentIdleRunStartMSec;
        private double longestContinuousIdleMSec;

        private int contentionCount;
        private double contentionWaitMSec;
        private int contentionOwnerCount;

        public ThreadSampleAccumulator(long threadId, double firstSampleMSec)
        {
            this.threadId = threadId;
            this.firstSampleMSec = firstSampleMSec;
            this.lastSampleMSec = firstSampleMSec;
        }

        public void Add(double relativeMSec, int stackIndex, in SampleClassification classification)
        {
            ++this.sampleCount;
            this.lastSampleMSec = relativeMSec;

            if (classification.IsManaged)
            {
                ++this.managedSampleCount;
            }

            if (classification.IsWaitLeaf)
            {
                ++this.waitSampleCount;
            }

            if (classification.IsPoolPark)
            {
                ++this.poolParkSampleCount;
            }

            if (classification.IsPoolWorkerStack)
            {
                ++this.poolWorkerSampleCount;
            }

            if (classification.IsRuntimeInfrastructureStack)
            {
                ++this.runtimeInfrastructureSampleCount;
            }

            if (stackIndex == this.pendingStackIndex)
            {
                ++this.pendingStackRunLength;
            }
            else
            {
                this.FlushPendingStackRun();
                this.pendingStackIndex = stackIndex;
                this.pendingStackRunLength = 1;
            }

            if (classification.IsIdle)
            {
                if (!this.previousSampleWasIdle)
                {
                    this.currentIdleRunStartMSec = relativeMSec;
                }

                double idleRunMSec = relativeMSec - this.currentIdleRunStartMSec;

                if (idleRunMSec > this.longestContinuousIdleMSec)
                {
                    this.longestContinuousIdleMSec = idleRunMSec;
                }

                this.previousSampleWasIdle = true;
                return;
            }

            // An idle run only ends when the thread is caught RUNNING, so a
            // gap in sampling inside a park does not split one park into two.
            // That matters: a thread blocked in a native call is exactly the
            // case the sampler is most likely to miss, and splitting its park
            // would understate the one number that says "this thread never
            // woke up".
            if (this.previousSampleWasIdle)
            {
                ++this.wakeCount;
            }

            this.previousSampleWasIdle = false;
        }

        private void FlushPendingStackRun()
        {
            if (this.pendingStackRunLength == 0)
            {
                return;
            }

            this.sampleCountByStackIndex.TryGetValue(this.pendingStackIndex, out int stackSampleCount);
            this.sampleCountByStackIndex[this.pendingStackIndex] = stackSampleCount + this.pendingStackRunLength;
            this.pendingStackRunLength = 0;
        }

        public void AddContentionWait(double durationMSec)
        {
            ++this.contentionCount;
            this.contentionWaitMSec += durationMSec;
        }

        public void AddContentionOwnership()
        {
            ++this.contentionOwnerCount;
        }

        public ThreadActivityProfile ToProfile(int maxTopStacks)
        {
            this.FlushPendingStackRun();

            ThreadActivityProfile profile = new ThreadActivityProfile();

            profile.ThreadId = this.threadId;
            profile.SampleCount = this.sampleCount;
            profile.ManagedSampleCount = this.managedSampleCount;
            profile.WaitSampleCount = this.waitSampleCount;
            profile.PoolParkSampleCount = this.poolParkSampleCount;
            profile.PoolWorkerSampleCount = this.poolWorkerSampleCount;
            profile.RuntimeInfrastructureSampleCount = this.runtimeInfrastructureSampleCount;
            profile.FirstSampleMSec = this.firstSampleMSec;
            profile.LastSampleMSec = this.lastSampleMSec;
            profile.WakeCount = this.wakeCount;
            profile.LongestContinuousIdleMSec = this.longestContinuousIdleMSec;
            profile.ContentionCount = this.contentionCount;
            profile.ContentionWaitMSec = this.contentionWaitMSec;
            profile.ContentionOwnerCount = this.contentionOwnerCount;

            int topStackCount = this.sampleCountByStackIndex.Count < maxTopStacks ? this.sampleCountByStackIndex.Count : maxTopStacks;

            if (topStackCount > 0)
            {
                List<KeyValuePair<int, int>> rankedStacks = new List<KeyValuePair<int, int>>(this.sampleCountByStackIndex);
                rankedStacks.Sort(CompareStackEntries);

                profile.TopStackIndices = new int[topStackCount];
                profile.TopStackSampleCounts = new int[topStackCount];

                for (int stackRank = 0; stackRank < topStackCount; ++stackRank)
                {
                    profile.TopStackIndices[stackRank] = rankedStacks[stackRank].Key;
                    profile.TopStackSampleCounts[stackRank] = rankedStacks[stackRank].Value;
                }
            }

            return profile;
        }

        // Count descending, then stack index ascending so ties do not reorder
        // between runs of the same capture.
        private static int CompareStackEntries(KeyValuePair<int, int> left, KeyValuePair<int, int> right)
        {
            if (left.Value != right.Value)
            {
                return right.Value.CompareTo(left.Value);
            }

            return left.Key.CompareTo(right.Key);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Threading)
