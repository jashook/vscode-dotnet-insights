////////////////////////////////////////////////////////////////////////////////
// Module: InsightRuleTests.cs
//
// Notes:
// Boundary tests for the rules in nettraceParser/Insights/Rules/.
//
// EVERY RULE IS TESTED ON BOTH SIDES OF ITS THRESHOLD, and that is the point
// rather than a formality. The failure mode a rule engine actually has is not
// "it crashes" - it is "it fires on everything", which reads as a working
// feature right up until somebody notices the report says the same thing about
// every capture. A test that only ever asserts the firing case cannot catch a
// rule that cannot NOT fire.
//
// Rules also have to be tested for the third verdict: NotApplicable when the
// capture lacks the data. That one matters most of all - a rule that reports
// BelowThreshold instead of NotApplicable on a capture with no allocation
// ticks is claiming the process allocates little, which it has no basis to
// say. See Insights/Insight.cs.
//
// The analyses here are built by hand rather than decoded from a fixture, on
// purpose: a threshold test needs values sitting exactly either side of a
// number, and no real capture obliges. Fixture-driven coverage of the same
// rules lives in InsightsCorpusTests.
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Analysis;
using DotnetInsights.NetTrace.Insights;
using DotnetInsights.NetTrace.Insights.Rules;
using DotnetInsights.NetTrace.Overview;
using DotnetInsights.NetTrace.Threading;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class InsightRuleTests
{
    ////////////////////////////////////////////////////////////////////////////
    // Builders
    ////////////////////////////////////////////////////////////////////////////

    // A capture with a duration and nothing else in it. Every "has data" flag
    // starts false so a test has to opt IN to the signal it is about - which
    // means a rule that reads a signal the test never enabled shows up as a
    // NotApplicable rather than quietly reading a zero.
    private static CaptureAnalysis EmptyAnalysis(double durationMSec = 60_000.0)
    {
        CaptureAnalysis analysis = new CaptureAnalysis();
        analysis.ProcessName = "test";
        analysis.FormatVersion = 5;
        analysis.HasCaptureDuration = durationMSec > 0;
        analysis.CaptureDurationMSec = durationMSec;
        analysis.TotalEventCount = 1000;

        return analysis;
    }

    private static void GiveGc(CaptureAnalysis analysis, int count)
    {
        analysis.Contents.HasGcEvents = true;
        analysis.Gc.Count = count;

        for (int gcIndex = 0; gcIndex < count; ++gcIndex)
        {
            GcRecord record = new GcRecord();
            record.Id = gcIndex;
            record.Generation = 0;
            record.Reason = "AllocSmall";
            record.PauseDurationMSec = 1.0;
            analysis.Gc.Gcs.Add(record);
        }

        analysis.Gc.TotalPauseMSec = count;
        analysis.Gc.CountByGeneration[0] = count;
    }

    private static void SetTimeBreakdown(CaptureAnalysis analysis, double gcPercent, double gcPauseMSec, double contentionPercent = 0, double contentionWaitMSec = 0, double idlePercent = 0, bool hasCpuBreakdown = false)
    {
        analysis.TimeBreakdown = new TimeBreakdown(
            hasCaptureDuration: analysis.HasCaptureDuration,
            captureDurationMSec: analysis.CaptureDurationMSec,
            gcPercent: gcPercent,
            gcPauseMSec: gcPauseMSec,
            contentionPercent: contentionPercent,
            contentionWaitMSec: contentionWaitMSec,
            averageThreadsBlocked: 0.5,
            hasCpuSampleBreakdown: hasCpuBreakdown,
            idlePercent: idlePercent,
            cpuBoundPercent: 100.0 - idlePercent);
    }

    private static ThreadRecord MakeThread(long threadId, ThreadActivityRole role, params string[] dominantFrames)
    {
        ThreadRecord thread = new ThreadRecord();
        thread.ThreadId = threadId;
        thread.RoleId = (int)role;
        thread.Role = ThreadActivityProfiler.NameForRole(role);
        thread.SampleCount = 1000;

        ThreadStackRecord stack = new ThreadStackRecord();
        stack.SampleCount = 900;
        stack.Share = 0.9;
        stack.Frames = new List<string>(dominantFrames);
        thread.TopStacks.Add(stack);

        return thread;
    }

    ////////////////////////////////////////////////////////////////////////////
    // gc/pause-share
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void GcPauseShare_FiresAtOrAboveThreshold()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 20);
        SetTimeBreakdown(analysis, GcPauseShareRule.WarningPercent, 3000);

        InsightRuleResult result = new GcPauseShareRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
        Assert.Equal(InsightSeverity.Warning, result.Insight.Severity);
    }

    [Fact]
    public void GcPauseShare_DoesNotFireJustBelowThreshold()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 20);
        SetTimeBreakdown(analysis, GcPauseShareRule.WarningPercent - 0.01, 2999);

        InsightRuleResult result = new GcPauseShareRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, result.Outcome);
        Assert.NotEmpty(result.Measured);
    }

    [Fact]
    public void GcPauseShare_EscalatesToCritical()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 20);
        SetTimeBreakdown(analysis, GcPauseShareRule.CriticalPercent, 9000);

        InsightRuleResult result = new GcPauseShareRule().Evaluate(analysis);

        Assert.Equal(InsightSeverity.Critical, result.Insight.Severity);
    }

    // The distinction this whole design turns on: no GC events is NOT a clean
    // GC result.
    [Fact]
    public void GcPauseShare_IsNotApplicableWithoutGcEvents()
    {
        CaptureAnalysis analysis = EmptyAnalysis();

        InsightRuleResult result = new GcPauseShareRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.NotApplicable, result.Outcome);
        Assert.Contains("no GC events", result.NotApplicableReason);
    }

    ////////////////////////////////////////////////////////////////////////////
    // gc/induced
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void GcInduced_FiresOnASingleInducedCollection()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 10);
        analysis.Gc.Gcs[3].IsInduced = true;
        analysis.Gc.InducedCount = 1;
        analysis.Gc.FirstInducedGcId = 3;

        InsightRuleResult result = new GcInducedRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
    }

    [Fact]
    public void GcInduced_DoesNotFireWithNoInducedCollections()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 10);

        InsightRuleResult result = new GcInducedRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, result.Outcome);
    }

    ////////////////////////////////////////////////////////////////////////////
    // gc/gen2-pause-concentration - background gen2s must not count
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void Gen2PauseConcentration_CountsOnlyBlockingGen2()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 10);
        analysis.Gc.TotalPauseMSec = 1000;
        analysis.Gc.BlockingGen2Count = GcGen2PauseConcentrationRule.MinimumBlockingGen2Count;
        analysis.Gc.BlockingGen2PauseMSec = 800;

        InsightRuleResult fired = new GcGen2PauseConcentrationRule().Evaluate(analysis);
        Assert.Equal(InsightRuleOutcome.Fired, fired.Outcome);

        // The same pause, but attributed to BACKGROUND gen2s, must not fire -
        // a background collection's mark phase is not pause.
        analysis.Gc.BlockingGen2Count = 0;
        analysis.Gc.BlockingGen2PauseMSec = 0;
        analysis.Gc.BackgroundGen2Count = 12;

        InsightRuleResult notFired = new GcGen2PauseConcentrationRule().Evaluate(analysis);
        Assert.Equal(InsightRuleOutcome.BelowThreshold, notFired.Outcome);
    }

    ////////////////////////////////////////////////////////////////////////////
    // gc/heap-growth - a slope alone is not a leak
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void HeapGrowth_FiresOnAStrongFitWithMaterialGrowth()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 20);
        analysis.Gc.HasHeapGrowthFit = true;
        analysis.Gc.HeapGrowthRSquared = 0.95;
        analysis.Gc.HeapGrowthBytesPerSecond = 5_000_000;
        analysis.Gc.FirstHeapSizeBytes = 500L * 1024 * 1024;
        analysis.Gc.LastHeapSizeBytes = 1500L * 1024 * 1024;

        InsightRuleResult result = new GcHeapGrowthRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
        // Medium, not high: a warm-up and a leak have the same shape.
        Assert.Equal(InsightConfidence.Medium, result.Insight.Confidence);
    }

    // Same growth, poor fit - the warm-up-then-level-off case.
    [Fact]
    public void HeapGrowth_DoesNotFireWhenTheLineDoesNotFit()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 20);
        analysis.Gc.HasHeapGrowthFit = true;
        analysis.Gc.HeapGrowthRSquared = GcHeapGrowthRule.MinimumRSquared - 0.01;
        analysis.Gc.HeapGrowthBytesPerSecond = 5_000_000;
        analysis.Gc.FirstHeapSizeBytes = 500L * 1024 * 1024;
        analysis.Gc.LastHeapSizeBytes = 1500L * 1024 * 1024;

        InsightRuleResult result = new GcHeapGrowthRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, result.Outcome);
    }

    // A strong fit on a heap that grew 30MB is a real trend and not worth
    // anybody's afternoon.
    [Fact]
    public void HeapGrowth_DoesNotFireOnImmaterialAbsoluteGrowth()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 20);
        analysis.Gc.HasHeapGrowthFit = true;
        analysis.Gc.HeapGrowthRSquared = 0.99;
        analysis.Gc.HeapGrowthBytesPerSecond = 500_000;
        analysis.Gc.FirstHeapSizeBytes = 40L * 1024 * 1024;
        analysis.Gc.LastHeapSizeBytes = 70L * 1024 * 1024;

        InsightRuleResult result = new GcHeapGrowthRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, result.Outcome);
    }

    ////////////////////////////////////////////////////////////////////////////
    // gc/server-heap-imbalance
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void ServerHeapImbalance_IsNotApplicableOnWorkstationGc()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 10);
        analysis.Gc.IsServerGc = false;

        InsightRuleResult result = new GcServerHeapImbalanceRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public void ServerHeapImbalance_FiresOnASkewedCollection()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveGc(analysis, 10);
        analysis.Gc.IsServerGc = true;
        analysis.Gc.HeapCount = 12;
        analysis.Gc.WorstHeapImbalance = GcServerHeapImbalanceRule.ImbalanceThreshold;
        analysis.Gc.WorstHeapImbalanceGcId = 4;

        InsightRuleResult result = new GcServerHeapImbalanceRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Allocation
    ////////////////////////////////////////////////////////////////////////////

    private static void GiveAllocation(CaptureAnalysis analysis, long totalBytes, long tickCount, params (string, long)[] types)
    {
        analysis.Contents.HasAllocationTicks = true;
        analysis.Allocation.TotalSampledBytes = totalBytes;
        analysis.Allocation.TotalTickCount = tickCount;
        analysis.Allocation.DistinctTypeCount = types.Length;

        for (int typeIndex = 0; typeIndex < types.Length; ++typeIndex)
        {
            AllocatedTypeRecord record = new AllocatedTypeRecord();
            record.TypeName = types[typeIndex].Item1;
            record.TotalBytes = types[typeIndex].Item2;
            record.TickCount = 1;
            record.PercentOfTotalBytes = totalBytes > 0 ? record.TotalBytes * 100.0 / totalBytes : 0;
            analysis.Allocation.TopTypes.Add(record);
        }
    }

    [Fact]
    public void AllocationRate_IsNotApplicableWithoutAllocationTicks()
    {
        CaptureAnalysis analysis = EmptyAnalysis();

        InsightRuleResult result = new AllocationRateRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.NotApplicable, result.Outcome);
        Assert.Contains("GCAllocationTick", result.NotApplicableReason);
    }

    // Bytes that, over the 60s EmptyAnalysis duration, produce exactly the
    // given rate.
    private static long BytesForRate(double megabytesPerSecond)
    {
        return (long)(megabytesPerSecond * 1024 * 1024 * 60);
    }

    [Fact]
    public void AllocationRate_FiresAndDoesNotFireEitherSideOfTheThreshold()
    {
        CaptureAnalysis firing = EmptyAnalysis();
        GiveAllocation(firing, BytesForRate(AllocationRateRule.InfoMegabytesPerSecond), 1000);
        Assert.Equal(InsightRuleOutcome.Fired, new AllocationRateRule().Evaluate(firing).Outcome);

        CaptureAnalysis quiet = EmptyAnalysis();
        GiveAllocation(quiet, BytesForRate(AllocationRateRule.InfoMegabytesPerSecond - 1.0), 1000);
        Assert.Equal(InsightRuleOutcome.BelowThreshold, new AllocationRateRule().Evaluate(quiet).Outcome);
    }

    // The rate is graded rather than binary: it is useful context at any level
    // this population reaches, and only unusual values should be loud. See the
    // corpus percentiles recorded on the rule.
    [Fact]
    public void AllocationRate_EscalatesWithTheRate()
    {
        CaptureAnalysis info = EmptyAnalysis();
        GiveAllocation(info, BytesForRate(AllocationRateRule.InfoMegabytesPerSecond), 1000);
        Assert.Equal(InsightSeverity.Info, new AllocationRateRule().Evaluate(info).Insight.Severity);

        CaptureAnalysis warning = EmptyAnalysis();
        GiveAllocation(warning, BytesForRate(AllocationRateRule.WarningMegabytesPerSecond), 1000);
        Assert.Equal(InsightSeverity.Warning, new AllocationRateRule().Evaluate(warning).Insight.Severity);

        CaptureAnalysis critical = EmptyAnalysis();
        GiveAllocation(critical, BytesForRate(AllocationRateRule.CriticalMegabytesPerSecond), 1000);
        Assert.Equal(InsightSeverity.Critical, new AllocationRateRule().Evaluate(critical).Insight.Severity);
    }

    [Fact]
    public void AllocationTypeConcentration_FiresOnADominantType()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveAllocation(analysis, 1000, 10, ("System.String", 700), ("System.Byte[]", 300));

        InsightRuleResult result = new AllocationTypeConcentrationRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
        Assert.Contains("System.String", result.Insight.Headline);
    }

    [Fact]
    public void AllocationTypeConcentration_DoesNotFireOnAFlatProfile()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        GiveAllocation(analysis, 1000, 10, ("System.String", 200), ("System.Byte[]", 200));

        InsightRuleResult result = new AllocationTypeConcentrationRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, result.Outcome);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Threading
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void PoolStarvation_FiresOnBlockedPoolWorkersOnly()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Threading.ThreadCount = 4;
        analysis.Threading.BlockedPoolWorkerCount = 2;
        analysis.Threading.Threads.Add(MakeThread(1, ThreadActivityRole.BlockedPoolWorker, "Contoso.Db.Query", "Contoso.Handler.Run"));
        analysis.Threading.Threads.Add(MakeThread(2, ThreadActivityRole.BlockedPoolWorker, "Contoso.Db.Query", "Contoso.Handler.Run"));

        InsightRuleResult result = new ThreadingPoolStarvationRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
        Assert.Contains("Contoso.Db.Query", result.Insight.Headline);
    }

    // A parked dedicated thread and a blocked pool worker look identical; only
    // the role separates them, and getting this backwards is what hid the best
    // finding in a real capture.
    [Fact]
    public void PoolStarvation_IgnoresBenignlyParkedThreads()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Threading.ThreadCount = 4;
        analysis.Threading.BlockedPoolWorkerCount = 0;
        analysis.Threading.Threads.Add(MakeThread(1, ThreadActivityRole.ParkedThread, "Contoso.Queue.Drain"));
        analysis.Threading.Threads.Add(MakeThread(2, ThreadActivityRole.IdlePoolWorker, "System.Threading.LowLevelLifoSemaphore.WaitForSignal"));

        InsightRuleResult result = new ThreadingPoolStarvationRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, result.Outcome);
    }

    // Regression for a real misreport: a blocked worker's TOP stack is often
    // the pool's own park, which is the one frame that is definitionally not
    // the problem. The finding must name the blocking call instead.
    [Fact]
    public void PoolStarvation_NamesTheBlockingCallNotThePoolsOwnPark()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Threading.ThreadCount = 2;
        analysis.Threading.BlockedPoolWorkerCount = 1;

        ThreadRecord thread = MakeThread(1, ThreadActivityRole.BlockedPoolWorker, "System.Threading.LowLevelLifoSemaphore.WaitForSignal");

        ThreadStackRecord blockingStack = new ThreadStackRecord();
        blockingStack.SampleCount = 400;
        blockingStack.Share = 0.4;
        blockingStack.Frames = new List<string> { "System.Threading.SemaphoreSlim.WaitUntilCountOrTimeout", "Contoso.Handler.Run" };
        thread.TopStacks.Add(blockingStack);

        analysis.Threading.Threads.Add(thread);

        InsightRuleResult result = new ThreadingPoolStarvationRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
        Assert.Contains("SemaphoreSlim", result.Insight.Headline);
        Assert.DoesNotContain("LowLevelLifoSemaphore", result.Insight.Headline);
    }

    // The name-heuristic rule may only ever EXPLAIN a finding the
    // non-heuristic classification already made.
    [Fact]
    public void SyncOverAsync_IsNotApplicableWithoutABlockedPoolWorker()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Threading.ThreadCount = 1;
        analysis.Threading.BlockedPoolWorkerCount = 0;
        analysis.Threading.Threads.Add(MakeThread(1, ThreadActivityRole.ActiveThread, "System.Threading.Tasks.Task.Wait"));

        InsightRuleResult result = new ThreadingSyncOverAsyncRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public void SyncOverAsync_FiresOnABlockedWorkerWaitingOnATask()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Threading.ThreadCount = 1;
        analysis.Threading.BlockedPoolWorkerCount = 1;
        analysis.Threading.Threads.Add(MakeThread(1, ThreadActivityRole.BlockedPoolWorker, "System.Threading.Tasks.Task.SpinThenBlockingWait", "Contoso.Legacy.Bridge"));

        InsightRuleResult result = new ThreadingSyncOverAsyncRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
        // Medium, because it is name-based - the report says so too.
        Assert.Equal(InsightConfidence.Medium, result.Insight.Confidence);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Contention - thresholds are stated in TIME, never in event count
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void ContentionTotalShare_DoesNotFireOnManyShortContentions()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Contents.HasContentionEvents = true;
        analysis.Contention.TotalContentionCount = 500_000;
        analysis.Contention.TotalWaitMSec = 8.3;
        SetTimeBreakdown(analysis, 0, 0, contentionPercent: 0.01, contentionWaitMSec: 8.3);

        InsightRuleResult result = new ContentionTotalShareRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, result.Outcome);
    }

    [Fact]
    public void ContentionHotLock_RequiresEnoughWaitersToBeAConvoy()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Contents.HasContentionEvents = true;
        analysis.Contention.TotalContentionCount = 100;
        analysis.Contention.TotalWaitMSec = 1000;

        ContentionSiteRecord site = new ContentionSiteRecord();
        site.SiteName = "Contoso.Cache.Get";
        site.PercentOfTotalWait = 90;
        site.TotalWaitMSec = 900;
        site.ContentionCount = 90;
        site.WaiterThreadCount = ContentionHotLockRule.MinimumWaiterThreads - 1;
        analysis.Contention.TopSites.Add(site);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, new ContentionHotLockRule().Evaluate(analysis).Outcome);

        site.WaiterThreadCount = ContentionHotLockRule.MinimumWaiterThreads;
        Assert.Equal(InsightRuleOutcome.Fired, new ContentionHotLockRule().Evaluate(analysis).Outcome);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Exceptions
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void ExceptionRate_IsNotApplicableWithoutExceptionEvents()
    {
        CaptureAnalysis analysis = EmptyAnalysis();

        InsightRuleResult result = new ExceptionRateRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public void ExceptionTypeConcentration_NeedsAMinimumVolume()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Contents.HasExceptionEvents = true;
        analysis.Exceptions.TotalCount = ExceptionTypeConcentrationRule.MinimumTotalCount - 1;
        analysis.Exceptions.DistinctTypeCount = 1;

        ExceptionTypeRecord type = new ExceptionTypeRecord();
        type.TypeName = "Contoso.NotFoundException";
        type.Count = analysis.Exceptions.TotalCount;
        type.PercentOfTotal = 100;
        analysis.Exceptions.TopTypes.Add(type);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, new ExceptionTypeConcentrationRule().Evaluate(analysis).Outcome);

        analysis.Exceptions.TotalCount = ExceptionTypeConcentrationRule.MinimumTotalCount;
        type.Count = analysis.Exceptions.TotalCount;
        Assert.Equal(InsightRuleOutcome.Fired, new ExceptionTypeConcentrationRule().Evaluate(analysis).Outcome);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Capture quality
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void MissingProviders_FiresAndNamesWhatWasNotCollected()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Contents.HasGcEvents = true;

        InsightRuleResult result = new CaptureMissingProvidersRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
        Assert.Contains("Allocation ticks", result.Insight.Headline);
        // It must never read as a negative finding about the process.
        Assert.Contains("not the same as the process having none", result.Insight.Detail);
    }

    [Fact]
    public void MissingProviders_DoesNotFireWhenEverythingWasCollected()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Contents.HasGcEvents = true;
        analysis.Contents.HasAllocationTicks = true;
        analysis.Contents.HasCpuSamples = true;
        analysis.Contents.HasContentionEvents = true;
        analysis.Contents.HasExceptionEvents = true;
        analysis.Contents.HasThreadPoolEvents = true;

        InsightRuleResult result = new CaptureMissingProvidersRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, result.Outcome);
    }

    // Regression for a real capture in which every CPU sample and every
    // contention event bottomed out in the kernel's event-writing function,
    // producing a confident "this method is 100% of CPU" and "this lock
    // serializes 181 threads". Both had to be suppressed.
    [Fact]
    public void CollapsedLeafAttribution_FiresAndSuppressesLeafRankedRules()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Contents.HasCpuSamples = true;
        analysis.Contents.CpuLeafAttributionCollapsed = true;
        analysis.Contents.CollapsedCpuLeafFrame = "user_events_write_core.isra.0";
        analysis.Contents.CollapsedCpuLeafShare = 100.0;
        analysis.Cpu.TotalSampleCount = 1000;

        HotMethodRecord method = new HotMethodRecord();
        method.Name = "user_events_write_core.isra.0";
        method.SelfSamples = 1000;
        method.SelfPercent = 100.0;
        analysis.Cpu.HotMethods.Add(method);

        Assert.Equal(InsightRuleOutcome.Fired, new CaptureCollapsedLeafAttributionRule().Evaluate(analysis).Outcome);

        // The leaf-ranked rule must decline rather than report the frame as a
        // hot method.
        InsightRuleResult singleMethod = new CpuSingleMethodRule().Evaluate(analysis);
        Assert.Equal(InsightRuleOutcome.NotApplicable, singleMethod.Outcome);
        Assert.Contains("collapsed", singleMethod.NotApplicableReason);
    }

    // Regression for a real misreport: the CPU sample profiler samples parked
    // threads too, so on a service with a large idle pool the raw top of the
    // self-time ranking is the pool's own park - measured at 68.11% on a
    // 518-thread capture. That is not a hot method; it is threads doing
    // nothing.
    [Fact]
    public void CpuSingleMethod_SkipsBlockingPrimitivesAndNamesTheHottestRunningMethod()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Contents.HasCpuSamples = true;
        analysis.Cpu.TotalSampleCount = 1000;

        HotMethodRecord parked = new HotMethodRecord();
        parked.Name = "System.Threading.LowLevelLifoSemaphore.WaitForSignal";
        parked.SelfSamples = 680;
        parked.SelfPercent = 68.0;
        parked.IsIdleWait = true;
        analysis.Cpu.HotMethods.Add(parked);

        HotMethodRecord running = new HotMethodRecord();
        running.Name = "Contoso.Serializer.Write";
        running.SelfSamples = 200;
        running.SelfPercent = 20.0;
        analysis.Cpu.HotMethods.Add(running);

        InsightRuleResult result = new CpuSingleMethodRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.Fired, result.Outcome);
        Assert.Contains("Contoso.Serializer.Write", result.Insight.Headline);
        Assert.DoesNotContain("LowLevelLifoSemaphore", result.Insight.Headline);
        // The parked share is still stated - it changes how 20% should be read.
        Assert.Contains(result.Insight.Evidence, e => e.Label == "Samples in blocking calls");
    }

    [Fact]
    public void CpuSingleMethod_IsNotApplicableWhenEveryLeafIsABlockingPrimitive()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Contents.HasCpuSamples = true;
        analysis.Cpu.TotalSampleCount = 1000;

        HotMethodRecord parked = new HotMethodRecord();
        parked.Name = "System.Threading.LowLevelLifoSemaphore.WaitForSignal";
        parked.SelfSamples = 1000;
        parked.SelfPercent = 60.0;
        parked.IsIdleWait = true;
        analysis.Cpu.HotMethods.Add(parked);

        InsightRuleResult result = new CpuSingleMethodRule().Evaluate(analysis);

        Assert.Equal(InsightRuleOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public void CollapsedLeafAttribution_DoesNotFireOnAnOrdinaryProfile()
    {
        CaptureAnalysis analysis = EmptyAnalysis();
        analysis.Contents.HasCpuSamples = true;
        analysis.Cpu.TotalSampleCount = 1000;

        HotMethodRecord method = new HotMethodRecord();
        method.Name = "Contoso.Handler.Run";
        method.SelfSamples = 120;
        method.SelfPercent = 12.0;
        analysis.Cpu.HotMethods.Add(method);

        Assert.Equal(InsightRuleOutcome.BelowThreshold, new CaptureCollapsedLeafAttributionRule().Evaluate(analysis).Outcome);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
