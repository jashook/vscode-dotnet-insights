////////////////////////////////////////////////////////////////////////////////
// Module: InsightReportJson.cs
//
// Notes:
// Serializes an InsightReport, both as a standalone file (--insights-json) and
// as the "insights" section of the main --json export.
//
// ONE WRITER, TWO CALLERS, on purpose. The webview and a script reading
// --insights-json must see the same shape, or a rule that renders correctly in
// one and wrongly in the other becomes possible - and only the webview would
// ever get noticed.
//
// Utf8JsonWriter rather than a JsonNode tree, per this project's own
// convention. The payload here is small (tens of KB), so this is a
// consistency choice rather than a measured one, but the alternative buys
// nothing and the convention exists for good reasons elsewhere.
//
// THE AUDIT LIST IS SERIALIZED TOO, not just the findings. A consumer that
// received only what fired would have no way to distinguish "the rule ran and
// this capture is clean" from "the rule had no data" - which is the exact
// distinction Insight.cs's three outcomes exist to preserve.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.IO;
using System.Text.Json;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class InsightReportJson
{
    public static void WriteToFile(string outputPath, InsightReport report)
    {
        using (FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (Utf8JsonWriter writer = new Utf8JsonWriter(fileStream, new JsonWriterOptions { Indented = true }))
        {
            Write(writer, report);
            writer.Flush();
        }
    }

    // Writes the report as a VALUE, so the --json export can call this
    // straight after a WritePropertyName("insights") without the two agreeing
    // on a property name in two places.
    public static void Write(Utf8JsonWriter writer, InsightReport report)
    {
        writer.WriteStartObject();

        writer.WriteNumber("firedCount", report.FiredCount);
        writer.WriteNumber("criticalCount", report.CriticalCount);
        writer.WriteNumber("warningCount", report.WarningCount);
        writer.WriteNumber("infoCount", report.InfoCount);
        writer.WriteNumber("belowThresholdCount", report.BelowThresholdCount);
        writer.WriteNumber("notApplicableCount", report.NotApplicableCount);
        writer.WriteNumber("failedCount", report.FailedCount);

        writer.WritePropertyName("insights");
        writer.WriteStartArray();

        for (int insightIndex = 0; insightIndex < report.Insights.Count; ++insightIndex)
        {
            WriteInsight(writer, report.Insights[insightIndex]);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("rules");
        writer.WriteStartArray();

        for (int resultIndex = 0; resultIndex < report.Results.Count; ++resultIndex)
        {
            InsightRuleResult result = report.Results[resultIndex];

            writer.WriteStartObject();
            writer.WriteString("id", result.RuleId);
            writer.WriteString("category", result.Category);
            writer.WriteString("threshold", result.Threshold);
            writer.WriteString("outcome", OutcomeName(result.Outcome));
            writer.WriteString("measured", result.Measured);
            writer.WriteString("notApplicableReason", result.NotApplicableReason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteInsight(Utf8JsonWriter writer, Insight insight)
    {
        writer.WriteStartObject();

        writer.WriteString("id", insight.Id);
        writer.WriteString("category", insight.Category);
        writer.WriteString("severity", InsightReportText.SeverityLabel(insight.Severity).ToLowerInvariant());
        writer.WriteString("confidence", InsightReportText.ConfidenceLabel(insight.Confidence));
        writer.WriteString("headline", insight.Headline);
        writer.WriteString("threshold", insight.Threshold);
        writer.WriteString("detail", insight.Detail);
        writer.WriteNumber("magnitude", insight.Magnitude);

        writer.WritePropertyName("evidence");
        writer.WriteStartArray();

        for (int evidenceIndex = 0; evidenceIndex < insight.Evidence.Count; ++evidenceIndex)
        {
            InsightEvidence evidence = insight.Evidence[evidenceIndex];

            writer.WriteStartObject();
            writer.WriteString("label", evidence.Label);
            writer.WriteString("value", evidence.Value);
            writer.WriteNumber("numeric", evidence.Numeric);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("actions");
        writer.WriteStartArray();

        for (int actionIndex = 0; actionIndex < insight.Actions.Count; ++actionIndex)
        {
            writer.WriteStringValue(insight.Actions[actionIndex]);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("links");
        writer.WriteStartArray();

        for (int linkIndex = 0; linkIndex < insight.Links.Count; ++linkIndex)
        {
            InsightLink link = insight.Links[linkIndex];

            writer.WriteStartObject();
            // Matches the data-view attribute GcSnapshotRenderer.ts already
            // puts on its nav buttons, so the webview needs no mapping table.
            writer.WriteString("view", link.View);
            writer.WriteString("label", link.Label);
            writer.WriteString("targetKind", link.TargetKind);
            writer.WriteString("targetValue", link.TargetValue);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    public static string OutcomeName(InsightRuleOutcome outcome)
    {
        switch (outcome)
        {
            case InsightRuleOutcome.Fired: return "fired";
            case InsightRuleOutcome.BelowThreshold: return "belowThreshold";
            default: return "notApplicable";
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// --fail-on's severity-to-exit-code mapping, split out so it can be tested
// without spawning a process.
public static class InsightExitCode
{
    public const int Success = 0;
    public const int ThresholdMet = 2;
    public const int BadSeverityArgument = 64;

    // Exit 2, not 1, when the threshold is met: 1 is what this tool already
    // returns for "could not do the job" (a missing file, an unreadable dump),
    // and a CI script must be able to tell "the capture has a critical
    // finding" apart from "the parse failed". Returns 64 (the conventional
    // usage-error code) for an unrecognized severity rather than silently
    // treating it as "never fail", which would leave a typo in a CI config
    // passing forever.
    public static int For(InsightReport report, string severityArgument)
    {
        if (!TryParseSeverity(severityArgument, out InsightSeverity threshold))
        {
            System.Console.Error.WriteLine("--fail-on expects one of: info, warning, critical (got \"" + severityArgument + "\")");
            return BadSeverityArgument;
        }

        for (int insightIndex = 0; insightIndex < report.Insights.Count; ++insightIndex)
        {
            if (report.Insights[insightIndex].Severity >= threshold)
            {
                return ThresholdMet;
            }
        }

        return Success;
    }

    public static bool TryParseSeverity(string text, out InsightSeverity severity)
    {
        severity = InsightSeverity.Info;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        switch (text.ToLowerInvariant())
        {
            case "info":
                severity = InsightSeverity.Info;
                return true;

            case "warning":
                severity = InsightSeverity.Warning;
                return true;

            case "critical":
                severity = InsightSeverity.Critical;
                return true;

            default:
                return false;
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights)
