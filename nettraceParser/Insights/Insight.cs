////////////////////////////////////////////////////////////////////////////////
// Module: Insight.cs
//
// Notes:
// The shape of one finding, and of one rule's verdict on one capture.
//
// THE HOUSE RULE this whole feature lives or dies by: every insight states
// the MEASUREMENT, the THRESHOLD, and the NUMBERS it fired on. "Consider
// reducing allocations" is worse than saying nothing, because it teaches the
// reader to skip the panel - and once they skip the panel, the one finding
// that mattered goes with it. Hence Evidence is non-optional in practice and
// Threshold is emitted verbatim: a reader has to be able to disagree with the
// RULE, not just with its conclusion.
//
// THREE VERDICTS, NOT TWO. A rule either fired, or measured something and
// came in under its threshold, or had nothing to measure. That last one is
// not a quiet "no": a `dotnet-trace collect-linux` capture carries no
// allocation ticks and no contention events at all, and a rule that reports
// "no allocation pressure found" on it is stating something it cannot know.
// InsightRuleOutcome keeps the three apart, and the audit list renders them
// differently.
//
// THE AUDIT LIST is why rules report a measured value even when they do not
// fire. This mirrors the Threading view's excluded-samples table and exists
// for the same reason: a filter that cannot be audited is one nobody believes
// the first time it hides something they expected. It is also the only
// feedback signal threshold calibration has.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Ordered so a larger value is more urgent - the ranking sorts on the
// underlying int, so the order here is the display order.
public enum InsightSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

// How much the rule trusts its own answer. Kept separate from severity
// because they genuinely vary independently: a name-heuristic rule can be
// confident that the consequence is severe while being unsure the pattern is
// really there, and collapsing the two into one number hides exactly that.
public enum InsightConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum InsightRuleOutcome
{
    // The rule measured its input and the threshold was met.
    Fired = 0,

    // The rule measured its input and the threshold was not met. The
    // measurement is still reported, in the audit list.
    BelowThreshold = 1,

    // The capture does not contain what this rule needs. Distinct from
    // BelowThreshold on purpose - see this file's header.
    NotApplicable = 2
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// One number behind a finding. Value is pre-formatted for display (so the
// rule decides whether its own quantity reads better as "11.4s", "38.2%" or
// "1,462"), and Numeric carries the raw magnitude for anything that wants to
// sort or compare rather than print.
public sealed class InsightEvidence
{
    public string Label = "";
    public string Value = "";
    public double Numeric;

    public InsightEvidence()
    {
    }

    public InsightEvidence(string label, string value, double numeric)
    {
        this.Label = label;
        this.Value = value;
        this.Numeric = numeric;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Where in the existing UI this finding can be seen for yourself. View names
// match the `data-view` attributes GcSnapshotRenderer.ts already puts on its
// nav buttons, so the webview needs no mapping table - it clicks the button
// the insight names. TargetKind/TargetValue narrow it further where the view
// can act on that (a GC id, a method name); both empty means "just open the
// view".
public sealed class InsightLink
{
    public string View = "";
    public string Label = "";
    public string TargetKind = "";
    public string TargetValue = "";

    public InsightLink()
    {
    }

    public InsightLink(string view, string label)
    {
        this.View = view;
        this.Label = label;
    }

    public InsightLink(string view, string label, string targetKind, string targetValue)
    {
        this.View = view;
        this.Label = label;
        this.TargetKind = targetKind;
        this.TargetValue = targetValue;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class Insight
{
    // Stable, slash-namespaced, and never renumbered - it is what a user
    // filters on, what a test pins, and what calibration notes refer to.
    public string Id = "";
    public string Category = "";

    public InsightSeverity Severity;
    public InsightConfidence Confidence;

    // One line, stating the finding AS A MEASUREMENT. "12 blocking gen2 GCs
    // carry 71% of all pause time", not "GC pressure detected".
    public string Headline = "";

    // The firing condition in words, emitted verbatim so the threshold is
    // arguable rather than hidden.
    public string Threshold = "";

    // What it means and why it matters. May be several sentences; the CLI
    // wraps it and the webview renders it under the evidence.
    public string Detail = "";

    public List<InsightEvidence> Evidence = new List<InsightEvidence>();
    public List<string> Actions = new List<string>();
    public List<InsightLink> Links = new List<InsightLink>();

    // How far past its own threshold this rule fired, normalized so it is
    // comparable between rules (1.0 = exactly at the threshold, 2.0 = twice
    // it). Used only to order findings that tie on severity and confidence,
    // so a capture with three critical findings leads with the worst one.
    public double Magnitude = 1.0;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A rule's full verdict, including the not-fired cases the audit list renders.
public sealed class InsightRuleResult
{
    public string RuleId = "";
    public string Category = "";
    public string Threshold = "";

    public InsightRuleOutcome Outcome;

    // Non-null if and only if Outcome is Fired.
    public Insight Insight;

    // What the rule actually measured, in one line. Present for both Fired
    // and BelowThreshold - this is the audit list's whole point.
    public string Measured = "";

    // Why the rule could not run. Present only for NotApplicable, and it must
    // name the missing DATA ("this capture recorded no allocation ticks"),
    // never imply a negative result.
    public string NotApplicableReason = "";
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights)
