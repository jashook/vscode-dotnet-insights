////////////////////////////////////////////////////////////////////////////////
// Module: CpuRules.cs
//
// Notes:
// Three rules over the CPU sample profile.
//
// SELF, NOT ON-STACK, throughout. Both numbers exist in the analysis and they
// answer different questions (see Cpu/CpuCategoryClassifier.cs's header):
// self attributes a sample to its innermost frame and sums to exactly 100%,
// on-stack counts a sample toward every category its stack passes through and
// deliberately sums to more. Every threshold here is stated against self,
// because a threshold against on-stack would be comparing against a total
// that is not 100% and would fire differently depending on how deep the
// stacks happened to be.
//
// UNRESOLVED IS TESTED SEPARATELY AND FIRST, by its own rule. An unresolved
// frame is named "libcrypto.so.3+0x1234", so it carries a module name that
// looks exactly like a category signal - reporting symbol-less time as if it
// had been attributed is the one thing this breakdown must never do. The
// classifier already gets this right; cpu/unresolved-symbols exists so that
// the REPORT says it too, and says which package would fix it.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights.Rules {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Analysis;
using DotnetInsights.NetTrace.Cpu;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

internal static class CpuRuleGate
{
    public const string NoCpuSamples = "this capture recorded no CPU samples (the sample profiler was not enabled)";

