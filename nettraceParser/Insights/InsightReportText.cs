////////////////////////////////////////////////////////////////////////////////
// Module: InsightReportText.cs
//
// Notes:
// Renders an InsightReport as the text `--insights` prints. This is the
// PRIMARY surface, not a debug dump of the webview's data: the CLI is where
// thresholds get calibrated (a run against the capture corpus, no VS Code in
// the loop) and where anyone scripting this tool will read the output.
//
// WRITES TO A TextWriter, NEVER TO Console DIRECTLY, so tests can render into
// a StringWriter and Program.cs can point it at stdout. Everything this
// produces belongs on stdout - progress and the Timing: line stay on stderr
// where they already are, which is what keeps `--insights | less` and
// `--insights > report.txt` working.
//
// NO ANSI COLOUR. The severity is already the first word of every finding,
// and the output is expected to be piped, redirected and diffed between runs
// during calibration - escape sequences would end up in all three. Alignment
// does the work colour would.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using DotnetInsights.NetTrace.Analysis;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class InsightReportText
{
    private const int WrapColumn = 88;
    private const int EvidenceLabelWidth = 30;

    public static void Write(TextWriter output, InsightReport report, bool includeAuditList)
    {
        WriteCaptureLine(output, report.Analysis);

        output.WriteLine();

        if (report.Insights.Count == 0)
        {
            output.WriteLine("No insights fired.");
            output.WriteLine();
            WriteFooter(output, report, includeAuditList);

            if (includeAuditList)
            {
                WriteAuditList(output, report);
            }

            return;
        }

        for (int insightIndex = 0; insightIndex < report.Insights.Count; ++insightIndex)
        {
            WriteInsight(output, report.Insights[insightIndex]);
            output.WriteLine();
        }

        WriteFooter(output, report, includeAuditList);

        if (includeAuditList)
        {
            WriteAuditList(output, report);
        }
    }

    private static void WriteCaptureLine(TextWriter output, CaptureAnalysis analysis)
    {
        string duration = analysis.HasCaptureDuration
            ? InsightRule.FormatDuration(analysis.CaptureDurationMSec)
            : "unknown duration";

        output.WriteLine("Capture: " + analysis.ProcessName
            + " (" + duration
            + ", " + analysis.TotalEventCount.ToString("N0", CultureInfo.InvariantCulture) + " events"
            + ", nettrace v" + analysis.FormatVersion + ")");
    }

    private static void WriteInsight(TextWriter output, Insight insight)
    {
        // Severity, id and confidence on one line, aligned so a report scans
        // vertically - the severity column is what a reader's eye follows.
        string header = SeverityLabel(insight.Severity).PadRight(10) + insight.Id;
        string confidence = "confidence: " + ConfidenceLabel(insight.Confidence);

        if (header.Length + confidence.Length + 2 <= WrapColumn)
        {
            output.WriteLine(header.PadRight(WrapColumn - confidence.Length) + confidence);
        }
        else
        {
            output.WriteLine(header + "  " + confidence);
        }

        WriteWrapped(output, insight.Headline, "  ");

        for (int evidenceIndex = 0; evidenceIndex < insight.Evidence.Count; ++evidenceIndex)
        {
            InsightEvidence evidence = insight.Evidence[evidenceIndex];

            // Evidence labels are already indented by their own rule where
            // they are meant to nest (a "  Top method" under its category), so
            // the padding is applied to whatever the rule chose rather than
            // being trimmed and re-added.
            output.WriteLine("    " + evidence.Label.PadRight(EvidenceLabelWidth) + " " + Elide(evidence.Value));
        }

        if (!string.IsNullOrEmpty(insight.Detail))
        {
            output.WriteLine();
            WriteWrapped(output, insight.Detail, "    ");
        }

        if (!string.IsNullOrEmpty(insight.Threshold))
        {
            output.WriteLine();
            WriteWrapped(output, "Fires when: " + insight.Threshold, "  ");
        }

        for (int actionIndex = 0; actionIndex < insight.Actions.Count; ++actionIndex)
        {
            // Hanging indent so a wrapped action's continuation lines sit under
            // its text rather than under its bullet, which otherwise reads as
            // two separate actions.
            WriteWrapped(output, insight.Actions[actionIndex], "    - ", "      ");
        }

        for (int linkIndex = 0; linkIndex < insight.Links.Count; ++linkIndex)
        {
            output.WriteLine("  -> " + Elide(insight.Links[linkIndex].Label));
        }
    }

    // Text-report only. The full value always reaches the JSON and the
    // webview - a sample exception message or a deeply generic type name is
    // worth having in full somewhere, just not wrapped across four lines of a
    // terminal report whose whole value is being scannable.
    private const int MaxEvidenceValueLength = 96;

    private static string Elide(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxEvidenceValueLength)
        {
            return value;
        }

        return value.Substring(0, MaxEvidenceValueLength - 3) + "...";
    }

    private static void WriteFooter(TextWriter output, InsightReport report, bool includeAuditList)
    {
        List<string> parts = new List<string>();

        if (report.CriticalCount > 0)
        {
            parts.Add(report.CriticalCount + " critical");
        }

        if (report.WarningCount > 0)
        {
            parts.Add(report.WarningCount + " warning");
        }

        if (report.InfoCount > 0)
        {
            parts.Add(report.InfoCount + " info");
        }

        string summary = parts.Count > 0 ? string.Join(", ", parts) : "nothing fired";

        // The not-fired and not-applicable counts are always stated, even when
        // the audit list itself is not being printed. A report that only ever
        // shows what fired gives a reader no way to know whether the silence
        // is a clean result or a rule that never ran - see Insight.cs.
        string tail = report.BelowThresholdCount + " rules measured and did not fire, "
            + report.NotApplicableCount + " had no data";

        if (!includeAuditList)
        {
            tail += " (--insights-all to list them)";
        }

        output.WriteLine(summary + ".  " + tail + ".");

        if (report.FailedCount > 0)
        {
            output.WriteLine(report.FailedCount + " rule(s) failed to evaluate; see the list below.");
        }
    }

    private static void WriteAuditList(TextWriter output, InsightReport report)
    {
        output.WriteLine();
        output.WriteLine("== Rules evaluated ==");

        for (int resultIndex = 0; resultIndex < report.Results.Count; ++resultIndex)
        {
            InsightRuleResult result = report.Results[resultIndex];

            string outcome;
            string detail;

            switch (result.Outcome)
            {
                case InsightRuleOutcome.Fired:
                    outcome = "FIRED";
                    detail = result.Measured;
                    break;

                case InsightRuleOutcome.BelowThreshold:
                    outcome = "under";
                    detail = result.Measured;
                    break;

                default:
                    outcome = "no data";
                    detail = result.NotApplicableReason;
                    break;
            }

            output.WriteLine("  " + outcome.PadRight(8) + result.RuleId.PadRight(38) + detail);
        }
    }

    public static string SeverityLabel(InsightSeverity severity)
    {
        switch (severity)
        {
            case InsightSeverity.Critical: return "CRITICAL";
            case InsightSeverity.Warning: return "WARNING";
            default: return "INFO";
        }
    }

    public static string ConfidenceLabel(InsightConfidence confidence)
    {
        switch (confidence)
        {
            case InsightConfidence.High: return "high";
            case InsightConfidence.Medium: return "medium";
            default: return "low";
        }
    }

    // Greedy wrap on spaces. Words longer than the wrap width (a mangled
    // native symbol, a deeply generic type name) are emitted whole and allowed
    // to overhang rather than being broken - a name split across two lines
    // cannot be copied out of the terminal and pasted into a search, which is
    // the main thing anybody does with one.
    private static void WriteWrapped(TextWriter output, string text, string indent)
    {
        WriteWrapped(output, text, indent, indent);
    }

    private static void WriteWrapped(TextWriter output, string text, string firstLineIndent, string continuationIndent)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int available = WrapColumn - continuationIndent.Length;

        if (available < 20)
        {
            output.WriteLine(firstLineIndent + text);
            return;
        }

        string indent = firstLineIndent;

        string[] words = text.Split(' ');
        System.Text.StringBuilder line = new System.Text.StringBuilder();

        for (int wordIndex = 0; wordIndex < words.Length; ++wordIndex)
        {
            string word = words[wordIndex];

            if (word.Length == 0)
            {
                continue;
            }

            if (line.Length > 0 && line.Length + 1 + word.Length > available)
            {
                output.WriteLine(indent + line.ToString());
                line.Clear();
                indent = continuationIndent;
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            output.WriteLine(indent + line.ToString());
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights)
