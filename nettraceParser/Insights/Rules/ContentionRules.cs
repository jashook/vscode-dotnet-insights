////////////////////////////////////////////////////////////////////////////////
// Module: ContentionRules.cs
//
// Notes:
// Two rules over lock contention.
//
// CONTENTION IS THE ONE AREA WHERE A RAW COUNT IS ACTIVELY MISLEADING, and
// both rules here are built around that. 157 contention events sounds
// decisive until they total 8.3 milliseconds across a 300-second thread -
// that is ambient lock traffic, and it explains nothing. This is not a
// hypothetical: it is the exact measurement that forced
// ThreadActivityProfiler to require contention evidence to be MATERIAL rather
// than merely present. So every threshold here is stated against TIME, never
// against event count, and count appears only as supporting evidence.
//
// The two rules split "is there a lot of it" from "is it all one lock",
// because the answers point somewhere different: a high total with contention
// spread evenly across many locks is a design-level finding about how much
// shared state there is, while a high total concentrated on one lock is a
// single line of code.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights.Rules {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Analysis;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

internal static class ContentionRuleGate
{
    public const string NoContentionEvents = "this capture recorded no lock contention events (the Contention keyword was not collected, or nothing contended)";

    public static bool HasContention(CaptureAnalysis analysis)
    {
        return analysis.Contents.HasContentionEvents && analysis.Contention.TotalContentionCount > 0;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ContentionTotalShareRule : InsightRule
{
    public const double WarningPercent = 5.0;
    public const double CriticalPercent = 20.0;

    public override string Id => "contention/total-share";

    public override string Category => "Contention";

    public override string Threshold =>
        "time with at least one thread blocked on a contended lock >= " + FormatPercent(WarningPercent)
        + " of capture duration (" + FormatPercent(CriticalPercent) + " is critical)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!ContentionRuleGate.HasContention(analysis))
        {
            return this.NotApplicable(ContentionRuleGate.NoContentionEvents);
        }

        if (!analysis.TimeBreakdown.HasCaptureDuration)
        {
            return this.NotApplicable("this capture has no measurable duration to compare contention against");
        }

        double contentionPercent = analysis.TimeBreakdown.ContentionPercent;

        string measured = FormatPercent(contentionPercent) + " of capture had a thread blocked on a lock ("
            + FormatDuration(analysis.Contention.TotalWaitMSec) + " total wait across "
            + FormatCount(analysis.Contention.TotalContentionCount) + " events)";

        if (contentionPercent < WarningPercent)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Capture time with a blocked thread", FormatPercent(contentionPercent), contentionPercent));
        evidence.Add(new InsightEvidence("Total lock wait", FormatDuration(analysis.Contention.TotalWaitMSec), analysis.Contention.TotalWaitMSec));
        evidence.Add(new InsightEvidence("Average threads blocked", analysis.TimeBreakdown.AverageThreadsBlocked.ToString("F2"), analysis.TimeBreakdown.AverageThreadsBlocked));
        evidence.Add(new InsightEvidence("Contention events", FormatCount(analysis.Contention.TotalContentionCount), analysis.Contention.TotalContentionCount));
        evidence.Add(new InsightEvidence("Distinct sites", FormatCount(analysis.Contention.DistinctSiteCount), analysis.Contention.DistinctSiteCount));

        if (analysis.Contention.TopSites.Count > 0)
        {
            ContentionSiteRecord topSite = analysis.Contention.TopSites[0];
            evidence.Add(new InsightEvidence("Worst site", topSite.SiteName + " (" + FormatPercent(topSite.PercentOfTotalWait) + " of wait)", topSite.PercentOfTotalWait));
        }

        return this.Fire(
            contentionPercent >= CriticalPercent ? InsightSeverity.Critical : InsightSeverity.Warning,
            InsightConfidence.High,
            FormatPercent(contentionPercent) + " of the capture had at least one thread blocked on a contended lock",
            "This is wall-clock time during which a thread was waiting to enter a lock somebody else held. On "
            + "average " + analysis.TimeBreakdown.AverageThreadsBlocked.ToString("F2") + " threads were blocked at "
            + "any instant. Note this is measured in TIME, not in event count - a large number of contentions that "
            + "each resolve in microseconds is ambient and harmless, and would not appear here.",
            contentionPercent / WarningPercent,
            evidence,
            new List<string>
            {
                "Open the Contention view and work down the ranked sites by total wait.",
                "Look for locks held across I/O - those turn a short critical section into a long one."
            },
            new List<InsightLink> { new InsightLink("contention", "Contention view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A convoy: one lock, many waiters. The waiter count is part of the threshold
// rather than just evidence, because a single lock accounting for most of the
// wait time is unremarkable when only two threads ever touch it (that is just
// what "the only lock in the process" looks like) and is a serialization
// bottleneck when a dozen do.
public sealed class ContentionHotLockRule : InsightRule
{
    public const double WaitShareThreshold = 40.0;
    public const int MinimumWaiterThreads = 3;
    public const double MinimumTotalWaitMSec = 100.0;

    public override string Id => "contention/hot-lock";

    public override string Category => "Contention";

    public override string Threshold =>
        "one site accounts for >= " + FormatPercent(WaitShareThreshold) + " of all lock wait, with >= "
        + MinimumWaiterThreads + " distinct waiting threads and >= " + FormatDuration(MinimumTotalWaitMSec) + " of wait on it";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!ContentionRuleGate.HasContention(analysis))
        {
            return this.NotApplicable(ContentionRuleGate.NoContentionEvents);
        }

        if (analysis.Contention.TopSites.Count == 0)
        {
            return this.NotApplicable("no contention site could be resolved to a call site in this capture");
        }

        // One site at ~100% of wait is not a convoy - see
        // capture/collapsed-leaf-attribution, which reports why.
        if (analysis.Contents.ContentionLeafAttributionCollapsed)
        {
            return this.NotApplicable("leaf attribution has collapsed onto " + analysis.Contents.CollapsedContentionLeafFrame + "; see capture/collapsed-leaf-attribution");
        }

        ContentionSiteRecord topSite = analysis.Contention.TopSites[0];

        string measured = "top site " + topSite.SiteName + " at " + FormatPercent(topSite.PercentOfTotalWait)
            + " of wait, " + FormatCount(topSite.WaiterThreadCount) + " waiting threads, "
            + FormatDuration(topSite.TotalWaitMSec);

        if (topSite.PercentOfTotalWait < WaitShareThreshold
            || topSite.WaiterThreadCount < MinimumWaiterThreads
            || topSite.TotalWaitMSec < MinimumTotalWaitMSec)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Site", topSite.SiteName, 0));
        evidence.Add(new InsightEvidence("Share of all lock wait", FormatPercent(topSite.PercentOfTotalWait), topSite.PercentOfTotalWait));
        evidence.Add(new InsightEvidence("Total wait here", FormatDuration(topSite.TotalWaitMSec), topSite.TotalWaitMSec));
        evidence.Add(new InsightEvidence("Distinct waiting threads", FormatCount(topSite.WaiterThreadCount), topSite.WaiterThreadCount));
        evidence.Add(new InsightEvidence("Contentions", FormatCount(topSite.ContentionCount), topSite.ContentionCount));
        evidence.Add(new InsightEvidence("Average wait", FormatDuration(topSite.AverageWaitMSec), topSite.AverageWaitMSec));
        evidence.Add(new InsightEvidence("Longest wait", FormatDuration(topSite.MaxWaitMSec), topSite.MaxWaitMSec));

        return this.Fire(
            InsightSeverity.Warning,
            InsightConfidence.High,
            topSite.SiteName + " accounts for " + FormatPercent(topSite.PercentOfTotalWait) + " of all lock wait, across " + FormatCount(topSite.WaiterThreadCount) + " threads",
            "One lock is serializing " + FormatCount(topSite.WaiterThreadCount) + " threads. Each of them waited "
            + FormatDuration(topSite.AverageWaitMSec) + " on average and the worst wait was "
            + FormatDuration(topSite.MaxWaitMSec) + ". A single site carrying this much of the total, with this "
            + "many distinct threads queued behind it, is a throughput ceiling: adding threads or cores will make "
            + "the queue longer rather than the work faster.",
            topSite.PercentOfTotalWait / WaitShareThreshold,
            evidence,
            new List<string>
            {
                "Expand " + topSite.SiteName + " in the Contention view to see the call paths reaching it.",
                "Consider narrowing the critical section, or replacing the lock with a concurrent collection or per-thread state."
            },
            new List<InsightLink> { new InsightLink("contention", "Contention view: " + topSite.SiteName, "site", topSite.SiteName) },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights.Rules)
