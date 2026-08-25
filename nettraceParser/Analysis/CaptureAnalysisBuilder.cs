////////////////////////////////////////////////////////////////////////////////
// Module: CaptureAnalysisBuilder.cs
//
// Notes:
// Turns the pipeline's projected event lists into the CaptureAnalysis model
// the insight rules and the MCP tool surface read. See CaptureAnalysis.cs for
// what the model is for and why it is not the --json export.
//
// COST. This runs three cheap passes (allocation ticks, exceptions,
// contentions - each a group-by over an already-projected list) plus a walk
// of the GC list. It runs NO pass over the CPU samples: the three whole-
// capture sample passes are hoisted to Program.cs and handed in here already
// built (see the timeBreakdown/threadProfiles/categoryTotals parameters), so
// a --json run computes each exactly once and shares it, and a plain
// --insights run computes each exactly once and skips the export entirely.
// The hot-method ranking is merged out of CpuCategoryBuilder's own per-
// category SelfSamplesByFrameId rather than being a fourth pass.
//
// AGREEING WITH THE VIEWS. Where a table already exists in the --json export,
// this builds it the same way the exporter does - contention sites group by
// resolved leaf frame id (ContentionJsonExporter), unresolved modules go
// through CpuCategoryBuilder.RankUnresolvedModules. An insight that quoted a
// number the user then could not find in the matching view would be worse
// than no insight, and duplicated aggregation logic is how that happens.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Analysis {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Contention;
using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Rundown;
using DotnetInsights.NetTrace.Threading;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class CaptureAnalysisBuilder
{
    // How many rows each top-N table keeps. Sized for what a rule quotes and
    // an agent skims, not for a scrollable view - the full ranking lives in
    // the --json export and its webview table.
    public const int TopTypeLimit = 25;
    public const int TopSiteLimit = 25;
    public const int TopMethodLimit = 50;
    public const int TopMethodsPerCategoryLimit = 8;
    public const int TopUnresolvedModuleLimit = 15;

    // Frames of a thread's dominant stack carried as evidence. Enough to name
    // what it is parked in and who called it; a reader wanting the whole chain
    // has the Threading view.
    public const int DominantStackFrameLimit = 8;

    // A least-squares heap-growth slope over fewer points than this is not a
    // trend, it is two numbers and a line through them.
    private const int MinimumGcsForHeapGrowthFit = 8;

    public static CaptureAnalysis Build(
        string sourcePath,
        string processName,
        int formatVersion,
        DateTime captureStartUtc,
        int numberOfProcessors,
        int processId,
        long totalEventCount,
        bool hasCaptureDuration,
        double captureDurationMSec,
        List<GcEvent> gcEvents,
        List<AllocationEvent> allocationEvents,
        List<ExceptionEvent> exceptionEvents,
        List<ContentionEvent> contentionEvents,
        List<SampleEvent> sampleEvents,
        ThreadingSummary threadingSummary,
        ThreadActivityProfileSet threadProfiles,
        CpuCategoryBuilder.CategoryTotals[] categoryTotals,
        TimeBreakdown timeBreakdown,
        StackTable stackTable,
        MethodSymbolTable symbolTable,
        string sampleTypeSource)
    {
        CaptureAnalysis analysis = new CaptureAnalysis();

        analysis.SourcePath = sourcePath ?? "";
        analysis.ProcessName = processName ?? "";
        analysis.FormatVersion = formatVersion;
        analysis.CaptureStartUtc = captureStartUtc;
        analysis.NumberOfProcessors = numberOfProcessors;
        analysis.ProcessId = processId;
        analysis.TotalEventCount = totalEventCount;
        analysis.HasCaptureDuration = hasCaptureDuration;
        analysis.CaptureDurationMSec = captureDurationMSec;
        analysis.TimeBreakdown = timeBreakdown;

        analysis.Contents.HasGcEvents = gcEvents != null && gcEvents.Count > 0;
        analysis.Contents.HasAllocationTicks = allocationEvents != null && allocationEvents.Count > 0;
        analysis.Contents.HasCpuSamples = sampleEvents != null && sampleEvents.Count > 0;
        analysis.Contents.HasContentionEvents = contentionEvents != null && contentionEvents.Count > 0;
        analysis.Contents.HasExceptionEvents = exceptionEvents != null && exceptionEvents.Count > 0;
        analysis.Contents.HasThreadPoolEvents = threadingSummary != null && threadingSummary.HasThreadPoolData;
        analysis.Contents.SampleTypeSource = analysis.Contents.HasCpuSamples ? (sampleTypeSource ?? "runtime") : "none";

        BuildGc(analysis.Gc, gcEvents);
        BuildAllocation(analysis.Allocation, allocationEvents);
        BuildCpu(analysis.Cpu, categoryTotals, sampleEvents, stackTable, symbolTable);
        BuildThreading(analysis.Threading, threadingSummary, threadProfiles, stackTable, symbolTable);
        BuildContention(analysis.Contention, contentionEvents, stackTable, symbolTable);
        BuildExceptions(analysis.Exceptions, exceptionEvents);

        DetectCollapsedLeafAttribution(analysis);

        return analysis;
    }

    // A single leaf frame owning essentially everything is a property of the
    // CAPTURE, not a finding about the process - see CaptureContents for the
    // real capture this was found on and what it turned out to be.
    private const double LeafAttributionCollapseShare = 95.0;

    private static void DetectCollapsedLeafAttribution(CaptureAnalysis analysis)
    {
        if (analysis.Cpu.HotMethods.Count > 0 && analysis.Cpu.HotMethods[0].SelfPercent >= LeafAttributionCollapseShare)
        {
            analysis.Contents.CpuLeafAttributionCollapsed = true;
            analysis.Contents.CollapsedCpuLeafFrame = analysis.Cpu.HotMethods[0].Name;
            analysis.Contents.CollapsedCpuLeafShare = analysis.Cpu.HotMethods[0].SelfPercent;
        }

        if (analysis.Contention.TopSites.Count > 0 && analysis.Contention.TopSites[0].PercentOfTotalWait >= LeafAttributionCollapseShare)
        {
            analysis.Contents.ContentionLeafAttributionCollapsed = true;
            analysis.Contents.CollapsedContentionLeafFrame = analysis.Contention.TopSites[0].SiteName;
            analysis.Contents.CollapsedContentionLeafShare = analysis.Contention.TopSites[0].PercentOfTotalWait;
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // GC
    ////////////////////////////////////////////////////////////////////////////

    private static void BuildGc(GcAnalysis gc, List<GcEvent> gcEvents)
    {
        if (gcEvents == null || gcEvents.Count == 0)
        {
            return;
        }

        gc.Count = gcEvents.Count;

        bool haveFirstHeapSize = false;

        // Collected for the heap-growth fit below. Only GCs that actually
        // reported heap stats contribute - a GC whose GCHeapStats never
        // arrived carries TotalHeapSize 0, and feeding those to a regression
        // produces a dramatic downward "trend" out of missing data.
        List<double> fitTimeSeconds = new List<double>();
        List<double> fitHeapSizes = new List<double>();

        for (int gcIndex = 0; gcIndex < gcEvents.Count; ++gcIndex)
        {
            GcEvent gcEvent = gcEvents[gcIndex];

            GcRecord record = new GcRecord();
            record.Id = gcEvent.Id;
            record.Generation = gcEvent.Generation;
            record.Reason = gcEvent.Reason.ToString();
            record.Type = gcEvent.Type.ToString();
            record.PauseDurationMSec = gcEvent.PauseDurationMSec;
            record.PauseStartRelativeMSec = gcEvent.PauseStartRelativeMSec;
            record.Timestamp = gcEvent.Timestamp;
            record.TotalHeapSize = gcEvent.TotalHeapSize;
            record.TotalPromoted = gcEvent.TotalPromoted;
            record.GenerationSize0 = gcEvent.GenerationSize0;
            record.GenerationSize1 = gcEvent.GenerationSize1;
            record.GenerationSize2 = gcEvent.GenerationSize2;
            record.GenerationSizeLOH = gcEvent.GenerationSize3;
            record.GenerationSizePOH = gcEvent.GenerationSize4;
            record.TotalPromotedLOH = gcEvent.TotalPromotedSize3;
            record.NumHeaps = gcEvent.NumHeaps;

            // Every "induced" flavour, not just the plain one. A caller
            // reaching for GC.Collect is the finding, and which overload they
            // reached for does not change it.
            record.IsInduced = gcEvent.Reason == GCReason.Induced
                || gcEvent.Reason == GCReason.InducedNotForced
                || gcEvent.Reason == GCReason.InducedLowMemory
                || gcEvent.Reason == GCReason.InducedCompacting;

            record.IsBackground = gcEvent.Type == GCType.BackgroundGC;

            SummarizeHeaps(record, gcEvent);

            gc.Gcs.Add(record);

            gc.TotalPauseMSec += gcEvent.PauseDurationMSec;

            if (gcEvent.Generation >= 0 && gcEvent.Generation < gc.CountByGeneration.Length)
            {
                ++gc.CountByGeneration[gcEvent.Generation];
                gc.PauseByGeneration[gcEvent.Generation] += gcEvent.PauseDurationMSec;
            }

            if (gcEvent.PauseDurationMSec > gc.MaxPauseMSec)
            {
                gc.MaxPauseMSec = gcEvent.PauseDurationMSec;
                gc.MaxPauseGcId = gcEvent.Id;
                gc.MaxPauseGeneration = gcEvent.Generation;
                gc.MaxPauseReason = record.Reason;
            }

            if (record.IsInduced)
            {
                ++gc.InducedCount;

                if (gc.FirstInducedGcId < 0)
                {
                    gc.FirstInducedGcId = gcEvent.Id;
                }
            }

            if (gcEvent.Reason == GCReason.AllocLarge || gcEvent.Reason == GCReason.OutOfSpaceLOH)
            {
                ++gc.LohTriggeredCount;

                if (gc.FirstLohTriggeredGcId < 0)
                {
                    gc.FirstLohTriggeredGcId = gcEvent.Id;
                }
            }

            if (gcEvent.Generation == 2)
            {
                if (record.IsBackground)
                {
                    ++gc.BackgroundGen2Count;
                    gc.HasBackgroundGc = true;
                }
                else
                {
                    ++gc.BlockingGen2Count;
                    gc.BlockingGen2PauseMSec += gcEvent.PauseDurationMSec;
                }
            }
            else if (record.IsBackground)
            {
                gc.HasBackgroundGc = true;
            }

            if (gcEvent.NumHeaps > gc.HeapCount)
            {
                gc.HeapCount = gcEvent.NumHeaps;
            }

            if (record.HasPerHeapDetail && record.HeapPromotedSpread > gc.WorstHeapImbalance)
            {
                gc.WorstHeapImbalance = record.HeapPromotedSpread;
                gc.WorstHeapImbalanceGcId = gcEvent.Id;
            }

            if (record.HasPerHeapDetail)
            {
                gc.HasFragmentation = true;
                gc.FinalFragmentationBytes = record.FragmentationBytes;
                gc.FinalGen2AndLohBytes = record.GenerationSize2 + record.GenerationSizeLOH;
            }

            if (gcEvent.HasHeapStats && gcEvent.TotalHeapSize > 0)
            {
                if (!haveFirstHeapSize)
                {
                    gc.FirstHeapSizeBytes = gcEvent.TotalHeapSize;
                    haveFirstHeapSize = true;
                }

                gc.LastHeapSizeBytes = gcEvent.TotalHeapSize;

                if (gcEvent.TotalHeapSize > gc.PeakHeapSizeBytes)
                {
                    gc.PeakHeapSizeBytes = gcEvent.TotalHeapSize;
                }

                fitTimeSeconds.Add(gcEvent.PauseStartRelativeMSec / 1000.0);
                fitHeapSizes.Add(gcEvent.TotalHeapSize);
            }
        }

        // More than one heap is what Server GC looks like from the outside,
        // and it is the only signal in the capture for it - the trace does
        // not carry a "server GC is on" flag.
        gc.IsServerGc = gc.HeapCount > 1;

        FitHeapGrowth(gc, fitTimeSeconds, fitHeapSizes);
    }

    // Per-heap promotion spread for one GC, as a coefficient of variation
    // (standard deviation over mean). 0 means every heap promoted the same
    // number of bytes; it grows without bound as they diverge, which makes it
    // comparable across captures with different heap counts and heap sizes in
    // a way a raw max-minus-min is not.
    private static void SummarizeHeaps(GcRecord record, GcEvent gcEvent)
    {
        if (gcEvent.Heaps == null || gcEvent.Heaps.Count < 2)
        {
            return;
        }

        long fragmentationBytes = 0;
        long promotedTotal = 0;
        long promotedMax = long.MinValue;
        long promotedMin = long.MaxValue;

        double[] promotedPerHeap = new double[gcEvent.Heaps.Count];

        for (int heapIndex = 0; heapIndex < gcEvent.Heaps.Count; ++heapIndex)
        {
            ClrGcHeap heap = gcEvent.Heaps[heapIndex];

            if (heap == null || heap.Generations == null)
            {
                return;
            }

            long heapPromoted = 0;

            for (int generationIndex = 0; generationIndex < heap.Generations.Length; ++generationIndex)
            {
                ref readonly ClrGcGeneration generation = ref heap.Generations[generationIndex];

                heapPromoted += generation.Out;

                // Generations 2 and 3 (gen2 and the LOH) ONLY, because
                // GcAnalysis.FinalGen2AndLohBytes is what this is reported
                // against and the two have to cover the same ground. Summing
                // every generation's free space over a gen2+LOH denominator
                // measured 937% fragmentation on a real capture whose gen2 was
                // nearly empty while its gen0 carried the free space - an
                // impossible number that looked like a very confident finding.
                // Gen0/gen1 free space is also not what anybody means by
                // fragmentation: those generations are compacted on every
                // collection, so their free space is budget, not damage.
                if (generationIndex >= 2)
                {
                    fragmentationBytes += generation.Fragmentation;
                }
            }

            promotedPerHeap[heapIndex] = heapPromoted;
            promotedTotal += heapPromoted;

            if (heapPromoted > promotedMax)
            {
                promotedMax = heapPromoted;
            }

            if (heapPromoted < promotedMin)
            {
                promotedMin = heapPromoted;
            }
        }

        record.HasPerHeapDetail = true;
        record.FragmentationBytes = fragmentationBytes;
        record.HeapPromotedMax = promotedMax;
        record.HeapPromotedMin = promotedMin;

        double mean = (double)promotedTotal / promotedPerHeap.Length;

        if (mean <= 0)
        {
            // Every heap promoted nothing. Perfectly balanced, and reporting
            // a spread of "undefined" as some large number here is how a
            // quiet gen0 GC ends up topping an imbalance ranking.
            record.HeapPromotedSpread = 0;
            return;
        }

        double sumOfSquaredDeviations = 0;

        for (int heapIndex = 0; heapIndex < promotedPerHeap.Length; ++heapIndex)
        {
            double deviation = promotedPerHeap[heapIndex] - mean;
            sumOfSquaredDeviations += deviation * deviation;
        }

        double standardDeviation = Math.Sqrt(sumOfSquaredDeviations / promotedPerHeap.Length);

        record.HeapPromotedSpread = standardDeviation / mean;
    }

    // Ordinary least squares plus the coefficient of determination. R^2 is
    // carried alongside the slope because the slope alone cannot tell a leak
    // from a process that warmed up and levelled off - both have a positive
    // slope, and only one of them fits a line.
    private static void FitHeapGrowth(GcAnalysis gc, List<double> timeSeconds, List<double> heapSizes)
    {
        if (timeSeconds.Count < MinimumGcsForHeapGrowthFit)
        {
            return;
        }

        double sumTime = 0;
        double sumSize = 0;

        for (int pointIndex = 0; pointIndex < timeSeconds.Count; ++pointIndex)
        {
            sumTime += timeSeconds[pointIndex];
            sumSize += heapSizes[pointIndex];
        }

        double meanTime = sumTime / timeSeconds.Count;
        double meanSize = sumSize / heapSizes.Count;

        double covariance = 0;
        double timeVariance = 0;

        for (int pointIndex = 0; pointIndex < timeSeconds.Count; ++pointIndex)
        {
            double timeDeviation = timeSeconds[pointIndex] - meanTime;
            double sizeDeviation = heapSizes[pointIndex] - meanSize;

            covariance += timeDeviation * sizeDeviation;
            timeVariance += timeDeviation * timeDeviation;
        }

        if (timeVariance <= 0)
        {
            // Every GC reported the same timestamp. Nothing to fit against.
            return;
        }

        double slope = covariance / timeVariance;
        double intercept = meanSize - slope * meanTime;

        double residualSumOfSquares = 0;
        double totalSumOfSquares = 0;

        for (int pointIndex = 0; pointIndex < timeSeconds.Count; ++pointIndex)
        {
            double predicted = slope * timeSeconds[pointIndex] + intercept;
            double residual = heapSizes[pointIndex] - predicted;
            double sizeDeviation = heapSizes[pointIndex] - meanSize;

            residualSumOfSquares += residual * residual;
            totalSumOfSquares += sizeDeviation * sizeDeviation;
        }

        gc.HasHeapGrowthFit = true;
        gc.HeapGrowthBytesPerSecond = slope;
        gc.HeapGrowthRSquared = totalSumOfSquares > 0 ? 1.0 - (residualSumOfSquares / totalSumOfSquares) : 0;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Allocation
    ////////////////////////////////////////////////////////////////////////////

    private static void BuildAllocation(AllocationAnalysis allocation, List<AllocationEvent> allocationEvents)
    {
        if (allocationEvents == null || allocationEvents.Count == 0)
        {
            return;
        }

        Dictionary<string, AllocatedTypeRecord> byTypeName = new Dictionary<string, AllocatedTypeRecord>(StringComparer.Ordinal);

        for (int eventIndex = 0; eventIndex < allocationEvents.Count; ++eventIndex)
        {
            AllocationEvent allocationEvent = allocationEvents[eventIndex];

            string typeName = allocationEvent.TypeName ?? "<unknown>";

            if (!byTypeName.TryGetValue(typeName, out AllocatedTypeRecord record))
            {
                record = new AllocatedTypeRecord();
                record.TypeName = typeName;
                byTypeName[typeName] = record;
            }

            record.TotalBytes += allocationEvent.AllocationAmount;
            ++record.TickCount;

            allocation.TotalSampledBytes += allocationEvent.AllocationAmount;
            ++allocation.TotalTickCount;

            if (allocationEvent.AllocationKind == GCAllocationKind.Large)
            {
                ++record.LargeCount;
                ++allocation.LargeObjectTickCount;
                allocation.LargeObjectSampledBytes += allocationEvent.AllocationAmount;
            }
        }

        allocation.DistinctTypeCount = byTypeName.Count;

        List<AllocatedTypeRecord> ranked = new List<AllocatedTypeRecord>(byTypeName.Values);
        ranked.Sort(CompareAllocatedTypesByBytesDescending);

        int keepCount = ranked.Count < TopTypeLimit ? ranked.Count : TopTypeLimit;

        for (int rankIndex = 0; rankIndex < keepCount; ++rankIndex)
        {
            AllocatedTypeRecord record = ranked[rankIndex];
            record.PercentOfTotalBytes = allocation.TotalSampledBytes > 0
                ? record.TotalBytes * 100.0 / allocation.TotalSampledBytes
                : 0;

            allocation.TopTypes.Add(record);
        }
    }

    private static int CompareAllocatedTypesByBytesDescending(AllocatedTypeRecord left, AllocatedTypeRecord right)
    {
        int byBytes = right.TotalBytes.CompareTo(left.TotalBytes);

        if (byBytes != 0)
        {
            return byBytes;
        }

        return string.CompareOrdinal(left.TypeName, right.TypeName);
    }

    ////////////////////////////////////////////////////////////////////////////
    // CPU
    ////////////////////////////////////////////////////////////////////////////

    private static void BuildCpu(
        CpuAnalysis cpu,
        CpuCategoryBuilder.CategoryTotals[] categoryTotals,
        List<SampleEvent> sampleEvents,
        StackTable stackTable,
        MethodSymbolTable symbolTable)
    {
        if (sampleEvents == null || sampleEvents.Count == 0 || categoryTotals == null)
        {
            return;
        }

        cpu.TotalSampleCount = sampleEvents.Count;
        cpu.DistinctStackCount = stackTable != null ? stackTable.Count : 0;

        // Merged across categories to produce the global self-sample ranking.
        // This is the whole reason no fourth pass over the samples is needed:
        // the category pass already attributed every sample to its leaf frame.
        Dictionary<int, long> globalSelfSamplesByFrameId = new Dictionary<int, long>();

        for (int categoryIndex = 0; categoryIndex < categoryTotals.Length; ++categoryIndex)
        {
            CpuCategoryBuilder.CategoryTotals totals = categoryTotals[categoryIndex];
            CpuCategory category = (CpuCategory)categoryIndex;

            CpuCategoryRecord record = new CpuCategoryRecord();
            record.Id = categoryIndex;
            record.Name = CpuCategoryClassifier.DisplayName(category);
            record.SelfSamples = totals.SelfSamples;
            record.OnStackSamples = totals.OnStackSamples;
            record.SelfPercent = totals.SelfSamples * 100.0 / sampleEvents.Count;
            record.OnStackPercent = totals.OnStackSamples * 100.0 / sampleEvents.Count;

            if (totals.SelfSamplesByFrameId != null)
            {
                record.TopMethods = RankMethods(totals.SelfSamplesByFrameId, symbolTable, sampleEvents.Count, TopMethodsPerCategoryLimit);

                foreach (KeyValuePair<int, long> entry in totals.SelfSamplesByFrameId)
                {
                    globalSelfSamplesByFrameId.TryGetValue(entry.Key, out long existing);
                    globalSelfSamplesByFrameId[entry.Key] = existing + entry.Value;
                }
            }

            cpu.Categories.Add(record);

            if (category == CpuCategory.Unresolved)
            {
                cpu.UnresolvedPercent = record.SelfPercent;

                List<KeyValuePair<string, long>> rankedModules =
                    CpuCategoryBuilder.RankUnresolvedModules(totals.SelfSamplesByFrameId, symbolTable);

                int moduleKeepCount = rankedModules.Count < TopUnresolvedModuleLimit
                    ? rankedModules.Count
                    : TopUnresolvedModuleLimit;

                for (int moduleIndex = 0; moduleIndex < moduleKeepCount; ++moduleIndex)
                {
                    UnresolvedModuleRecord moduleRecord = new UnresolvedModuleRecord();
                    moduleRecord.ModuleName = rankedModules[moduleIndex].Key;
                    moduleRecord.SelfSamples = rankedModules[moduleIndex].Value;
                    moduleRecord.SelfPercent = rankedModules[moduleIndex].Value * 100.0 / sampleEvents.Count;

                    cpu.UnresolvedModules.Add(moduleRecord);
                }
            }
        }

        cpu.HotMethods = RankMethods(globalSelfSamplesByFrameId, symbolTable, sampleEvents.Count, TopMethodLimit);
    }

    private static List<HotMethodRecord> RankMethods(Dictionary<int, long> selfSamplesByFrameId, MethodSymbolTable symbolTable, long totalSampleCount, int limit)
    {
        List<HotMethodRecord> ranked = new List<HotMethodRecord>();

        if (selfSamplesByFrameId == null || symbolTable == null)
        {
            return ranked;
        }

        foreach (KeyValuePair<int, long> entry in selfSamplesByFrameId)
        {
            HotMethodRecord record = new HotMethodRecord();
            record.FrameId = entry.Key;
            record.Name = symbolTable.NameForId(entry.Key) ?? "<unknown>";
            record.SelfSamples = entry.Value;
            record.SelfPercent = totalSampleCount > 0 ? entry.Value * 100.0 / totalSampleCount : 0;
            record.IsIdleWait = CpuIdleWaitClassifier.IsKnownIdleWaitLeafMethodName(record.Name);

            ranked.Add(record);
        }

        ranked.Sort(CompareHotMethodsBySamplesDescending);

        if (ranked.Count > limit)
        {
            ranked.RemoveRange(limit, ranked.Count - limit);
        }

        return ranked;
    }

    // Ties break on frame id, matching CpuProfileJsonExporter's own
    // WriteHotMethods - two methods with identical sample counts must not
    // swap places between runs over the same capture.
    private static int CompareHotMethodsBySamplesDescending(HotMethodRecord left, HotMethodRecord right)
    {
        int bySamples = right.SelfSamples.CompareTo(left.SelfSamples);

        if (bySamples != 0)
        {
            return bySamples;
        }

        return left.FrameId.CompareTo(right.FrameId);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Threading
    ////////////////////////////////////////////////////////////////////////////

    private static void BuildThreading(
        ThreadingAnalysis threading,
        ThreadingSummary summary,
        ThreadActivityProfileSet threadProfiles,
        StackTable stackTable,
        MethodSymbolTable symbolTable)
    {
        if (summary != null)
        {
            threading.HasThreadPoolData = summary.HasThreadPoolData;
            threading.PeakActiveWorkerThreads = summary.PeakActiveWorkerThreads;
            threading.MinActiveWorkerThreads = summary.MinActiveWorkerThreads;
            threading.FinalActiveWorkerThreads = summary.FinalActiveWorkerThreads;
            threading.WorkerThreadStartCount = summary.WorkerThreadStartCount;
            threading.AdjustmentCount = summary.Adjustments != null ? summary.Adjustments.Count : 0;

            if (summary.Adjustments != null)
            {
                for (int adjustmentIndex = 0; adjustmentIndex < summary.Adjustments.Count; ++adjustmentIndex)
                {
                    // "Stall driven" is a property of the adjustment's REASON
                    // code, not a field on the record - the same test
                    // ThreadingJsonExporter.WriteStallCorrelation applies, so
                    // an insight's count and the Threading view's stall table
                    // are counting the same adjustments.
                    if (ThreadAdjustmentReason.IsStallDriven(summary.Adjustments[adjustmentIndex].Reason))
                    {
                        ++threading.StallDrivenAdjustmentCount;
                    }
                }
            }

            // What stall-driven injection actually produces: the pool grows.
            // Reported as the span rather than as a rate because hill climbing
            // is not periodic - it reacts, so an average per second smears a
            // burst of injections across the quiet stretch either side of it.
            threading.WorkerThreadGrowth = summary.PeakActiveWorkerThreads - summary.MinActiveWorkerThreads;
        }

        if (threadProfiles == null)
        {
            return;
        }

        threading.HasSampleTypeData = threadProfiles.HasSampleTypeData;
        threading.BenignlyParkedThreadCount = threadProfiles.BenignlyParkedThreadCount;
        threading.ThreadCount = threadProfiles.Ranked.Count;

        for (int threadIndex = 0; threadIndex < threadProfiles.Ranked.Count; ++threadIndex)
        {
            ThreadActivityProfile profile = threadProfiles.Ranked[threadIndex];

            ThreadRecord record = new ThreadRecord();
            record.ThreadId = profile.ThreadId;
            record.Role = ThreadActivityProfiler.NameForRole(profile.Role);
            record.RoleId = (int)profile.Role;
            record.IsBenignlyParked = profile.IsBenignlyParked;
            record.IsPoolWorker = profile.IsPoolWorker;
            record.SampleCount = profile.SampleCount;
            record.ManagedFraction = profile.ManagedFraction;
            record.WaitFraction = profile.WaitFraction;
            record.PoolParkFraction = profile.PoolParkFraction;
            record.TopStacksShare = profile.TopStacksShare;
            record.SampledSpanMSec = profile.SampledSpanMSec;
            record.LongestContinuousIdleMSec = profile.LongestContinuousIdleMSec;
            record.WakeCount = profile.WakeCount;
            record.ContentionCount = profile.ContentionCount;
            record.ContentionWaitMSec = profile.ContentionWaitMSec;
            record.ContentionShareOfLife = profile.ContentionShareOfLife;
            record.TopStacks = ResolveTopStacks(profile, stackTable, symbolTable);

            threading.Threads.Add(record);

            switch (profile.Role)
            {
                case ThreadActivityRole.BlockedPoolWorker:
                    ++threading.BlockedPoolWorkerCount;
                    break;

                case ThreadActivityRole.BlockedThread:
                    ++threading.BlockedThreadCount;
                    break;

                case ThreadActivityRole.ActiveThread:
                    ++threading.ActiveThreadCount;
                    break;

                case ThreadActivityRole.IdlePoolWorker:
                    ++threading.IdlePoolWorkerCount;
                    break;
            }
        }
    }

    private static List<ThreadStackRecord> ResolveTopStacks(ThreadActivityProfile profile, StackTable stackTable, MethodSymbolTable symbolTable)
    {
        List<ThreadStackRecord> stacks = new List<ThreadStackRecord>();

        if (stackTable == null || symbolTable == null)
        {
            return stacks;
        }

        for (int stackRank = 0; stackRank < profile.TopStackIndices.Length; ++stackRank)
        {
            long[] stackFrames = stackTable.FramesAt(profile.TopStackIndices[stackRank]);

            if (stackFrames.Length == 0)
            {
                continue;
            }

            ThreadStackRecord record = new ThreadStackRecord();
            record.SampleCount = stackRank < profile.TopStackSampleCounts.Length ? profile.TopStackSampleCounts[stackRank] : 0;
            record.Share = profile.SampleCount > 0 ? (double)record.SampleCount / profile.SampleCount : 0;

            int keepCount = stackFrames.Length < DominantStackFrameLimit ? stackFrames.Length : DominantStackFrameLimit;

            for (int frameIndex = 0; frameIndex < keepCount; ++frameIndex)
            {
                // LastSampleMSec rather than a per-sample time: the symbol
                // table is time-aware (a method can be rejitted), and these are
                // by definition the stacks the thread held for most of its
                // life, so any instant inside that life resolves them the same
                // way.
                record.Frames.Add(symbolTable.Resolve(stackFrames[frameIndex], profile.LastSampleMSec));
            }

            stacks.Add(record);
        }

        return stacks;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Contention
    ////////////////////////////////////////////////////////////////////////////

    private sealed class ContentionSiteAccumulator
    {
        public int LeafFrameId;
        public int ContentionCount;
        public double TotalWaitMSec;
        public double MaxWaitMSec;
        public HashSet<long> WaiterThreadIds = new HashSet<long>();
    }

    private static void BuildContention(
        ContentionAnalysis contention,
        List<ContentionEvent> contentionEvents,
        StackTable stackTable,
        MethodSymbolTable symbolTable)
    {
        if (contentionEvents == null || contentionEvents.Count == 0)
        {
            return;
        }

        contention.TotalContentionCount = contentionEvents.Count;

        Dictionary<int, ContentionSiteAccumulator> byLeafFrameId = new Dictionary<int, ContentionSiteAccumulator>();
        HashSet<long> distinctLockIds = new HashSet<long>();

        for (int eventIndex = 0; eventIndex < contentionEvents.Count; ++eventIndex)
        {
            ContentionEvent contentionEvent = contentionEvents[eventIndex];

            contention.TotalWaitMSec += contentionEvent.DurationMSec;

            if (contentionEvent.DurationMSec > contention.MaxWaitMSec)
            {
                contention.MaxWaitMSec = contentionEvent.DurationMSec;
            }

            if (contentionEvent.LockId != 0)
            {
                distinctLockIds.Add(contentionEvent.LockId);
            }

            // Grouped by resolved leaf frame id, exactly as
            // ContentionJsonExporter groups its topSites - so a site named by
            // an insight is findable by that name in the Contention view.
            int leafFrameId = ResolveLeafFrameId(contentionEvent.StackIndex, contentionEvent.RelativeMSec, stackTable, symbolTable);

            if (!byLeafFrameId.TryGetValue(leafFrameId, out ContentionSiteAccumulator accumulator))
            {
                accumulator = new ContentionSiteAccumulator();
                accumulator.LeafFrameId = leafFrameId;
                byLeafFrameId[leafFrameId] = accumulator;
            }

            ++accumulator.ContentionCount;
            accumulator.TotalWaitMSec += contentionEvent.DurationMSec;
            accumulator.WaiterThreadIds.Add(contentionEvent.ThreadId);

            if (contentionEvent.DurationMSec > accumulator.MaxWaitMSec)
            {
                accumulator.MaxWaitMSec = contentionEvent.DurationMSec;
            }
        }

        contention.DistinctSiteCount = byLeafFrameId.Count;
        contention.DistinctLockCount = distinctLockIds.Count;

        List<ContentionSiteAccumulator> ranked = new List<ContentionSiteAccumulator>(byLeafFrameId.Values);
        ranked.Sort(CompareContentionSitesByWaitDescending);

        int keepCount = ranked.Count < TopSiteLimit ? ranked.Count : TopSiteLimit;

        for (int rankIndex = 0; rankIndex < keepCount; ++rankIndex)
        {
            ContentionSiteAccumulator accumulator = ranked[rankIndex];

            ContentionSiteRecord record = new ContentionSiteRecord();
            record.SiteName = accumulator.LeafFrameId >= 0 && symbolTable != null
                ? (symbolTable.NameForId(accumulator.LeafFrameId) ?? "<unknown>")
                : "<no stack>";
            record.ContentionCount = accumulator.ContentionCount;
            record.TotalWaitMSec = accumulator.TotalWaitMSec;
            record.AverageWaitMSec = accumulator.ContentionCount > 0 ? accumulator.TotalWaitMSec / accumulator.ContentionCount : 0;
            record.MaxWaitMSec = accumulator.MaxWaitMSec;
            record.PercentOfTotalWait = contention.TotalWaitMSec > 0 ? accumulator.TotalWaitMSec * 100.0 / contention.TotalWaitMSec : 0;
            record.WaiterThreadCount = accumulator.WaiterThreadIds.Count;

            contention.TopSites.Add(record);
        }
    }

    private static int ResolveLeafFrameId(int stackIndex, double relativeMSec, StackTable stackTable, MethodSymbolTable symbolTable)
    {
        if (stackTable == null || symbolTable == null)
        {
            return -1;
        }

        long[] frames = stackTable.FramesAt(stackIndex);

        if (frames.Length == 0)
        {
            return -1;
        }

        return symbolTable.ResolveId(frames[0], relativeMSec);
    }

    private static int CompareContentionSitesByWaitDescending(ContentionSiteAccumulator left, ContentionSiteAccumulator right)
    {
        int byWait = right.TotalWaitMSec.CompareTo(left.TotalWaitMSec);

        if (byWait != 0)
        {
            return byWait;
        }

        return left.LeafFrameId.CompareTo(right.LeafFrameId);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Exceptions
    ////////////////////////////////////////////////////////////////////////////

    private static void BuildExceptions(ExceptionAnalysis exceptions, List<ExceptionEvent> exceptionEvents)
    {
        if (exceptionEvents == null || exceptionEvents.Count == 0)
        {
            return;
        }

        exceptions.TotalCount = exceptionEvents.Count;

        Dictionary<string, ExceptionTypeRecord> byTypeName = new Dictionary<string, ExceptionTypeRecord>(StringComparer.Ordinal);

        for (int eventIndex = 0; eventIndex < exceptionEvents.Count; ++eventIndex)
        {
            ExceptionEvent exceptionEvent = exceptionEvents[eventIndex];

            string typeName = exceptionEvent.ExceptionType ?? "<unknown>";

            if (!byTypeName.TryGetValue(typeName, out ExceptionTypeRecord record))
            {
                record = new ExceptionTypeRecord();
                record.TypeName = typeName;
                record.SampleMessage = exceptionEvent.ExceptionMessage ?? "";
                byTypeName[typeName] = record;
            }

            ++record.Count;
        }

        exceptions.DistinctTypeCount = byTypeName.Count;

        List<ExceptionTypeRecord> ranked = new List<ExceptionTypeRecord>(byTypeName.Values);
        ranked.Sort(CompareExceptionTypesByCountDescending);

        int keepCount = ranked.Count < TopTypeLimit ? ranked.Count : TopTypeLimit;

        for (int rankIndex = 0; rankIndex < keepCount; ++rankIndex)
        {
            ExceptionTypeRecord record = ranked[rankIndex];
            record.PercentOfTotal = exceptions.TotalCount > 0 ? record.Count * 100.0 / exceptions.TotalCount : 0;

            exceptions.TopTypes.Add(record);
        }
    }

    private static int CompareExceptionTypesByCountDescending(ExceptionTypeRecord left, ExceptionTypeRecord right)
    {
        int byCount = right.Count.CompareTo(left.Count);

        if (byCount != 0)
        {
            return byCount;
        }

        return string.CompareOrdinal(left.TypeName, right.TypeName);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Analysis)