    public static bool HasCpu(CaptureAnalysis analysis)
    {
        return analysis.Contents.HasCpuSamples && analysis.Cpu.TotalSampleCount > 0;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// One rule, not one per category, and it fires once naming every category
// over its own threshold. A rule per category would let a single capture put
// six near-identical findings at the top of the report and push everything
// else off the page - the reader wants "the CPU is going here", once.
//
// Thresholds differ per category because the categories are not comparable:
// 5% in the JIT is remarkable on a warm process, while 5% in the managed
// framework is what a managed process looks like. Categories with no
// meaningful "too high" value - application code, managed framework, kernel -
// are deliberately absent from the table rather than given a large threshold,
// because a threshold implies there is a level at which the reading becomes a
// finding, and for those there is not.
public sealed class CpuCategoryOutlierRule : InsightRule
{
    private struct CategoryThreshold
    {
        public CpuCategory Category;
        public double SelfPercent;
        public string Meaning;

        public CategoryThreshold(CpuCategory category, double selfPercent, string meaning)
        {
            this.Category = category;
            this.SelfPercent = selfPercent;
            this.Meaning = meaning;
        }
    }

    // CALIBRATED AGAINST THE CORPUS, and the calibration is the whole reason
    // these numbers are not round. Measured self-time share across the 20
    // corpus captures carrying CPU samples:
    //
    //     Garbage collection   median 0.01%  p90 0.02%  max 0.02%
    //     Allocation           median 0.00%  p90 0.00%  max 0.01%
    //     JIT                  median 0.00%  p90 0.00%  max 0.00%
    //     TLS / crypto         median 0.00%  p90 0.34%  max 1.96%
    //     Compression          median 0.00%  p90 0.05%  max 0.33%
    //     Locking              median 0.00%  p90 5.22%  max 6.04%
    //     Serialization        median 0.57%  p90 3.21%  max 6.85%
    //     Globalization        median 0.06%  p90 0.12%  max 1.01%
    //
    // The baseline for every one of these is essentially zero: on real service
    // captures the CPU goes to application code, framework code and the
    // kernel, which are deliberately absent from this table (there is no share
    // of application code that is "too high" - that is just the process
    // working). The first version of this table used round 10-15% thresholds
    // and could not fire on ANY capture in the corpus, which is a rule that
    // exists only to appear in the audit list.
    //
    // Each threshold now sits just ABOVE that category's observed maximum, so
    // the rule stays quiet on a normal profile and fires on one where the
    // category is genuinely out of family - which is what it is for.
    private static readonly CategoryThreshold[] Thresholds = new CategoryThreshold[]
    {
        new CategoryThreshold(CpuCategory.GarbageCollection, 5.0, "the collector itself, not the allocation that feeds it"),
        new CategoryThreshold(CpuCategory.Allocation, 5.0, "the allocation helper - mutator cost, not collection cost"),
        new CategoryThreshold(CpuCategory.Jit, 3.0, "compilation; expected at startup, not on a warm process"),
        new CategoryThreshold(CpuCategory.TlsCrypto, 8.0, "TLS handshakes and symmetric crypto"),
        new CategoryThreshold(CpuCategory.Compression, 5.0, "compression and decompression"),
        new CategoryThreshold(CpuCategory.LockingAndSynchronization, 8.0, "lock acquisition and synchronization primitives"),
        new CategoryThreshold(CpuCategory.Serialization, 10.0, "serialization and deserialization"),
        new CategoryThreshold(CpuCategory.Globalization, 3.0, "culture-aware string comparison and collation")
    };

    public override string Id => "cpu/category-outlier";

    public override string Category => "CPU";

    public override string Threshold => "any of GC (5%), allocation (5%), JIT (3%), TLS/crypto (8%), compression (5%), locking (8%), serialization (10%) or globalization (3%) above its own self-time share";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!CpuRuleGate.HasCpu(analysis))
        {
            return this.NotApplicable(CpuRuleGate.NoCpuSamples);
        }

        List<InsightEvidence> evidence = EvidenceList();
        List<string> overThresholdNames = new List<string>();
        List<InsightLink> links = new List<InsightLink>();

        double worstRatio = 0;
        double highestPercent = 0;
        string highestName = "";
        string highestMeaning = "";

        // Reported alongside the firing categories so the audit line is
        // informative even when nothing fires - "GC 3.1%, JIT 0.4%, ..." is a
        // useful thing to read in the not-fired list.
        List<string> allReadings = new List<string>();

        for (int thresholdIndex = 0; thresholdIndex < Thresholds.Length; ++thresholdIndex)
        {
            CategoryThreshold categoryThreshold = Thresholds[thresholdIndex];
            CpuCategoryRecord record = analysis.Cpu.TryGetCategory((int)categoryThreshold.Category);

            if (record == null)
            {
                continue;
            }

            allReadings.Add(record.Name + " " + FormatPercent(record.SelfPercent));

            if (record.SelfPercent < categoryThreshold.SelfPercent)
            {
                continue;
            }

            overThresholdNames.Add(record.Name);
            evidence.Add(new InsightEvidence(record.Name, FormatPercent(record.SelfPercent) + " self (threshold " + FormatPercent(categoryThreshold.SelfPercent) + ")", record.SelfPercent));

            if (record.TopMethods.Count > 0)
            {
                evidence.Add(new InsightEvidence("  Top method", record.TopMethods[0].Name + " (" + FormatPercent(record.TopMethods[0].SelfPercent) + ")", record.TopMethods[0].SelfPercent));
            }

            double ratio = record.SelfPercent / categoryThreshold.SelfPercent;

            if (ratio > worstRatio)
            {
                worstRatio = ratio;
                highestPercent = record.SelfPercent;
                highestName = record.Name;
                highestMeaning = categoryThreshold.Meaning;
            }
        }

        string measured = allReadings.Count > 0 ? string.Join(", ", allReadings) : "no tracked categories present";

        if (overThresholdNames.Count == 0)
        {
            return this.BelowThreshold(measured);
        }

        links.Add(new InsightLink("profile", "CPU Profile view"));

        string headline = overThresholdNames.Count == 1
            ? highestName + " is " + FormatPercent(highestPercent) + " of CPU self time"
            : overThresholdNames.Count + " CPU categories are above their expected share, led by " + highestName + " at " + FormatPercent(highestPercent);

        return this.Fire(
            worstRatio >= 2.0 ? InsightSeverity.Warning : InsightSeverity.Info,
            InsightConfidence.High,
            headline,
            "These are self-time shares: each sample is attributed to the innermost frame it was executing, so "
            + "the figures across all categories sum to exactly 100% of CPU samples. " + highestName + " covers "
            + highestMeaning + ". Every category in the CPU view can be expanded into the call paths that "
            + "produced it, which is where the actionable part is.",
            worstRatio,
            evidence,
            new List<string> { "Open the CPU Profile view and expand " + highestName + " to see its call paths." },
            links,
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class CpuSingleMethodRule : InsightRule
{
    // Calibrated against the 19 corpus captures with resolved CPU samples:
    // the hottest method's self share runs min 1.5%, median 26%, max 74%. A
    // 10% floor fired on 12 of 19 - on this population a single method at 10%
    // is ordinary. 15% sits below the median so the finding is still available
    // as context; the Warning at 30% is above it.
    public const double SelfPercentThreshold = 15.0;
    public const double WarningSelfPercentThreshold = 30.0;

    public override string Id => "cpu/single-method";

    public override string Category => "CPU";

    public override string Threshold => "a single method that is not a blocking primitive accounts for >= " + FormatPercent(SelfPercentThreshold) + " of CPU self time";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!CpuRuleGate.HasCpu(analysis))
        {
            return this.NotApplicable(CpuRuleGate.NoCpuSamples);
        }

        if (analysis.Cpu.HotMethods.Count == 0)
        {
            return this.NotApplicable("no method names were resolved for this capture's samples");
        }

        // A frame at ~100% is not a hot method - see
        // capture/collapsed-leaf-attribution, which reports why.
        if (analysis.Contents.CpuLeafAttributionCollapsed)
        {
            return this.NotApplicable("leaf attribution has collapsed onto " + analysis.Contents.CollapsedCpuLeafFrame + "; see capture/collapsed-leaf-attribution");
        }

        // The hottest method that is actually RUNNING. The CPU sample profiler
        // samples every thread, including the ones parked doing nothing, so on
        // a service with a large idle thread pool the top of the raw self-time
        // ranking is the pool's own park - measured at 68.11% on a real
        // 518-thread capture. Reporting that as "the hottest method" is not
        // just unhelpful, it is wrong about what the process is doing: those
        // samples represent no work at all. Idle-wait leaves are identified by
        // Cpu/CpuIdleWaitClassifier.cs, the same list the CPU view's own
        // idle/CPU-bound split uses, so the two agree about what counts as
        // idle.
        HotMethodRecord topMethod = null;
        double idleShare = 0;

        for (int methodIndex = 0; methodIndex < analysis.Cpu.HotMethods.Count; ++methodIndex)
        {
            HotMethodRecord candidate = analysis.Cpu.HotMethods[methodIndex];

            if (candidate.IsIdleWait)
            {
                idleShare += candidate.SelfPercent;
                continue;
            }

            if (topMethod == null)
            {
                topMethod = candidate;
            }
        }

        if (topMethod == null)
        {
            return this.NotApplicable("every sampled leaf in this capture is a blocking primitive - no thread was running managed or native work");
        }

        string measured = "hottest running method " + topMethod.Name + " at " + FormatPercent(topMethod.SelfPercent) + " self"
            + (idleShare > 0 ? " (" + FormatPercent(idleShare) + " of samples are threads parked in a blocking call)" : "");

        if (topMethod.SelfPercent < SelfPercentThreshold)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Method", topMethod.Name, 0));
        evidence.Add(new InsightEvidence("Self time", FormatPercent(topMethod.SelfPercent), topMethod.SelfPercent));
        evidence.Add(new InsightEvidence("Samples", FormatCount(topMethod.SelfSamples), topMethod.SelfSamples));

        int runnerUpRank = 2;

        for (int methodIndex = 0; methodIndex < analysis.Cpu.HotMethods.Count && runnerUpRank <= 4; ++methodIndex)
        {
            HotMethodRecord runnerUp = analysis.Cpu.HotMethods[methodIndex];

            if (runnerUp.IsIdleWait || ReferenceEquals(runnerUp, topMethod))
            {
                continue;
            }

            evidence.Add(new InsightEvidence("  #" + runnerUpRank, runnerUp.Name + " (" + FormatPercent(runnerUp.SelfPercent) + ")", runnerUp.SelfPercent));
            ++runnerUpRank;
        }

        if (idleShare > 0)
        {
            // Stated alongside, because it changes how the percentage above
            // should be read: 12% of all samples is a larger share of the
            // running ones when most threads were parked.
            evidence.Add(new InsightEvidence("Samples in blocking calls", FormatPercent(idleShare), idleShare));
        }

        return this.Fire(
            topMethod.SelfPercent >= WarningSelfPercentThreshold ? InsightSeverity.Warning : InsightSeverity.Info,
            InsightConfidence.High,
            topMethod.Name + " is " + FormatPercent(topMethod.SelfPercent) + " of CPU self time",
            "One method is executing for a large share of every CPU sample taken. Self time means the samples "
            + "landed in this method itself rather than in something it called, so this is where the instructions "
            + "are actually being retired. Expanding it in the CPU view shows who calls it, which is usually where "
            + "the fix is - calling it less often beats making it faster.",
            topMethod.SelfPercent / SelfPercentThreshold,
            evidence,
            new List<string> { "Expand " + topMethod.Name + " in the CPU Profile view to see its callers." },
            new List<InsightLink> { new InsightLink("profile", "CPU Profile view: " + topMethod.Name, "method", topMethod.Name) },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Names the packages whose debug symbols would fix the profile. This is the
// difference between a complaint and an action: "12% of this profile has no
// symbols" is the former, "4.20% of it is libcrypto.so.3" is the latter.
public sealed class CpuUnresolvedSymbolsRule : InsightRule
{
    public const double UnresolvedPercentThreshold = 5.0;

    public override string Id => "cpu/unresolved-symbols";

    public override string Category => "CPU";

    public override string Threshold => "unresolved frames account for >= " + FormatPercent(UnresolvedPercentThreshold) + " of CPU self time";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!CpuRuleGate.HasCpu(analysis))
        {
            return this.NotApplicable(CpuRuleGate.NoCpuSamples);
        }

        double unresolvedPercent = analysis.Cpu.UnresolvedPercent;

        string measured = FormatPercent(unresolvedPercent) + " of samples are in unresolved frames";

        if (unresolvedPercent < UnresolvedPercentThreshold)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Unresolved share", FormatPercent(unresolvedPercent), unresolvedPercent));

        double namedShare = 0;

        for (int moduleIndex = 0; moduleIndex < analysis.Cpu.UnresolvedModules.Count && moduleIndex < 5; ++moduleIndex)
        {
            UnresolvedModuleRecord module = analysis.Cpu.UnresolvedModules[moduleIndex];
            evidence.Add(new InsightEvidence(module.ModuleName, FormatPercent(module.SelfPercent), module.SelfPercent));
            namedShare += module.SelfPercent;
        }

        string leadModule = analysis.Cpu.UnresolvedModules.Count > 0
            ? analysis.Cpu.UnresolvedModules[0].ModuleName
            : "unknown modules";

        return this.Fire(
            InsightSeverity.Info,
            InsightConfidence.High,
            FormatPercent(unresolvedPercent) + " of CPU samples have no symbols, mostly " + leadModule,
            "These samples landed in modules whose debug symbols were not available, so they appear as "
            + "\"module+0xADDRESS\" rather than as function names and are counted as Unresolved rather than being "
            + "attributed to a category. That understates whichever categories those modules belong to. Frames "
            + "that are unresolvable for permanent reasons - runtime-generated stubs, the vDSO - are already "
            + "excluded from this figure, so what is left is genuinely fetchable.",
            unresolvedPercent / UnresolvedPercentThreshold,
            evidence,
            new List<string>
            {
                "Install the debug symbols for " + leadModule + " and reopen the capture.",
                "On Ubuntu, the matching *-dbgsym package (from ddebs) provides these; the symbol server is consulted automatically when downloads are enabled."
            },
            new List<InsightLink> { new InsightLink("profile", "CPU Profile view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights.Rules)
