////////////////////////////////////////////////////////////////////////////////
// Module: CaptureAnalysis.cs
//
// Notes:
// The compact, derived summary of one capture that the insight rules
// (Insights/) and the MCP tool surface (Mcp/) both read. It is deliberately
// NOT the --json export: that export exists to feed a webview and runs to
// 54MB on a real 3.01GB capture, most of it drill-down trees nobody asks a
// yes/no question of. This is the part a rule or an agent actually reasons
// over - per-GC records, per-thread roles, category totals, top-N tables -
// and it lands in the low hundreds of KB, small enough to hold resident for
// an MCP server's lifetime without paying the 2-5GB the full parse costs.
//
// ONE MODEL, THREE CONSUMERS, on purpose. A rule and an agent tool that
// derived the same number by two different routes could disagree, and there
// would be no way to tell which was right. Everything downstream reads these
// fields or it does not exist.
//
// WHAT GOES IN HERE. Only values that are already computed by the pipeline,
// or that cost one cheap pass over an already-projected list. Nothing in this
// file may re-walk the 16M+ CPU samples: the three whole-capture passes that
// would need to (TimeBreakdownBuilder, ThreadActivityProfiler,
// CpuCategoryBuilder) are hoisted to Program.cs and handed in, so --json and
// --insights each run them exactly once between them. CpuCategoryBuilder's
// per-category SelfSamplesByFrameId is also what the hot-method ranking is
// merged from, rather than a fourth pass.
//
// NULLABILITY. Every section is non-null; emptiness is expressed by counts
// and by the Contents flags. "This capture recorded no contention events" and
// "this capture recorded contention events and none were interesting" are
// different answers, and a rule that cannot tell them apart will confidently
// report the wrong one - see CaptureContents.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Analysis {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Overview;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class CaptureAnalysis
{
    // Provenance. SourcePath is echoed back by the MCP surface so an agent
    // holding several open captures can tell them apart; nothing reads it to
    // find the file again.
    public string ProcessName = "";
    public string SourcePath = "";
    public int FormatVersion;
    public DateTime CaptureStartUtc;
    public int NumberOfProcessors;
    public int ProcessId;

    // False when the capture carried too few timestamped events to establish
    // a span at all. Every rate/percent-of-capture rule has to check this
    // first - dividing by a zero duration produces an infinity that renders
    // as a very confident finding.
    public bool HasCaptureDuration;
    public double CaptureDurationMSec;
    public long TotalEventCount;

    public CaptureContents Contents = new CaptureContents();

    public TimeBreakdown TimeBreakdown;

    public GcAnalysis Gc = new GcAnalysis();
    public AllocationAnalysis Allocation = new AllocationAnalysis();
    public CpuAnalysis Cpu = new CpuAnalysis();
    public ThreadingAnalysis Threading = new ThreadingAnalysis();
    public ContentionAnalysis Contention = new ContentionAnalysis();
    public ExceptionAnalysis Exceptions = new ExceptionAnalysis();

    [JsonIgnore]
    public double CaptureDurationSeconds => this.CaptureDurationMSec / 1000.0;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// What this capture actually recorded, as opposed to what it found.
//
// This is the single most load-bearing section for not embarrassing
// ourselves. A `dotnet-trace collect-linux` capture has no GCAllocationTick
// and no contention events at all - not because the process allocated
// nothing and contended for nothing, but because those keywords were not
// collected. Every rule that would otherwise report "no allocation pressure"
// has to be able to say "allocation was not recorded" instead, and
// capture/missing-providers exists to say it out loud.
public sealed class CaptureContents
{
    public bool HasGcEvents;
    public bool HasAllocationTicks;
    public bool HasCpuSamples;
    public bool HasContentionEvents;
    public bool HasExceptionEvents;
    public bool HasThreadPoolEvents;

    // "runtime" when the capture's samples carry the CLR's own
    // ThreadSampleType, "derived" when it was inferred from whether each
    // sample's leaf frame resolved to managed code (a v6/perf capture - see
    // Universal/UniversalSampleTypeClassifier.cs), "none" when there are no
    // samples. The threading thresholds were calibrated against "runtime",
    // so "derived" is a real caveat and capture/derived-sample-type reports
    // it rather than letting the roles read as equally trustworthy.
    public string SampleTypeSource = "none";

    [JsonIgnore]
    public bool HasRuntimeSampleType => this.SampleTypeSource == "runtime";

    // Set when a SINGLE leaf frame accounts for essentially every CPU sample
    // or essentially all contention wait. That is not a hot method and not a
    // hot lock - it means leaf attribution has collapsed and every stack in
    // that signal is bottoming out in the same place.
    //
    // Found on a real capture, not anticipated: a `dotnet-trace collect-linux`
    // capture of an ASP.NET service reported `user_events_write_core.isra.0` -
    // the KERNEL's user_events write path, i.e. the code that records events -
    // as 100.00% of CPU self time, 100% of the Kernel category, and 100% of
    // lock wait across 181 threads. Every sample and every contention event
    // carried the stack of the writer that recorded it rather than the stack
    // of the thing being recorded. The numbers are real; what they measure is
    // the tracing machinery.
    //
    // The rules that rank by leaf frame decline when this is set, because the
    // alternative is presenting that as a finding - which is the single worst
    // thing this feature could do, and would be done with total confidence.
    public bool CpuLeafAttributionCollapsed;
    public string CollapsedCpuLeafFrame = "";
    public double CollapsedCpuLeafShare;

    public bool ContentionLeafAttributionCollapsed;
    public string CollapsedContentionLeafFrame = "";
    public double CollapsedContentionLeafShare;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// One completed GC, flattened to what a rule or an agent asks about. The
// full per-heap detail stays in the --json export; what survives here is the
// per-heap AGGREGATE the imbalance rule needs, because carrying every heap of
// every GC would put this model back in megabyte territory on a 12,386-GC
// capture for the benefit of exactly one rule.
public sealed class GcRecord
{
    public int Id;
    public int Generation;
    public string Reason = "";
    public string Type = "";
    public double PauseDurationMSec;
    public double PauseStartRelativeMSec;
    public DateTime Timestamp;

    public long TotalHeapSize;
    public long TotalPromoted;
    public long GenerationSize0;
    public long GenerationSize1;
    public long GenerationSize2;
    public long GenerationSizeLOH;
    public long GenerationSizePOH;
    public long TotalPromotedLOH;
    public int NumHeaps;

    public bool IsInduced;
    public bool IsBackground;

    // Set only when this GC decoded per-heap detail (GCPerHeapHistory
    // version >= 3). The spread is the coefficient of variation of promoted
    // bytes across heaps - 0 when every heap did equal work, growing as they
    // diverge. See GcAnalysis.WorstHeapImbalance.
    public bool HasPerHeapDetail;
    public double HeapPromotedSpread;
    public long HeapPromotedMax;
    public long HeapPromotedMin;

    // Sum of per-heap fragmentation at the end of this GC, and the gen2 +
    // LOH size it should be read against. Both 0 without per-heap detail.
    public long FragmentationBytes;
}

public sealed class GcAnalysis
{
    public List<GcRecord> Gcs = new List<GcRecord>();

    public int Count;
    public double TotalPauseMSec;

    // Indexed by generation, 0..2. A gen3/gen4 (LOH/POH) never appears as a
    // GC's own Generation - those are sizes, not collection generations.
    public int[] CountByGeneration = new int[3];
    public double[] PauseByGeneration = new double[3];

    public double MaxPauseMSec;
    public int MaxPauseGcId = -1;
    public int MaxPauseGeneration;
    public string MaxPauseReason = "";

    public int InducedCount;
    public int FirstInducedGcId = -1;

    // GCs the runtime itself attributed to large-object allocation
    // (GCReason.AllocLarge or OutOfSpaceLOH). This is the runtime's own
    // reason code, not an inference from allocation-tick sizes, which is what
    // makes it a separate finding from "a lot of bytes went to the LOH":
    // allocating on the LOH is normal, having it drive collections is not.
    public int LohTriggeredCount;
    public int FirstLohTriggeredGcId = -1;

    // A blocking gen2 is the expensive one: a background gen2 does its mark
    // phase concurrently and its real pause is a fraction of its span (see
    // CLAUDE.md on PauseDurationMSec's true window), so lumping the two
    // together makes a healthy background-GC capture look like a stalling
    // one.
    public int BlockingGen2Count;
    public double BlockingGen2PauseMSec;
    public int BackgroundGen2Count;

    public bool IsServerGc;
    public int HeapCount = 1;
    public bool HasBackgroundGc;

    public long FirstHeapSizeBytes;
    public long LastHeapSizeBytes;
    public long PeakHeapSizeBytes;

    // Least-squares slope of TotalHeapSize against wall-clock, in bytes per
    // second, with the fit quality alongside it. The slope alone is not a
    // leak: a capture that starts cold and levels off has a positive slope
    // and a poor fit, which is exactly the case the R^2 gate exists to keep
    // out of the report.
    public bool HasHeapGrowthFit;
    public double HeapGrowthBytesPerSecond;
    public double HeapGrowthRSquared;

    // The largest per-GC heap-promotion spread seen, and which GC it was on.
    // Only meaningful when IsServerGc.
    public double WorstHeapImbalance;
    public int WorstHeapImbalanceGcId = -1;

    // Fragmentation at the last GC that reported per-heap detail, against
    // that same GC's gen2+LOH size.
    public bool HasFragmentation;
    public long FinalFragmentationBytes;
    public long FinalGen2AndLohBytes;

    public double GcsPerSecond(double captureDurationSeconds)
    {
        return captureDurationSeconds > 0 ? this.Count / captureDurationSeconds : 0;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class AllocatedTypeRecord
{
    public string TypeName = "";
    public long TotalBytes;
    public long TickCount;
    public long LargeCount;
    public double PercentOfTotalBytes;
}

// Built from the allocation TICK stream, which is a sampled signal (the
// runtime emits one tick per ~100KB allocated), so every byte figure here is
// "sampled bytes" and is an estimate of the real allocation volume rather
// than a measurement of it. Named TotalSampledBytes rather than TotalBytes
// for that reason - a rule quoting it should say "sampled".
public sealed class AllocationAnalysis
{
    public List<AllocatedTypeRecord> TopTypes = new List<AllocatedTypeRecord>();

    public long TotalSampledBytes;
    public long TotalTickCount;
    public int DistinctTypeCount;

    public long LargeObjectTickCount;
    public long LargeObjectSampledBytes;

    public double SampledBytesPerSecond(double captureDurationSeconds)
    {
        return captureDurationSeconds > 0 ? this.TotalSampledBytes / captureDurationSeconds : 0;
    }

    [JsonIgnore]
    public double LargeObjectByteShare => this.TotalSampledBytes > 0
        ? (double)this.LargeObjectSampledBytes / this.TotalSampledBytes
        : 0;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class CpuCategoryRecord
{
    public int Id;
    public string Name = "";
    public long SelfSamples;
    public long OnStackSamples;

    // Self sums to exactly 100% across categories; on-stack counts a sample
    // toward every category its stack passes through and deliberately sums to
    // more. A rule must say which one it is quoting - see
    // Cpu/CpuCategoryClassifier.cs's header.
    public double SelfPercent;
    public double OnStackPercent;

    public List<HotMethodRecord> TopMethods = new List<HotMethodRecord>();
}

public sealed class HotMethodRecord
{
    public int FrameId;
    public string Name = "";
    public long SelfSamples;
    public double SelfPercent;

    // True when this frame is a known blocking primitive - the thread pool's
    // park, a semaphore wait, a sleep. The CPU sample profiler samples every
    // thread, including the ones doing nothing, so on a service with a large
    // idle pool these frames dominate the self-time ranking without
    // representing any work at all. Reported rather than filtered out of the
    // model, because "68% of your samples are threads parked in the pool" is
    // itself worth knowing - it is just not a hot method.
    //
    // Uses Cpu/CpuIdleWaitClassifier.cs, the same list the CPU view's own
    // idle/CPU-bound time breakdown is computed from, so the two agree.
    public bool IsIdleWait;
}

public sealed class UnresolvedModuleRecord
{
    public string ModuleName = "";
    public long SelfSamples;
    public double SelfPercent;
}

public sealed class CpuAnalysis
{
    public long TotalSampleCount;
    public int DistinctStackCount;

    public List<CpuCategoryRecord> Categories = new List<CpuCategoryRecord>();
    public List<HotMethodRecord> HotMethods = new List<HotMethodRecord>();

    // Modules that samples landed in and no symbol could be found for. This
    // is the actionable half of an unresolved profile: "12% of this capture
    // has no symbols" is a complaint, "4.20% of it is libcrypto.so.3" names
    // the package to install. Excludes the two populations that are unnamed
    // for permanent reasons (runtime-generated code, the vDSO) - see
    // CLAUDE.md, "Not every unnamed frame is a missing symbol".
    public List<UnresolvedModuleRecord> UnresolvedModules = new List<UnresolvedModuleRecord>();
    public double UnresolvedPercent;

    public CpuCategoryRecord TryGetCategory(int id)
    {
        for (int categoryIndex = 0; categoryIndex < this.Categories.Count; ++categoryIndex)
        {
            if (this.Categories[categoryIndex].Id == id)
            {
                return this.Categories[categoryIndex];
            }
        }

        return null;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ThreadRecord
{
    public long ThreadId;
    public string Role = "";
    public int RoleId;
    public bool IsBenignlyParked;
    public bool IsPoolWorker;

    public int SampleCount;
    public double ManagedFraction;
    public double WaitFraction;
    public double PoolParkFraction;
    public double TopStacksShare;
    public double SampledSpanMSec;
    public double LongestContinuousIdleMSec;
    public int WakeCount;

    public int ContentionCount;
    public double ContentionWaitMSec;
    public double ContentionShareOfLife;

    // The thread's most-sampled stacks, most first, each resolved to names and
    // bounded in depth. More than one is carried because a parked worker is a
    // small LOOP rather than one frozen stack, and because a BLOCKED pool
    // worker's top stack is often the pool's own park - naming that would
    // point a reader at the one frame that is definitionally not the problem.
    // Empty when the thread produced no sampled stack at all.
    public List<ThreadStackRecord> TopStacks = new List<ThreadStackRecord>();

    [JsonIgnore]
    public List<string> DominantStack => this.TopStacks.Count > 0
        ? this.TopStacks[0].Frames
        : EmptyFrames;

    [JsonIgnore]
    public double DominantStackShare => this.TopStacks.Count > 0 ? this.TopStacks[0].Share : 0;

    private static readonly List<string> EmptyFrames = new List<string>();
}

public sealed class ThreadStackRecord
{
    // Innermost frame first, matching every other stack in this codebase.
    public List<string> Frames = new List<string>();
    public int SampleCount;
    public double Share;

    [JsonIgnore]
    public string LeafFrame => this.Frames.Count > 0 ? this.Frames[0] : "";
}

public sealed class ThreadingAnalysis
{
    public bool HasThreadPoolData;
    public bool HasSampleTypeData;

    public List<ThreadRecord> Threads = new List<ThreadRecord>();

    public int ThreadCount;
    public int BenignlyParkedThreadCount;
    public int BlockedPoolWorkerCount;
    public int BlockedThreadCount;
    public int ActiveThreadCount;
    public int IdlePoolWorkerCount;

    public int PeakActiveWorkerThreads;
    public int MinActiveWorkerThreads;
    public int FinalActiveWorkerThreads;
    public long WorkerThreadStartCount;

    public int AdjustmentCount;

    // Adjustments the pool's own hill-climbing attributed to a starvation
    // stall. This is the pool corroborating a starvation finding with its own
    // bookkeeping rather than the finding resting on sample classification
    // alone - two independent signals agreeing is the difference between a
    // finding worth acting on and a threshold that happened to trip.
    public int StallDrivenAdjustmentCount;

    // Peak minus minimum active workers: what stall-driven injection produces
    // when it happens.
    public int WorkerThreadGrowth;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ContentionSiteRecord
{
    public string SiteName = "";
    public int ContentionCount;
    public double TotalWaitMSec;
    public double AverageWaitMSec;
    public double MaxWaitMSec;
    public double PercentOfTotalWait;
    public int WaiterThreadCount;
}

public sealed class ContentionAnalysis
{
    public List<ContentionSiteRecord> TopSites = new List<ContentionSiteRecord>();

    public int TotalContentionCount;
    public double TotalWaitMSec;
    public int DistinctSiteCount;
    public int DistinctLockCount;
    public double MaxWaitMSec;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ExceptionTypeRecord
{
    public string TypeName = "";
    public int Count;
    public double PercentOfTotal;
    public string SampleMessage = "";
}

public sealed class ExceptionAnalysis
{
    public List<ExceptionTypeRecord> TopTypes = new List<ExceptionTypeRecord>();

    public int TotalCount;
    public int DistinctTypeCount;

    public double PerSecond(double captureDurationSeconds)
    {
        return captureDurationSeconds > 0 ? this.TotalCount / captureDurationSeconds : 0;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Analysis)
