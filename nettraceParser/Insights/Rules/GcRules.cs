////////////////////////////////////////////////////////////////////////////////
// Module: GcRules.cs
//
// Notes:
// The eight GC rules. This is the area where the tool has the most to say,
// because the GC projection already does the hard correlation work - pause
// windows measured from GCSuspendEEBegin to GCRestartEEEnd rather than
// GCStart to GCEnd, per-heap detail, background versus blocking - and these
// rules only have to read it.
//
// TWO THINGS THAT WOULD BE WRONG IF DONE THE OBVIOUS WAY, both encoded here:
//
// 1. A BACKGROUND gen2 is not a blocking gen2. Its pause is a fraction of its
//    span (see CLAUDE.md on PauseDurationMSec's true window - a background
//    GC's own pause is seeded from its initiating suspend gap, microseconds,
//    not its whole concurrent mark phase). Counting the two together makes a
//    healthy server look like a stalling one, so gc/gen2-pause-concentration
//    counts only the blocking ones.
//
// 2. A POSITIVE HEAP-SIZE SLOPE IS NOT A LEAK. A process that starts cold,
//    fills its caches and levels off has one, and so does a process that
//    leaks. What separates them is whether a straight line actually fits, so
//    gc/heap-growth gates on R^2 as well as on slope, and additionally
//    requires the growth to be material in both relative and absolute terms -
//    a 40MB heap growing 30% is 12MB and is nobody's incident.
//
// THRESHOLDS. The values below are the starting set, chosen to be defensible
// rather than measured, and are due for calibration against the capture
// corpus (see the plan's calibration step and CLAUDE.md's own convention of
// recording measured thresholds with the numbers behind them). Where a
// threshold is genuinely not a judgement call it says so - gc/induced fires
// on a single induced collection because the runtime's own reason code is
// telling us somebody called GC.Collect, and there is no honest threshold
// above one.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights.Rules {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Analysis;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Shared gate: nearly every rule here needs GC events to exist at all, and
// the decline has to name the missing data rather than imply a clean result.
internal static class GcRuleGate
{
    public const string NoGcEvents = "this capture recorded no GC events";

