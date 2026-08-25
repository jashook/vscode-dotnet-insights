////////////////////////////////////////////////////////////////////////////////
// Module: ExceptionRules.cs
//
// Notes:
// Two rules over first-chance exceptions.
//
// FIRST CHANCE, NOT UNHANDLED. Every throw in the process appears here,
// including ones that were caught and handled a frame later and that nobody
// would call a bug. That is exactly what makes the rate interesting and also
// what makes it easy to over-report: a service with a well-behaved retry
// policy legitimately throws, and a rule that treated any exception as a
// defect would fire on every capture and be ignored on all of them.
//
// So both rules are about VOLUME and CONCENTRATION rather than about
// exceptions existing, and the minimum counts exist to keep a short capture
// with four exceptions from being described as having an exception problem.
//
// The cost being reported is real regardless of handling: a throw walks the
// stack to find a handler, and on a hot path that is not cheap. A capture
// showing thousands per second is nearly always exceptions used as control
// flow - a parse that throws instead of returning false, a cache miss modelled
// as KeyNotFoundException.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights.Rules {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Analysis;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

internal static class ExceptionRuleGate
{
    public const string NoExceptionEvents = "this capture recorded no exception events (the Exception keyword was not collected, or nothing threw)";

    public static bool HasExceptions(CaptureAnalysis analysis)
    {
        return analysis.Contents.HasExceptionEvents && analysis.Exceptions.TotalCount > 0;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ExceptionRateRule : InsightRule
{
    public const double InfoPerSecond = 10.0;
    public const double WarningPerSecond = 100.0;
    public const double CriticalPerSecond = 1000.0;

    public override string Id => "exceptions/rate";

    public override string Category => "Exceptions";

    public override string Threshold =>
        "first-chance exceptions >= " + InfoPerSecond.ToString("F0") + "/s ("
        + WarningPerSecond.ToString("F0") + "/s is a warning, " + CriticalPerSecond.ToString("F0") + "/s is critical)";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!ExceptionRuleGate.HasExceptions(analysis))
        {
            return this.NotApplicable(ExceptionRuleGate.NoExceptionEvents);
        }

        if (!analysis.HasCaptureDuration || analysis.CaptureDurationSeconds <= 0)
        {
            return this.NotApplicable("this capture has no measurable duration to compute an exception rate over");
        }

        double perSecond = analysis.Exceptions.PerSecond(analysis.CaptureDurationSeconds);

        string measured = perSecond.ToString("F1") + " exceptions/s ("
            + FormatCount(analysis.Exceptions.TotalCount) + " over " + FormatDuration(analysis.CaptureDurationMSec) + ")";

        if (perSecond < InfoPerSecond)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Exception rate", perSecond.ToString("F1") + "/s", perSecond));
        evidence.Add(new InsightEvidence("Total thrown", FormatCount(analysis.Exceptions.TotalCount), analysis.Exceptions.TotalCount));
        evidence.Add(new InsightEvidence("Distinct types", FormatCount(analysis.Exceptions.DistinctTypeCount), analysis.Exceptions.DistinctTypeCount));

        if (analysis.Exceptions.TopTypes.Count > 0)
        {
            ExceptionTypeRecord topType = analysis.Exceptions.TopTypes[0];
            evidence.Add(new InsightEvidence("Most thrown", topType.TypeName + " (" + FormatPercent(topType.PercentOfTotal) + ")", topType.PercentOfTotal));
        }

        InsightSeverity severity = InsightSeverity.Info;

        if (perSecond >= CriticalPerSecond)
        {
            severity = InsightSeverity.Critical;
        }
        else if (perSecond >= WarningPerSecond)
        {
            severity = InsightSeverity.Warning;
        }

        return this.Fire(
            severity,
            InsightConfidence.High,
            perSecond.ToString("F1") + " first-chance exceptions per second (" + FormatCount(analysis.Exceptions.TotalCount) + " in total)",
            "These are first-chance throws: every exception raised in the process, whether or not it was caught "
            + "and handled immediately afterwards. Throwing is not free even when handled - the runtime walks the "
            + "stack looking for a handler - so at this rate it is a measurable cost in its own right. A rate this "
            + "high almost always means exceptions are being used where a return value would do.",
            perSecond / InfoPerSecond,
            evidence,
            new List<string>
            {
                "Open the Exceptions view and expand the top type to see where it is thrown from.",
                "Look for Try-prefixed alternatives on the hot path (TryParse, TryGetValue) that avoid the throw entirely."
            },
            new List<InsightLink> { new InsightLink("exceptions", "Exceptions view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ExceptionTypeConcentrationRule : InsightRule
{
    public const double ShareThreshold = 50.0;
    public const int MinimumTotalCount = 100;

    public override string Id => "exceptions/type-concentration";

    public override string Category => "Exceptions";

    public override string Threshold =>
        "one exception type accounts for >= " + FormatPercent(ShareThreshold) + " of at least "
        + MinimumTotalCount + " total throws";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!ExceptionRuleGate.HasExceptions(analysis))
        {
            return this.NotApplicable(ExceptionRuleGate.NoExceptionEvents);
        }

        if (analysis.Exceptions.TopTypes.Count == 0)
        {
            return this.NotApplicable("no exception type names were resolved in this capture");
        }

        ExceptionTypeRecord topType = analysis.Exceptions.TopTypes[0];

        string measured = "top type " + topType.TypeName + " at " + FormatPercent(topType.PercentOfTotal)
            + " of " + FormatCount(analysis.Exceptions.TotalCount) + " throws";

        if (topType.PercentOfTotal < ShareThreshold || analysis.Exceptions.TotalCount < MinimumTotalCount)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Type", topType.TypeName, 0));
        evidence.Add(new InsightEvidence("Thrown", FormatCount(topType.Count), topType.Count));
        evidence.Add(new InsightEvidence("Share of all throws", FormatPercent(topType.PercentOfTotal), topType.PercentOfTotal));

        if (!string.IsNullOrEmpty(topType.SampleMessage))
        {
            evidence.Add(new InsightEvidence("Example message", topType.SampleMessage, 0));
        }

        if (analysis.HasCaptureDuration && analysis.CaptureDurationSeconds > 0)
        {
            double typePerSecond = topType.Count / analysis.CaptureDurationSeconds;
            evidence.Add(new InsightEvidence("Rate for this type", typePerSecond.ToString("F1") + "/s", typePerSecond));
        }

        return this.Fire(
            InsightSeverity.Warning,
            InsightConfidence.High,
            topType.TypeName + " is " + FormatPercent(topType.PercentOfTotal) + " of all exceptions thrown (" + FormatCount(topType.Count) + " times)",
            "A single exception type dominating this heavily is the signature of exceptions being used as control "
            + "flow rather than for exceptional conditions - one code path is throwing it over and over, and "
            + "something is catching it just as reliably. Because it is one type from (usually) one place, it is "
            + "also the cheapest exception finding to act on.",
            topType.PercentOfTotal / ShareThreshold,
            evidence,
            new List<string> { "Expand " + topType.TypeName + " in the Exceptions view to see the throw sites and their call paths." },
            new List<InsightLink> { new InsightLink("exceptions", "Exceptions view: " + topType.TypeName, "type", topType.TypeName) },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights.Rules)
