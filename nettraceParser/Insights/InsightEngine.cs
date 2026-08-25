////////////////////////////////////////////////////////////////////////////////
// Module: InsightEngine.cs
//
// Notes:
// Runs every rule against one CaptureAnalysis and ranks what fired.
//
// THE REGISTRY IS THE FEATURE. Every rule appears in AllRules() exactly once,
// and the report carries a row for each of them whatever the outcome, so
// "which rules exist" and "which rules had an opinion about this capture" are
// both answerable from the output alone. InsightEngineTests asserts that
// every registered rule shows up in exactly one of the three buckets - a rule
// that silently evaluates to nothing is indistinguishable from a rule that
// was never registered, and the second of those is a bug the first would
// hide.
//
// A THROWING RULE MUST NOT TAKE THE REPORT WITH IT. Rules read a model built
// from real capture data, and real captures contain shapes nobody anticipated
// (a GC whose heap stats never arrived, a thread with no sampled stack, a
// capture whose duration is zero). One rule dividing by such a thing should
// cost that rule's row, not the other 24 findings. Failures are caught,
// reported in place as a distinct outcome, and the run continues.
//
// RANKING is severity, then confidence, then how far past its own threshold
// the rule fired, then id. The last term is not decoration: without it two
// findings that tie on the first three would order by whatever
// Dictionary/registration order happened to produce, and the same capture
// would rank differently between runs - the same determinism trap
// CpuProfileJsonExporter's own hot-method tie-break exists for.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using DotnetInsights.NetTrace.Analysis;
using DotnetInsights.NetTrace.Insights.Rules;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class InsightReport
{
    public CaptureAnalysis Analysis;

    // Everything that fired, most important first.
    public List<Insight> Insights = new List<Insight>();

    // Every rule's verdict, in registration order, whatever the outcome -
    // including the ones that fired. This is the audit list's source and the
    // completeness guarantee: Results.Count always equals the number of
    // registered rules.
    public List<InsightRuleResult> Results = new List<InsightRuleResult>();

    public int FiredCount;
    public int BelowThresholdCount;
    public int NotApplicableCount;
    public int FailedCount;

    public int CriticalCount;
    public int WarningCount;
    public int InfoCount;

    public InsightSeverity HighestSeverity;
    public bool HasAnyInsight => this.Insights.Count > 0;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class InsightEngine
{
    // Every rule, exactly once. Order here is the audit list's order and is
    // grouped by area for readability; it has no effect on the ranking of
    // findings, which is computed below.
    public static List<InsightRule> AllRules()
    {
        return new List<InsightRule>
        {
            // Capture quality runs first because it is what tells the reader
            // whether to trust the rest - a rule reporting "no allocation
            // pressure" on a capture that never recorded allocation is the
            // single worst thing this feature could do.
            new CaptureShortRule(),
            new CaptureMissingProvidersRule(),
            new CaptureDerivedSampleTypeRule(),
            new CaptureMostlyIdleRule(),
            new CaptureCollapsedLeafAttributionRule(),

            new GcPauseShareRule(),
            new GcMaxPauseRule(),
            new GcInducedRule(),
            new GcGen2PauseConcentrationRule(),
            new GcHeapGrowthRule(),
            new GcServerHeapImbalanceRule(),
            new GcFragmentationRule(),
            new GcLohDrivenGen2Rule(),

            new AllocationRateRule(),
            new AllocationTypeConcentrationRule(),
            new AllocationLargeObjectRule(),

            new CpuCategoryOutlierRule(),
            new CpuSingleMethodRule(),
            new CpuUnresolvedSymbolsRule(),

            new ThreadingPoolStarvationRule(),
            new ThreadingStallDrivenInjectionRule(),
            new ThreadingSyncOverAsyncRule(),

            new ContentionTotalShareRule(),
            new ContentionHotLockRule(),

            new ExceptionRateRule(),
            new ExceptionTypeConcentrationRule()
        };
    }

    public static InsightReport Run(CaptureAnalysis analysis)
    {
        return Run(analysis, AllRules());
    }

    // The rule list is a parameter so tests can drive the engine with one
    // rule, or with a deliberately throwing one, without reaching into the
    // registry.
    public static InsightReport Run(CaptureAnalysis analysis, List<InsightRule> rules)
    {
        InsightReport report = new InsightReport();
        report.Analysis = analysis;

        for (int ruleIndex = 0; ruleIndex < rules.Count; ++ruleIndex)
        {
            InsightRule rule = rules[ruleIndex];
            InsightRuleResult result;

            try
            {
                result = rule.Evaluate(analysis);
            }
            catch (Exception ruleException)
            {
                // Deliberately caught. See this file's header: one rule
                // meeting an unanticipated capture shape must cost its own
                // row, not the whole report. The failure is reported rather
                // than swallowed - a rule that silently never fires is a rule
                // nobody will notice is broken.
                result = new InsightRuleResult();
                result.RuleId = rule.Id;
                result.Category = rule.Category;
                result.Threshold = rule.Threshold;
                result.Outcome = InsightRuleOutcome.NotApplicable;
                result.NotApplicableReason = "rule failed: " + ruleException.Message;

                ++report.FailedCount;
            }

            if (result == null)
            {
                result = new InsightRuleResult();
                result.RuleId = rule.Id;
                result.Category = rule.Category;
                result.Threshold = rule.Threshold;
                result.Outcome = InsightRuleOutcome.NotApplicable;
                result.NotApplicableReason = "rule returned no verdict";

                ++report.FailedCount;
            }

            report.Results.Add(result);

            switch (result.Outcome)
            {
                case InsightRuleOutcome.Fired:
                    if (result.Insight != null)
                    {
                        report.Insights.Add(result.Insight);
                    }

                    break;

                case InsightRuleOutcome.BelowThreshold:
                    ++report.BelowThresholdCount;
                    break;

                default:
                    ++report.NotApplicableCount;
                    break;
            }
        }

        report.Insights.Sort(CompareInsights);

        report.FiredCount = report.Insights.Count;

        for (int insightIndex = 0; insightIndex < report.Insights.Count; ++insightIndex)
        {
            Insight insight = report.Insights[insightIndex];

            switch (insight.Severity)
            {
                case InsightSeverity.Critical:
                    ++report.CriticalCount;
                    break;

                case InsightSeverity.Warning:
                    ++report.WarningCount;
                    break;

                default:
                    ++report.InfoCount;
                    break;
            }
        }

        report.HighestSeverity = report.Insights.Count > 0 ? report.Insights[0].Severity : InsightSeverity.Info;

        return report;
    }

    private static int CompareInsights(Insight left, Insight right)
    {
        int bySeverity = ((int)right.Severity).CompareTo((int)left.Severity);

        if (bySeverity != 0)
        {
            return bySeverity;
        }

        int byConfidence = ((int)right.Confidence).CompareTo((int)left.Confidence);

        if (byConfidence != 0)
        {
            return byConfidence;
        }

        int byMagnitude = right.Magnitude.CompareTo(left.Magnitude);

        if (byMagnitude != 0)
        {
            return byMagnitude;
        }

        // Determinism, not decoration - see this file's header.
        return string.CompareOrdinal(left.Id, right.Id);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights)