    public static bool HasGc(CaptureAnalysis analysis)
    {
        return analysis.Contents.HasGcEvents && analysis.Gc.Count > 0;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class GcPauseShareRule : InsightRule
{
    public const double WarningPercent = 5.0;
    public const double CriticalPercent = 15.0;

    public override string Id => "gc/pause-share";

    public override string Category => "GC";

    public override string Threshold =>
        "total GC pause >= " + FormatPercent(WarningPercent) + " of capture duration ("
        + FormatPercent(CriticalPercent) + " is critical)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!GcRuleGate.HasGc(analysis))
        {
            return this.NotApplicable(GcRuleGate.NoGcEvents);
        }

        if (!analysis.TimeBreakdown.HasCaptureDuration)
        {
            return this.NotApplicable("this capture has no measurable duration to compare GC pause against");
        }

        double gcPercent = analysis.TimeBreakdown.GcPercent;
        double gcPauseMSec = analysis.TimeBreakdown.GcPauseMSec;

        string measured = FormatPercent(gcPercent) + " of capture in GC pause ("
            + FormatDuration(gcPauseMSec) + " of " + FormatDuration(analysis.CaptureDurationMSec) + ")";

        if (gcPercent < WarningPercent)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("GC pause share", FormatPercent(gcPercent), gcPercent));
        evidence.Add(new InsightEvidence("Total GC pause", FormatDuration(gcPauseMSec), gcPauseMSec));
        evidence.Add(new InsightEvidence("Capture duration", FormatDuration(analysis.CaptureDurationMSec), analysis.CaptureDurationMSec));
        evidence.Add(new InsightEvidence("Collections", FormatCount(analysis.Gc.Count), analysis.Gc.Count));

        return this.Fire(
            gcPercent >= CriticalPercent ? InsightSeverity.Critical : InsightSeverity.Warning,
            InsightConfidence.High,
            FormatPercent(gcPercent) + " of wall-clock time is GC pause (" + FormatDuration(gcPauseMSec) + " of " + FormatDuration(analysis.CaptureDurationMSec) + ")",
            "This is stop-the-world time: every managed thread in the process was suspended for it. The window "
            + "measured is from the runtime being asked to suspend threads through to threads running again, so "
            + "it includes the cost of actually stopping them, which a GCStart-to-GCEnd figure would omit. "
            + "Reducing it means allocating less or promoting less, not tuning the collector.",
            gcPercent / WarningPercent,
            evidence,
            new List<string>
            {
                "Check the Heap Contents view for what is being allocated most.",
                "Check whether the pause is concentrated in a few blocking gen2 collections or spread across many gen0s."
            },
            new List<InsightLink> { new InsightLink("gc", "GC view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class GcMaxPauseRule : InsightRule
{
    public const double WarningMSec = 200.0;
    public const double CriticalMSec = 1000.0;

    public override string Id => "gc/max-pause";

    public override string Category => "GC";

    public override string Threshold =>
        "longest single GC pause >= " + FormatDuration(WarningMSec) + " (" + FormatDuration(CriticalMSec) + " is critical)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!GcRuleGate.HasGc(analysis))
        {
            return this.NotApplicable(GcRuleGate.NoGcEvents);
        }

        double maxPause = analysis.Gc.MaxPauseMSec;
        string measured = "longest pause " + FormatDuration(maxPause)
            + (analysis.Gc.MaxPauseGcId >= 0 ? " (GC #" + analysis.Gc.MaxPauseGcId + ")" : "");

        if (maxPause < WarningMSec)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Longest pause", FormatDuration(maxPause), maxPause));
        evidence.Add(new InsightEvidence("GC", "#" + analysis.Gc.MaxPauseGcId, analysis.Gc.MaxPauseGcId));
        evidence.Add(new InsightEvidence("Generation", "gen" + analysis.Gc.MaxPauseGeneration, analysis.Gc.MaxPauseGeneration));
        evidence.Add(new InsightEvidence("Reason", analysis.Gc.MaxPauseReason, 0));

        return this.Fire(
            maxPause >= CriticalMSec ? InsightSeverity.Critical : InsightSeverity.Warning,
            InsightConfidence.High,
            "Longest single GC pause was " + FormatDuration(maxPause) + " (GC #" + analysis.Gc.MaxPauseGcId + ", gen" + analysis.Gc.MaxPauseGeneration + ", " + analysis.Gc.MaxPauseReason + ")",
            "One collection held every managed thread for this long. A tail-latency complaint that nobody can "
            + "reproduce under average load often turns out to be a single pause of this shape - the average "
            + "pause figure will not show it.",
            maxPause / WarningMSec,
            evidence,
            new List<string> { "Open GC #" + analysis.Gc.MaxPauseGcId + " in the GC view to see its heap sizes and per-heap detail." },
            new List<InsightLink> { new InsightLink("gc", "GC view: GC #" + analysis.Gc.MaxPauseGcId, "gcId", analysis.Gc.MaxPauseGcId.ToString()) },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Fires on ONE induced collection, and that is not a threshold that needs
// calibrating. The runtime's own reason code is reporting that application
// code called GC.Collect; there is no number of those that is fine and one
// more that is not.
public sealed class GcInducedRule : InsightRule
{
    public override string Id => "gc/induced";

    public override string Category => "GC";

    public override string Threshold => "any collection with an Induced reason code (GC.Collect was called)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!GcRuleGate.HasGc(analysis))
        {
            return this.NotApplicable(GcRuleGate.NoGcEvents);
        }

        int inducedCount = analysis.Gc.InducedCount;
        string measured = FormatCount(inducedCount) + " induced of " + FormatCount(analysis.Gc.Count) + " collections";

        if (inducedCount == 0)
        {
            return this.BelowThreshold(measured);
        }

        double inducedShare = inducedCount * 100.0 / analysis.Gc.Count;

        double inducedPauseMSec = 0;

        for (int gcIndex = 0; gcIndex < analysis.Gc.Gcs.Count; ++gcIndex)
        {
            if (analysis.Gc.Gcs[gcIndex].IsInduced)
            {
                inducedPauseMSec += analysis.Gc.Gcs[gcIndex].PauseDurationMSec;
            }
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Induced collections", FormatCount(inducedCount), inducedCount));
        evidence.Add(new InsightEvidence("Share of all collections", FormatPercent(inducedShare), inducedShare));
        evidence.Add(new InsightEvidence("Pause in induced GCs", FormatDuration(inducedPauseMSec), inducedPauseMSec));
        evidence.Add(new InsightEvidence("First induced GC", "#" + analysis.Gc.FirstInducedGcId, analysis.Gc.FirstInducedGcId));

        return this.Fire(
            inducedShare >= 25.0 ? InsightSeverity.Warning : InsightSeverity.Info,
            InsightConfidence.High,
            FormatCount(inducedCount) + " collections were induced by application code calling GC.Collect",
            "The runtime attributed these collections to an explicit GC.Collect call rather than to allocation "
            + "pressure. Induced collections are usually full, blocking, and timed by whatever called them rather "
            + "than by what the heap needed - they cost " + FormatDuration(inducedPauseMSec) + " of pause here. "
            + "Common sources are diagnostic code, a benchmark harness left in, or a library forcing a collection "
            + "after a large operation.",
            1.0 + inducedShare / 25.0,
            evidence,
            new List<string>
            {
                "Search the application and its dependencies for GC.Collect calls.",
                "If a library is responsible and the call cannot be removed, consider whether GCSettings.LatencyMode can limit the damage."
            },
            new List<InsightLink> { new InsightLink("gc", "GC view: GC #" + analysis.Gc.FirstInducedGcId, "gcId", analysis.Gc.FirstInducedGcId.ToString()) },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class GcGen2PauseConcentrationRule : InsightRule
{
    // The count gate was 3 and, on the corpus, blocked three real captures
    // sitting at 80-85% concentration across 2 blocking gen2 collections while
    // its purpose was to exclude tiny captures whose single GC is trivially
    // 100% of their own pause. A count cannot tell those apart; an absolute
    // pause floor can, so the count drops to 2 and the floor does the work.
    // Measured: every capture the old gate wrongly excluded carries far more
    // than 500ms of total pause, and every trivially-100% one carries a few
    // milliseconds.
    public const int MinimumBlockingGen2Count = 2;
    public const double MinimumTotalPauseMSec = 500.0;
    public const double PauseShareThreshold = 50.0;

    public override string Id => "gc/gen2-pause-concentration";

    public override string Category => "GC";

    public override string Threshold =>
        "at least " + MinimumBlockingGen2Count + " BLOCKING gen2 collections carrying >= "
        + FormatPercent(PauseShareThreshold) + " of at least " + FormatDuration(MinimumTotalPauseMSec)
        + " of total pause time (background gen2s excluded)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!GcRuleGate.HasGc(analysis))
        {
            return this.NotApplicable(GcRuleGate.NoGcEvents);
        }

        if (analysis.Gc.TotalPauseMSec <= 0)
        {
            return this.NotApplicable("no GC in this capture reported a pause duration");
        }

        int blockingGen2Count = analysis.Gc.BlockingGen2Count;
        double blockingGen2Pause = analysis.Gc.BlockingGen2PauseMSec;
        double pauseShare = blockingGen2Pause * 100.0 / analysis.Gc.TotalPauseMSec;

        string measured = FormatCount(blockingGen2Count) + " blocking gen2 GCs carrying "
            + FormatPercent(pauseShare) + " of " + FormatDuration(analysis.Gc.TotalPauseMSec) + " total pause";

        if (blockingGen2Count < MinimumBlockingGen2Count
            || pauseShare < PauseShareThreshold
            || analysis.Gc.TotalPauseMSec < MinimumTotalPauseMSec)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Blocking gen2 collections", FormatCount(blockingGen2Count), blockingGen2Count));
        evidence.Add(new InsightEvidence("Pause in blocking gen2", FormatDuration(blockingGen2Pause), blockingGen2Pause));
        evidence.Add(new InsightEvidence("Total pause", FormatDuration(analysis.Gc.TotalPauseMSec), analysis.Gc.TotalPauseMSec));
        evidence.Add(new InsightEvidence("Share of total pause", FormatPercent(pauseShare), pauseShare));
        evidence.Add(new InsightEvidence("Background gen2 collections", FormatCount(analysis.Gc.BackgroundGen2Count), analysis.Gc.BackgroundGen2Count));

        return this.Fire(
            InsightSeverity.Warning,
            InsightConfidence.High,
            FormatCount(blockingGen2Count) + " blocking gen2 collections carry " + FormatPercent(pauseShare) + " of all pause time",
            "Most of this process's stop-the-world time is concentrated in a small number of full, blocking "
            + "collections rather than spread across many cheap gen0s. Background gen2 collections are counted "
            + "separately and excluded here - their concurrent mark phase is not pause. Objects surviving into "
            + "gen2 are what drives this: something is living long enough to be promoted twice, then dying.",
            pauseShare / PauseShareThreshold,
            evidence,
            new List<string>
            {
                "Look at promotion into gen2 across the GC view's detailed table.",
                "Check Heap Contents for types whose lifetime spans more than one collection."
            },
            new List<InsightLink> { new InsightLink("gc", "GC view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class GcHeapGrowthRule : InsightRule
{
    // A straight line has to actually fit before a slope means anything - see
    // this file's header.
    public const double MinimumRSquared = 0.70;
    public const double MinimumRelativeGrowth = 0.30;
    public const long MinimumAbsoluteGrowthBytes = 100L * 1024 * 1024;

    public override string Id => "gc/heap-growth";

    public override string Category => "GC";

    public override string Threshold =>
        "heap size trends upward with R^2 >= " + MinimumRSquared.ToString("F2")
        + ", growing >= " + FormatPercent(MinimumRelativeGrowth * 100.0)
        + " and >= " + FormatBytes(MinimumAbsoluteGrowthBytes) + " across the capture";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!GcRuleGate.HasGc(analysis))
        {
            return this.NotApplicable(GcRuleGate.NoGcEvents);
        }

        if (!analysis.Gc.HasHeapGrowthFit)
        {
            return this.NotApplicable("too few GCs reported heap sizes to fit a growth trend");
        }

        long growthBytes = analysis.Gc.LastHeapSizeBytes - analysis.Gc.FirstHeapSizeBytes;
        double relativeGrowth = analysis.Gc.FirstHeapSizeBytes > 0
            ? (double)growthBytes / analysis.Gc.FirstHeapSizeBytes
            : 0;

        string measured = "heap " + FormatBytes(analysis.Gc.FirstHeapSizeBytes) + " -> " + FormatBytes(analysis.Gc.LastHeapSizeBytes)
            + " (" + FormatRate(analysis.Gc.HeapGrowthBytesPerSecond / (1024.0 * 1024.0), "MB")
            + ", R^2 " + analysis.Gc.HeapGrowthRSquared.ToString("F2") + ")";

        bool fits = analysis.Gc.HeapGrowthRSquared >= MinimumRSquared;
        bool materialRelative = relativeGrowth >= MinimumRelativeGrowth;
        bool materialAbsolute = growthBytes >= MinimumAbsoluteGrowthBytes;

        if (analysis.Gc.HeapGrowthBytesPerSecond <= 0 || !fits || !materialRelative || !materialAbsolute)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Heap at first GC", FormatBytes(analysis.Gc.FirstHeapSizeBytes), analysis.Gc.FirstHeapSizeBytes));
        evidence.Add(new InsightEvidence("Heap at last GC", FormatBytes(analysis.Gc.LastHeapSizeBytes), analysis.Gc.LastHeapSizeBytes));
        evidence.Add(new InsightEvidence("Growth", FormatBytes(growthBytes) + " (" + FormatPercent(relativeGrowth * 100.0) + ")", growthBytes));
        evidence.Add(new InsightEvidence("Trend", FormatRate(analysis.Gc.HeapGrowthBytesPerSecond / (1024.0 * 1024.0), "MB"), analysis.Gc.HeapGrowthBytesPerSecond));
        evidence.Add(new InsightEvidence("Fit quality (R^2)", analysis.Gc.HeapGrowthRSquared.ToString("F2"), analysis.Gc.HeapGrowthRSquared));

        return this.Fire(
            InsightSeverity.Warning,
            InsightConfidence.Medium,
            "Heap grew steadily from " + FormatBytes(analysis.Gc.FirstHeapSizeBytes) + " to " + FormatBytes(analysis.Gc.LastHeapSizeBytes) + " across the capture",
            "Heap size after collection trends upward and a straight line fits it well (R^2 "
            + analysis.Gc.HeapGrowthRSquared.ToString("F2") + "), which is the shape a leak makes. It is also the "
            + "shape a cache filling up makes, and a capture that begins at process start will show it for warm-up "
            + "alone - this rule cannot tell those apart, which is why it is reported at medium confidence. What "
            + "settles it is whether the trend continues in a capture taken later, once the process is warm.",
            analysis.Gc.HeapGrowthRSquared / MinimumRSquared,
            evidence,
            new List<string>
            {
                "Take a second capture after the process has been warm for a while and see whether the trend persists.",
                "If it does, a .gcdump heap snapshot will name the types being retained and what is holding them."
            },
            new List<InsightLink> { new InsightLink("gc", "GC view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Server GC gives each heap its own allocation budget and its own collection
// work. When one heap does much more promotion than its siblings, the
// collection is only as fast as that heap, and adding cores does not help.
// Only visible at all because per-heap detail is decoded and kept.
public sealed class GcServerHeapImbalanceRule : InsightRule
{
    // Coefficient of variation of promoted bytes across heaps. 0.5 means the
    // standard deviation is half the mean, which is a lot of skew for work
    // the collector intends to be symmetric.
    //
    // CONFIRMED, not merely chosen. The corpus splits cleanly in two with an
    // EMPTY BAND in between: 13 captures measure 0.00-0.25 and 17 measure
    // 1.28-1.73, and nothing at all lands between 0.25 and 1.28. A threshold
    // inside that gap cannot be moved anywhere within it and change a single
    // verdict, which is the strongest form this kind of number comes in - the
    // same property the thread-classification thresholds were chosen for.
    // It is left at 0.50 because it already sits in the gap.
    public const double ImbalanceThreshold = 0.50;

    public override string Id => "gc/server-heap-imbalance";

    public override string Category => "GC";

    public override string Threshold =>
        "Server GC, and at least one collection whose promoted bytes across heaps have a coefficient of variation >= "
        + ImbalanceThreshold.ToString("F2");

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!GcRuleGate.HasGc(analysis))
        {
            return this.NotApplicable(GcRuleGate.NoGcEvents);
        }

        if (!analysis.Gc.IsServerGc)
        {
            return this.NotApplicable("this process is not using Server GC (one heap), so there is nothing to balance");
        }

        if (analysis.Gc.WorstHeapImbalanceGcId < 0)
        {
            return this.NotApplicable("no collection in this capture decoded per-heap detail");
        }

        double imbalance = analysis.Gc.WorstHeapImbalance;
        string measured = "worst per-heap promotion spread " + imbalance.ToString("F2")
            + " (GC #" + analysis.Gc.WorstHeapImbalanceGcId + ", " + analysis.Gc.HeapCount + " heaps)";

        if (imbalance < ImbalanceThreshold)
        {
            return this.BelowThreshold(measured);
        }

        GcRecord worst = null;

        for (int gcIndex = 0; gcIndex < analysis.Gc.Gcs.Count; ++gcIndex)
        {
            if (analysis.Gc.Gcs[gcIndex].Id == analysis.Gc.WorstHeapImbalanceGcId)
            {
                worst = analysis.Gc.Gcs[gcIndex];
                break;
            }
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Heaps", FormatCount(analysis.Gc.HeapCount), analysis.Gc.HeapCount));
        evidence.Add(new InsightEvidence("Worst spread", imbalance.ToString("F2"), imbalance));
        evidence.Add(new InsightEvidence("Worst GC", "#" + analysis.Gc.WorstHeapImbalanceGcId, analysis.Gc.WorstHeapImbalanceGcId));

        if (worst != null)
        {
            evidence.Add(new InsightEvidence("Busiest heap promoted", FormatBytes(worst.HeapPromotedMax), worst.HeapPromotedMax));
            evidence.Add(new InsightEvidence("Quietest heap promoted", FormatBytes(worst.HeapPromotedMin), worst.HeapPromotedMin));
        }

        return this.Fire(
            InsightSeverity.Warning,
            InsightConfidence.Medium,
            "Server GC heaps are doing very uneven work (worst spread " + imbalance.ToString("F2") + " across " + analysis.Gc.HeapCount + " heaps)",
            "Server GC splits the heap per core and collects them in parallel, so a collection finishes when its "
            + "SLOWEST heap finishes. When promotion is concentrated on a few heaps the others idle through the "
            + "pause and the extra cores buy nothing. This usually comes from allocation being concentrated on a "
            + "few threads, since a thread allocates into the heap it is affinitized to.",
            imbalance / ImbalanceThreshold,
            evidence,
            new List<string>
            {
                "Check whether a small number of threads do most of the allocation.",
                "Compare per-heap columns for this GC in the GC view's detailed table."
            },
            new List<InsightLink> { new InsightLink("gc", "GC view: GC #" + analysis.Gc.WorstHeapImbalanceGcId, "gcId", analysis.Gc.WorstHeapImbalanceGcId.ToString()) },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class GcFragmentationRule : InsightRule
{
    public const double FragmentationShareThreshold = 30.0;

    public override string Id => "gc/fragmentation";

    public override string Category => "GC";

    public override string Threshold =>
        "free space after the last detailed collection >= " + FormatPercent(FragmentationShareThreshold) + " of gen2 + LOH size";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!GcRuleGate.HasGc(analysis))
        {
            return this.NotApplicable(GcRuleGate.NoGcEvents);
        }

        if (!analysis.Gc.HasFragmentation || analysis.Gc.FinalGen2AndLohBytes <= 0)
        {
            return this.NotApplicable("no collection in this capture reported per-heap free-space detail");
        }

        double fragmentationShare = analysis.Gc.FinalFragmentationBytes * 100.0 / analysis.Gc.FinalGen2AndLohBytes;

        string measured = FormatBytes(analysis.Gc.FinalFragmentationBytes) + " free of "
            + FormatBytes(analysis.Gc.FinalGen2AndLohBytes) + " gen2+LOH (" + FormatPercent(fragmentationShare) + ")";

        if (fragmentationShare < FragmentationShareThreshold)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Free space", FormatBytes(analysis.Gc.FinalFragmentationBytes), analysis.Gc.FinalFragmentationBytes));
        evidence.Add(new InsightEvidence("gen2 + LOH size", FormatBytes(analysis.Gc.FinalGen2AndLohBytes), analysis.Gc.FinalGen2AndLohBytes));
        evidence.Add(new InsightEvidence("Fragmentation share", FormatPercent(fragmentationShare), fragmentationShare));

        return this.Fire(
            InsightSeverity.Warning,
            InsightConfidence.Medium,
            FormatPercent(fragmentationShare) + " of the old-generation heap is free space between live objects",
            "The heap is holding " + FormatBytes(analysis.Gc.FinalFragmentationBytes) + " of committed memory that "
            + "contains nothing. Fragmentation on this scale usually means pinning - a pinned object cannot be moved, "
            + "so the collector has to work around it - or a large-object heap that is never compacted. The process "
            + "pays for this memory in RSS whether or not it ever uses it.",
            fragmentationShare / FragmentationShareThreshold,
            evidence,
            new List<string>
            {
                "Check the GC view's pinned object counts.",
                "A .gcdump heap snapshot will show whether the free space is on the LOH or in gen2."
            },
            new List<InsightLink> { new InsightLink("gc", "GC view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Keyed on the runtime's OWN reason code for each collection, not on how many
// bytes the allocation ticks say went to the LOH. Allocating on the LOH is
// ordinary; having it trigger collections is the finding, and only the reason
// code can say that happened.
public sealed class GcLohDrivenGen2Rule : InsightRule
{
    public const int MinimumCount = 3;
    public const double ShareThreshold = 10.0;

    public override string Id => "gc/loh-driven-gen2";

    public override string Category => "GC";

    public override string Threshold =>
        "at least " + MinimumCount + " collections with an AllocLarge or OutOfSpaceLOH reason code, and >= "
        + FormatPercent(ShareThreshold) + " of all collections";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!GcRuleGate.HasGc(analysis))
        {
            return this.NotApplicable(GcRuleGate.NoGcEvents);
        }

        int lohTriggered = analysis.Gc.LohTriggeredCount;
        double share = lohTriggered * 100.0 / analysis.Gc.Count;

        string measured = FormatCount(lohTriggered) + " of " + FormatCount(analysis.Gc.Count)
            + " collections triggered by large-object allocation (" + FormatPercent(share) + ")";

        if (lohTriggered < MinimumCount || share < ShareThreshold)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("LOH-triggered collections", FormatCount(lohTriggered), lohTriggered));
        evidence.Add(new InsightEvidence("Share of all collections", FormatPercent(share), share));
        evidence.Add(new InsightEvidence("Total collections", FormatCount(analysis.Gc.Count), analysis.Gc.Count));

        return this.Fire(
            InsightSeverity.Warning,
            InsightConfidence.High,
            FormatCount(lohTriggered) + " collections (" + FormatPercent(share) + ") were triggered by large-object allocation",
            "The runtime attributed these collections to running out of room for a large object - anything over "
            + "85,000 bytes, which in practice means arrays, buffers and large strings. Large-object allocation "
            + "goes straight to a generation that is collected with gen2, so a steady stream of big buffers forces "
            + "full collections regardless of how healthy the rest of the heap is.",
            share / ShareThreshold,
            evidence,
            new List<string>
            {
                "Check Heap Contents for large arrays and buffers.",
                "Pooling large buffers (ArrayPool) removes both the allocation and the collection it forces."
            },
            new List<InsightLink> { new InsightLink("heapContents", "Heap Contents view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights.Rules)
