////////////////////////////////////////////////////////////////////////////////
// Module: AllocationRules.cs
//
// Notes:
// Three rules over the allocation-tick stream.
//
// EVERY BYTE FIGURE HERE IS SAMPLED. The runtime emits one GCAllocationTick
// per roughly 100KB allocated, so the totals are an estimate of allocation
// volume, not a measurement of it. Each headline says "sampled" for that
// reason - a reader who takes these as exact will compare them against a
// memory profiler's numbers and conclude, wrongly, that one of the two tools
// is broken.
//
// The sampling is also why there is no "allocation is small, all clear"
// finding anywhere in this file. A low sampled total is consistent with low
// allocation and with a capture that ran briefly; only the high side carries
// information.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights.Rules {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Analysis;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

internal static class AllocationRuleGate
{
    public const string NoAllocationTicks = "this capture recorded no allocation ticks (the GCAllocationTick keyword was not collected, or nothing allocated)";

    public static bool HasAllocation(CaptureAnalysis analysis)
    {
        return analysis.Contents.HasAllocationTicks && analysis.Allocation.TotalTickCount > 0;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class AllocationRateRule : InsightRule
{
    // Calibrated against the 20 corpus captures that carry allocation ticks:
    // p25 447 MB/s, median 618, p75 707, max 774. A 200 MB/s warning fired on
    // 51% of the corpus - it was measuring what a busy .NET service allocates,
    // not a problem. Info starts below p25 because the RATE is useful context
    // on any capture; Warning sits above p75 so it marks a capture that is
    // heavy for this population rather than heavy in the abstract.
    public const double InfoMegabytesPerSecond = 250.0;
    public const double WarningMegabytesPerSecond = 750.0;
    public const double CriticalMegabytesPerSecond = 2000.0;

    public override string Id => "alloc/rate";

    public override string Category => "Allocation";

    public override string Threshold =>
        "sampled allocation rate >= " + InfoMegabytesPerSecond.ToString("F0") + " MB/s ("
        + WarningMegabytesPerSecond.ToString("F0") + " MB/s is a warning, "
        + CriticalMegabytesPerSecond.ToString("F0") + " MB/s is critical)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!AllocationRuleGate.HasAllocation(analysis))
        {
            return this.NotApplicable(AllocationRuleGate.NoAllocationTicks);
        }

        if (!analysis.HasCaptureDuration || analysis.CaptureDurationSeconds <= 0)
        {
            return this.NotApplicable("this capture has no measurable duration to compute an allocation rate over");
        }

        double bytesPerSecond = analysis.Allocation.SampledBytesPerSecond(analysis.CaptureDurationSeconds);
        double megabytesPerSecond = bytesPerSecond / (1024.0 * 1024.0);

        string measured = megabytesPerSecond.ToString("F1") + " MB/s sampled ("
            + FormatBytes(analysis.Allocation.TotalSampledBytes) + " over " + FormatDuration(analysis.CaptureDurationMSec) + ")";

        if (megabytesPerSecond < InfoMegabytesPerSecond)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Sampled allocation rate", megabytesPerSecond.ToString("F1") + " MB/s", megabytesPerSecond));
        evidence.Add(new InsightEvidence("Sampled bytes", FormatBytes(analysis.Allocation.TotalSampledBytes), analysis.Allocation.TotalSampledBytes));
        evidence.Add(new InsightEvidence("Allocation ticks", FormatCount(analysis.Allocation.TotalTickCount), analysis.Allocation.TotalTickCount));
        evidence.Add(new InsightEvidence("Distinct types", FormatCount(analysis.Allocation.DistinctTypeCount), analysis.Allocation.DistinctTypeCount));

        InsightSeverity severity = InsightSeverity.Info;

        if (megabytesPerSecond >= CriticalMegabytesPerSecond)
        {
            severity = InsightSeverity.Critical;
        }
        else if (megabytesPerSecond >= WarningMegabytesPerSecond)
        {
            severity = InsightSeverity.Warning;
        }

        return this.Fire(
            severity,
            InsightConfidence.High,
            "Allocating " + megabytesPerSecond.ToString("F1") + " MB/s (sampled)",
            "Allocation rate is what sets collection frequency: the gen0 budget is a fixed number of bytes, so "
            + "twice the allocation rate is twice as many gen0 collections. Most of this is usually a small number "
            + "of types on a small number of call paths. Note that this figure is sampled - the runtime emits one "
            + "tick per ~100KB allocated - so treat it as an estimate of the order of magnitude.",
            megabytesPerSecond / InfoMegabytesPerSecond,
            evidence,
            new List<string>
            {
                "Open Heap Contents and expand the top types to see which call paths allocate them.",
                "Look for per-request allocations that could be pooled, reused, or replaced with a Span."
            },
            new List<InsightLink> { new InsightLink("heapContents", "Heap Contents view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// One type dominating allocation is good news, not bad: it means there is a
// single place to look. Reported at Info unless it is extreme, because a
// concentrated allocation profile is not itself a defect - a JSON service
// really does allocate mostly strings and byte arrays.
public sealed class AllocationTypeConcentrationRule : InsightRule
{
    public const double ShareThreshold = 40.0;
    public const double WarningShareThreshold = 65.0;

    public override string Id => "alloc/type-concentration";

    public override string Category => "Allocation";

    public override string Threshold => "the top allocated type accounts for >= " + FormatPercent(ShareThreshold) + " of sampled bytes";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!AllocationRuleGate.HasAllocation(analysis))
        {
            return this.NotApplicable(AllocationRuleGate.NoAllocationTicks);
        }

        if (analysis.Allocation.TopTypes.Count == 0)
        {
            return this.NotApplicable("no allocated types were resolved in this capture");
        }

        AllocatedTypeRecord topType = analysis.Allocation.TopTypes[0];

        string measured = "top type " + topType.TypeName + " at " + FormatPercent(topType.PercentOfTotalBytes)
            + " of sampled bytes";

        if (topType.PercentOfTotalBytes < ShareThreshold)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Type", topType.TypeName, 0));
        evidence.Add(new InsightEvidence("Share of sampled bytes", FormatPercent(topType.PercentOfTotalBytes), topType.PercentOfTotalBytes));
        evidence.Add(new InsightEvidence("Sampled bytes", FormatBytes(topType.TotalBytes), topType.TotalBytes));
        evidence.Add(new InsightEvidence("Ticks", FormatCount(topType.TickCount), topType.TickCount));

        if (analysis.Allocation.TopTypes.Count > 1)
        {
            AllocatedTypeRecord runnerUp = analysis.Allocation.TopTypes[1];
            evidence.Add(new InsightEvidence("Next type", runnerUp.TypeName + " at " + FormatPercent(runnerUp.PercentOfTotalBytes), runnerUp.PercentOfTotalBytes));
        }

        return this.Fire(
            topType.PercentOfTotalBytes >= WarningShareThreshold ? InsightSeverity.Warning : InsightSeverity.Info,
            InsightConfidence.High,
            topType.TypeName + " is " + FormatPercent(topType.PercentOfTotalBytes) + " of all sampled allocation",
            "Allocation is concentrated in a single type, which makes this the highest-leverage thing to change: "
            + "one call path is responsible for most of the collection pressure in this process. Expanding this type "
            + "in the Heap Contents view shows the call paths that allocate it, ranked.",
            topType.PercentOfTotalBytes / ShareThreshold,
            evidence,
            new List<string> { "Expand " + topType.TypeName + " in Heap Contents to see which call paths allocate it." },
            new List<InsightLink> { new InsightLink("heapContents", "Heap Contents view: " + topType.TypeName, "type", topType.TypeName) },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class AllocationLargeObjectRule : InsightRule
{
    public const double ByteShareThreshold = 20.0;

    public override string Id => "alloc/loh";

    public override string Category => "Allocation";

    public override string Threshold => "large-object allocations account for >= " + FormatPercent(ByteShareThreshold) + " of sampled bytes";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!AllocationRuleGate.HasAllocation(analysis))
        {
            return this.NotApplicable(AllocationRuleGate.NoAllocationTicks);
        }

        double lohShare = analysis.Allocation.LargeObjectByteShare * 100.0;

        string measured = FormatPercent(lohShare) + " of sampled bytes are large-object allocations ("
            + FormatCount(analysis.Allocation.LargeObjectTickCount) + " ticks)";

        if (lohShare < ByteShareThreshold)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Large-object share", FormatPercent(lohShare), lohShare));
        evidence.Add(new InsightEvidence("Large-object sampled bytes", FormatBytes(analysis.Allocation.LargeObjectSampledBytes), analysis.Allocation.LargeObjectSampledBytes));
        evidence.Add(new InsightEvidence("Large-object ticks", FormatCount(analysis.Allocation.LargeObjectTickCount), analysis.Allocation.LargeObjectTickCount));

        // Name the biggest offender where one stands out - "20% of your bytes
        // are large" is a fact, "and most of them are byte[]" is a lead.
        AllocatedTypeRecord topLargeType = null;

        for (int typeIndex = 0; typeIndex < analysis.Allocation.TopTypes.Count; ++typeIndex)
        {
            AllocatedTypeRecord candidate = analysis.Allocation.TopTypes[typeIndex];

            if (candidate.LargeCount > 0 && (topLargeType == null || candidate.LargeCount > topLargeType.LargeCount))
            {
                topLargeType = candidate;
            }
        }

        if (topLargeType != null)
        {
            evidence.Add(new InsightEvidence("Most large allocations", topLargeType.TypeName + " (" + FormatCount(topLargeType.LargeCount) + " ticks)", topLargeType.LargeCount));
        }

        return this.Fire(
            InsightSeverity.Warning,
            InsightConfidence.High,
            FormatPercent(lohShare) + " of sampled allocation goes to the large object heap",
            "Objects over 85,000 bytes are allocated on the large object heap, which is collected only with gen2 "
            + "and is not compacted by default. A steady stream of them therefore forces full collections and "
            + "fragments the heap at the same time. In practice this is almost always buffers, arrays, or large "
            + "strings that could be pooled or written in chunks.",
            lohShare / ByteShareThreshold,
            evidence,
            new List<string>
            {
                "Pool large buffers with ArrayPool rather than allocating per operation.",
                "Where a large array is built up incrementally, consider whether it can be streamed instead."
            },
            new List<InsightLink> { new InsightLink("heapContents", "Heap Contents view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights.Rules)
