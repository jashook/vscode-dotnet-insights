////////////////////////////////////////////////////////////////////////////////
// Module: InsightRule.cs
//
// Notes:
// The base every rule in Rules/ derives from, plus the formatting helpers
// they share.
//
// WHY A BASE CLASS AND NOT JUST AN INTERFACE. Every rule has to produce the
// same three-verdict result (see Insight.cs) with its own id, category and
// threshold text stamped on it, and doing that by hand in 25 files is 25
// chances to forget the audit-list measurement on the not-fired path - which
// is precisely the path nobody looks at until calibration, by which point the
// gap is invisible. Fire/BelowThreshold/NotApplicable stamp it for you.
//
// FORMATTING lives here rather than in each rule because the report is read
// as a whole: a capture that reports "11462ms" in one finding and "11.5s" in
// the next reads as two tools stapled together. Bytes, durations, percentages
// and counts each have exactly one rendering.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;

using DotnetInsights.NetTrace.Analysis;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public abstract class InsightRule
{
    // Stable id, slash-namespaced by area: "gc/pause-share". Never renumbered
    // or renamed - tests pin it and calibration notes cite it.
    public abstract string Id { get; }

    public abstract string Category { get; }

    // The firing condition in words. Emitted verbatim into the report so the
    // reader can argue with the rule instead of only with its conclusion.
    public abstract string Threshold { get; }

    public abstract InsightRuleResult Evaluate(CaptureAnalysis analysis);

    ////////////////////////////////////////////////////////////////////////////
    // Verdict helpers
    ////////////////////////////////////////////////////////////////////////////

    protected InsightRuleResult Fire(
        InsightSeverity severity,
        InsightConfidence confidence,
        string headline,
        string detail,
        double magnitude,
        List<InsightEvidence> evidence,
        List<string> actions,
        List<InsightLink> links,
        string measured)
    {
        Insight insight = new Insight();
        insight.Id = this.Id;
        insight.Category = this.Category;
        insight.Severity = severity;
        insight.Confidence = confidence;
        insight.Headline = headline;
        insight.Threshold = this.Threshold;
        insight.Detail = detail;
        insight.Magnitude = magnitude;

        if (evidence != null)
        {
            insight.Evidence = evidence;
        }

        if (actions != null)
        {
            insight.Actions = actions;
        }

        if (links != null)
        {
            insight.Links = links;
        }

        InsightRuleResult result = this.NewResult();
        result.Outcome = InsightRuleOutcome.Fired;
        result.Insight = insight;
        result.Measured = measured;

        return result;
    }

    // The rule ran and its threshold was not met. `measured` is not optional
    // in spirit: it is the whole content of this rule's row in the audit
    // list, and "did not fire" with no number attached is exactly the
    // unauditable filter this design exists to avoid.
    protected InsightRuleResult BelowThreshold(string measured)
    {
        InsightRuleResult result = this.NewResult();
        result.Outcome = InsightRuleOutcome.BelowThreshold;
        result.Measured = measured;

        return result;
    }

    // The capture does not contain what this rule needs. The reason must name
    // the missing DATA, never imply a negative finding - see Insight.cs.
    protected InsightRuleResult NotApplicable(string reason)
    {
        InsightRuleResult result = this.NewResult();
        result.Outcome = InsightRuleOutcome.NotApplicable;
        result.NotApplicableReason = reason;

        return result;
    }

    private InsightRuleResult NewResult()
    {
        InsightRuleResult result = new InsightRuleResult();
        result.RuleId = this.Id;
        result.Category = this.Category;
        result.Threshold = this.Threshold;

        return result;
    }

    protected static List<InsightEvidence> EvidenceList()
    {
        return new List<InsightEvidence>();
    }

    ////////////////////////////////////////////////////////////////////////////
    // Formatting
    ////////////////////////////////////////////////////////////////////////////

    // Binary units, because every byte figure in this tool comes from the
    // runtime's own heap accounting and that is what the GC view already
    // shows.
    public static string FormatBytes(double bytes)
    {
        double absolute = Math.Abs(bytes);

        if (absolute >= 1024.0 * 1024.0 * 1024.0)
        {
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " GB";
        }

        if (absolute >= 1024.0 * 1024.0)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
        }

        if (absolute >= 1024.0)
        {
            return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB";
        }

        return bytes.ToString("F0", CultureInfo.InvariantCulture) + " B";
    }

    // Sub-second durations keep milliseconds because that is the unit GC
    // pause is argued about in; anything longer reads better in seconds.
    public static string FormatDuration(double milliseconds)
    {
        if (Math.Abs(milliseconds) >= 1000.0)
        {
            return (milliseconds / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "s";
        }

        return milliseconds.ToString("F1", CultureInfo.InvariantCulture) + "ms";
    }

    public static string FormatPercent(double percent)
    {
        return percent.ToString("F2", CultureInfo.InvariantCulture) + "%";
    }

    public static string FormatCount(long count)
    {
        return count.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string FormatRate(double perSecond, string unit)
    {
        return perSecond.ToString("F1", CultureInfo.InvariantCulture) + " " + unit + "/s";
    }

    // A fraction rendered as a fraction, for the thread-classification
    // measures whose calibrated thresholds are themselves stated as fractions
    // in CLAUDE.md (managedFraction <= 0.05, topStacksShare >= 0.95). Showing
    // those as percentages would make the report and the recorded reasoning
    // disagree about what the number is.
    public static string FormatFraction(double fraction)
    {
        return fraction.ToString("F3", CultureInfo.InvariantCulture);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights)
