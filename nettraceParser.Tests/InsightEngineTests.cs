////////////////////////////////////////////////////////////////////////////////
// Module: InsightEngineTests.cs
//
// Notes:
// Tests for the engine itself rather than for any rule: registry
// completeness, the audit list's completeness guarantee, ranking determinism,
// and that one broken rule cannot take the report down with it.
//
// The completeness test is the load-bearing one. The audit list is what makes
// this feature trustworthy - it is how a reader checks that a rule ran and
// measured something rather than silently doing nothing - and it is only worth
// anything if it is guaranteed to have a row per registered rule. A rule that
// evaluates to nothing looks exactly like a rule that was never registered,
// and that is a bug the audit list would otherwise hide rather than expose.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using DotnetInsights.NetTrace.Analysis;
using DotnetInsights.NetTrace.Insights;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class InsightEngineTests
{
    private static CaptureAnalysis MinimalAnalysis()
    {
        CaptureAnalysis analysis = new CaptureAnalysis();
        analysis.ProcessName = "test";
        analysis.FormatVersion = 5;
        analysis.HasCaptureDuration = true;
        analysis.CaptureDurationMSec = 60_000;
        analysis.TotalEventCount = 10;

        return analysis;
    }

    private sealed class ThrowingRule : InsightRule
    {
        public override string Id => "test/throws";

        public override string Category => "Test";

        public override string Threshold => "never";

        public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
        {
            throw new InvalidOperationException("deliberate");
        }
    }

    private sealed class NullReturningRule : InsightRule
    {
        public override string Id => "test/null";

        public override string Category => "Test";

        public override string Threshold => "never";

        public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
        {
            return null;
        }
    }

    private sealed class AlwaysFiresRule : InsightRule
    {
        private readonly InsightSeverity severity;
        private readonly InsightConfidence confidence;
        private readonly double magnitude;
        private readonly string id;

        public AlwaysFiresRule(string id, InsightSeverity severity, InsightConfidence confidence, double magnitude)
        {
            this.id = id;
            this.severity = severity;
            this.confidence = confidence;
            this.magnitude = magnitude;
        }

        public override string Id => this.id;

        public override string Category => "Test";

        public override string Threshold => "always";

        public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
        {
            return this.Fire(this.severity, this.confidence, "fired", "detail", this.magnitude, null, null, null, "measured");
        }
    }

    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void AllRules_RegistersEachRuleIdExactlyOnce()
    {
        List<InsightRule> rules = InsightEngine.AllRules();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        for (int ruleIndex = 0; ruleIndex < rules.Count; ++ruleIndex)
        {
            Assert.True(seen.Add(rules[ruleIndex].Id), "duplicate rule id: " + rules[ruleIndex].Id);
        }

        Assert.Equal(rules.Count, seen.Count);
    }

    // Every rule must state its firing condition. It is emitted verbatim into
    // the report so a reader can disagree with the rule rather than only with
    // its conclusion, and an empty one silently removes that.
    [Fact]
    public void AllRules_EachDeclaresAnIdCategoryAndThreshold()
    {
        List<InsightRule> rules = InsightEngine.AllRules();

        for (int ruleIndex = 0; ruleIndex < rules.Count; ++ruleIndex)
        {
            InsightRule rule = rules[ruleIndex];

            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
            Assert.False(string.IsNullOrWhiteSpace(rule.Category));
            Assert.False(string.IsNullOrWhiteSpace(rule.Threshold));
            Assert.Contains("/", rule.Id);
        }
    }

    // The audit list's completeness guarantee - see this file's header.
    [Fact]
    public void Run_ProducesExactlyOneResultPerRegisteredRule()
    {
        List<InsightRule> rules = InsightEngine.AllRules();
        InsightReport report = InsightEngine.Run(MinimalAnalysis(), rules);

        Assert.Equal(rules.Count, report.Results.Count);

        HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);

        for (int resultIndex = 0; resultIndex < report.Results.Count; ++resultIndex)
        {
            Assert.True(reported.Add(report.Results[resultIndex].RuleId));
        }

        for (int ruleIndex = 0; ruleIndex < rules.Count; ++ruleIndex)
        {
            Assert.Contains(rules[ruleIndex].Id, reported);
        }

        Assert.Equal(
            report.Results.Count,
            report.Insights.Count + report.BelowThresholdCount + report.NotApplicableCount);
    }

    // Every not-fired verdict must carry SOMETHING a reader can act on: either
    // what was measured, or why there was nothing to measure. A blank row is
    // the unauditable filter this design exists to avoid.
    [Fact]
    public void Run_EveryNonFiringResultExplainsItself()
    {
        InsightReport report = InsightEngine.Run(MinimalAnalysis());

        for (int resultIndex = 0; resultIndex < report.Results.Count; ++resultIndex)
        {
            InsightRuleResult result = report.Results[resultIndex];

            if (result.Outcome == InsightRuleOutcome.BelowThreshold)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Measured), result.RuleId + " did not report what it measured");
            }
            else if (result.Outcome == InsightRuleOutcome.NotApplicable)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.NotApplicableReason), result.RuleId + " did not say why it could not run");
            }
        }
    }

    [Fact]
    public void Run_AThrowingRuleCostsItsOwnRowAndNothingElse()
    {
        List<InsightRule> rules = new List<InsightRule>
        {
            new ThrowingRule(),
            new AlwaysFiresRule("test/ok", InsightSeverity.Warning, InsightConfidence.High, 1.0)
        };

        InsightReport report = InsightEngine.Run(MinimalAnalysis(), rules);

        Assert.Equal(2, report.Results.Count);
        Assert.Equal(1, report.FailedCount);
        Assert.Single(report.Insights);
        Assert.Contains("deliberate", report.Results[0].NotApplicableReason);
    }

    [Fact]
    public void Run_ARuleReturningNoVerdictIsReportedRatherThanDropped()
    {
        InsightReport report = InsightEngine.Run(MinimalAnalysis(), new List<InsightRule> { new NullReturningRule() });

        Assert.Single(report.Results);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(InsightRuleOutcome.NotApplicable, report.Results[0].Outcome);
    }

    [Fact]
    public void Run_RanksBySeverityThenConfidenceThenMagnitude()
    {
        List<InsightRule> rules = new List<InsightRule>
        {
            new AlwaysFiresRule("test/info", InsightSeverity.Info, InsightConfidence.High, 5.0),
            new AlwaysFiresRule("test/warn-low", InsightSeverity.Warning, InsightConfidence.Low, 9.0),
            new AlwaysFiresRule("test/warn-high", InsightSeverity.Warning, InsightConfidence.High, 1.0),
            new AlwaysFiresRule("test/critical", InsightSeverity.Critical, InsightConfidence.Low, 1.0)
        };

        InsightReport report = InsightEngine.Run(MinimalAnalysis(), rules);

        Assert.Equal("test/critical", report.Insights[0].Id);
        // Confidence outranks magnitude: warn-high (1.0) beats warn-low (9.0).
        Assert.Equal("test/warn-high", report.Insights[1].Id);
        Assert.Equal("test/warn-low", report.Insights[2].Id);
        Assert.Equal("test/info", report.Insights[3].Id);

        Assert.Equal(InsightSeverity.Critical, report.HighestSeverity);
        Assert.Equal(1, report.CriticalCount);
        Assert.Equal(2, report.WarningCount);
        Assert.Equal(1, report.InfoCount);
    }

    // Two findings identical on severity, confidence and magnitude must still
    // order deterministically, or the same capture ranks differently between
    // runs - the trap the hot-method ranking already had to fix.
    [Fact]
    public void Run_TiesBreakOnIdSoRankingIsDeterministic()
    {
        List<InsightRule> rules = new List<InsightRule>
        {
            new AlwaysFiresRule("test/zebra", InsightSeverity.Warning, InsightConfidence.High, 1.0),
            new AlwaysFiresRule("test/alpha", InsightSeverity.Warning, InsightConfidence.High, 1.0)
        };

        InsightReport report = InsightEngine.Run(MinimalAnalysis(), rules);

        Assert.Equal("test/alpha", report.Insights[0].Id);
        Assert.Equal("test/zebra", report.Insights[1].Id);
    }

    ////////////////////////////////////////////////////////////////////////////
    // Text report and exit codes
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void TextReport_StatesTheNotFiredCountsEvenWithoutTheAuditList()
    {
        InsightReport report = InsightEngine.Run(MinimalAnalysis());

        StringWriter output = new StringWriter();
        InsightReportText.Write(output, report, includeAuditList: false);
        string text = output.ToString();

        Assert.Contains("did not fire", text);
        Assert.Contains("had no data", text);
        Assert.DoesNotContain("== Rules evaluated ==", text);
    }

    [Fact]
    public void TextReport_AuditListNamesEveryRule()
    {
        List<InsightRule> rules = InsightEngine.AllRules();
        InsightReport report = InsightEngine.Run(MinimalAnalysis(), rules);

        StringWriter output = new StringWriter();
        InsightReportText.Write(output, report, includeAuditList: true);
        string text = output.ToString();

        Assert.Contains("== Rules evaluated ==", text);

        for (int ruleIndex = 0; ruleIndex < rules.Count; ++ruleIndex)
        {
            Assert.Contains(rules[ruleIndex].Id, text);
        }
    }

    [Fact]
    public void TextReport_IsStableForTheSameReport()
    {
        InsightReport report = InsightEngine.Run(MinimalAnalysis());

        StringWriter first = new StringWriter();
        StringWriter second = new StringWriter();
        InsightReportText.Write(first, report, includeAuditList: true);
        InsightReportText.Write(second, report, includeAuditList: true);

        Assert.Equal(first.ToString(), second.ToString());
    }

    [Fact]
    public void FailOn_MapsSeverityToExitCode()
    {
        InsightReport warningOnly = InsightEngine.Run(
            MinimalAnalysis(),
            new List<InsightRule> { new AlwaysFiresRule("test/warn", InsightSeverity.Warning, InsightConfidence.High, 1.0) });

        Assert.Equal(InsightExitCode.ThresholdMet, InsightExitCode.For(warningOnly, "warning"));
        Assert.Equal(InsightExitCode.ThresholdMet, InsightExitCode.For(warningOnly, "info"));
        Assert.Equal(InsightExitCode.Success, InsightExitCode.For(warningOnly, "critical"));
    }

    // A typo in a CI config must not silently mean "never fail".
    [Fact]
    public void FailOn_RejectsAnUnrecognizedSeverity()
    {
        InsightReport report = InsightEngine.Run(MinimalAnalysis());

        Assert.Equal(InsightExitCode.BadSeverityArgument, InsightExitCode.For(report, "sever"));
        Assert.False(InsightExitCode.TryParseSeverity("sever", out _));
        Assert.True(InsightExitCode.TryParseSeverity("CRITICAL", out InsightSeverity parsed));
        Assert.Equal(InsightSeverity.Critical, parsed);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
