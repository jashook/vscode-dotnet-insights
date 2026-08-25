////////////////////////////////////////////////////////////////////////////////
// Module: CaptureQualityRules.cs
//
// Notes:
// The four rules that describe the CAPTURE rather than the process, and they
// run first because they are what tells a reader whether to trust the other
// twenty-one.
//
// This file exists because of a specific, repeatable way for a tool like this
// to be badly wrong. A `dotnet-trace collect-linux` capture contains no
// GCAllocationTick events and no contention events AT ALL - not because the
// process allocated nothing and contended for nothing, but because those
// keywords were not collected (see CLAUDE.md, "What is and is not in a
// collect-linux capture"). Every allocation and contention rule correctly
// declines to fire on such a capture, and the report would then read as a
// clean bill of health for two whole subsystems nobody measured.
// capture/missing-providers is what says so out loud.
//
// The same applies to the threading classification. Its parked/blocked
// thresholds were calibrated against the runtime's own ThreadSampleType, and
// a perf-sampled v6 capture has none - the signal is DERIVED from whether
// each sample's leaf frame resolved to managed code. That derivation is
// sound and is not the leaf-name heuristic ThreadActivityProfiler's header
// warns about, but it has not been validated against a v5 capture of the same
// process, and on the reference capture it runs managedFraction ~0.53 where
// v5 parked threads sit at 0.0-0.021. capture/derived-sample-type carries
// that caveat into the report rather than letting the roles read as equally
// trustworthy.
//
// All four are Info severity by design. None of them is a problem with the
// PROCESS, and promoting a capture caveat to Warning would push a real
// finding down the ranking to make room for a note about how the data was
// collected.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Insights.Rules {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

using DotnetInsights.NetTrace.Analysis;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A capture too short, or containing too few collections, for the rate and
// share rules downstream to mean much. Those rules still run - this one is a
// caveat on their output, not a gate on it, because "too short to be sure" is
// a judgement the reader should make with the numbers in front of them.
public sealed class CaptureShortRule : InsightRule
{
    public const double MinimumUsefulDurationMSec = 10_000.0;
    public const int MinimumUsefulGcCount = 5;

    public override string Id => "capture/short";

    public override string Category => "Capture quality";

    public override string Threshold =>
        "capture duration < " + FormatDuration(MinimumUsefulDurationMSec)
        + ", or fewer than " + MinimumUsefulGcCount + " GCs when GC events were collected";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!analysis.HasCaptureDuration)
        {
            List<InsightEvidence> noDurationEvidence = EvidenceList();
            noDurationEvidence.Add(new InsightEvidence("Events", FormatCount(analysis.TotalEventCount), analysis.TotalEventCount));

            return this.Fire(
                InsightSeverity.Info,
                InsightConfidence.High,
                "This capture has no measurable time span",
                "No two events in this capture carried timestamps far enough apart to establish a duration. "
                + "Every rate and percent-of-capture figure in this report is therefore unavailable, and the "
                + "rules that depend on one have declined rather than divided by zero.",
                1.0,
                noDurationEvidence,
                new List<string> { "Re-capture with a longer collection window." },
                null,
                "no capture duration could be established");
        }

        bool tooShort = analysis.CaptureDurationMSec < MinimumUsefulDurationMSec;
        bool tooFewGcs = analysis.Contents.HasGcEvents && analysis.Gc.Count < MinimumUsefulGcCount;

        string measured = "duration " + FormatDuration(analysis.CaptureDurationMSec)
            + ", " + FormatCount(analysis.Gc.Count) + " GCs";

        if (!tooShort && !tooFewGcs)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Capture duration", FormatDuration(analysis.CaptureDurationMSec), analysis.CaptureDurationMSec));
        evidence.Add(new InsightEvidence("Completed GCs", FormatCount(analysis.Gc.Count), analysis.Gc.Count));
        evidence.Add(new InsightEvidence("Events", FormatCount(analysis.TotalEventCount), analysis.TotalEventCount));

        string headline = tooShort
            ? "Short capture (" + FormatDuration(analysis.CaptureDurationMSec) + ") - treat rates below as provisional"
            : "Only " + FormatCount(analysis.Gc.Count) + " GCs in this capture - GC findings below are provisional";

        return this.Fire(
            InsightSeverity.Info,
            InsightConfidence.High,
            headline,
            "A short window sees whatever the process happened to be doing during it. A single slow request, "
            + "a warm-up, or one unlucky collection can dominate every percentage in this report. The findings "
            + "below are still computed from real events - they just describe a small sample of the process's life.",
            1.0,
            evidence,
            new List<string> { "Capture for at least 30 seconds of representative load before acting on rate-based findings." },
            null,
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Which analyses had no data to work with, stated as a property of the
// CAPTURE. See this file's header for why this rule is the most important one
// here.
public sealed class CaptureMissingProvidersRule : InsightRule
{
    public override string Id => "capture/missing-providers";

    public override string Category => "Capture quality";

    public override string Threshold => "any of GC, allocation, CPU sample, contention, exception or thread-pool events absent from the capture";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        List<string> missing = new List<string>();
        List<string> present = new List<string>();

        Classify(analysis.Contents.HasGcEvents, "GC", missing, present);
        Classify(analysis.Contents.HasAllocationTicks, "Allocation ticks", missing, present);
        Classify(analysis.Contents.HasCpuSamples, "CPU samples", missing, present);
        Classify(analysis.Contents.HasContentionEvents, "Lock contention", missing, present);
        Classify(analysis.Contents.HasExceptionEvents, "Exceptions", missing, present);
        Classify(analysis.Contents.HasThreadPoolEvents, "Thread pool", missing, present);

        string measured = missing.Count == 0
            ? "all six event classes present"
            : missing.Count + " of 6 event classes absent: " + string.Join(", ", missing);

        if (missing.Count == 0)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();

        for (int missingIndex = 0; missingIndex < missing.Count; ++missingIndex)
        {
            evidence.Add(new InsightEvidence(missing[missingIndex], "not collected", 0));
        }

        return this.Fire(
            InsightSeverity.Info,
            InsightConfidence.High,
            missing.Count + " of 6 event classes were not collected: " + string.Join(", ", missing),
            "These are absent from the capture, which is not the same as the process having none. "
            + "No rule in this report can say anything about them, and the corresponding views will be empty. "
            + "This is normal for a capture taken with a narrower keyword set - a `dotnet-trace collect-linux` "
            + "capture, for instance, carries no allocation ticks and no contention events by default.",
            1.0 + missing.Count,
            evidence,
            new List<string>
            {
                "Re-capture with the keywords for the missing classes if you need findings about them.",
                "Do not read the absence of findings in those areas as an absence of problems."
            },
            null,
            measured);
    }

    private static void Classify(bool present, string name, List<string> missingNames, List<string> presentNames)
    {
        if (present)
        {
            presentNames.Add(name);
        }
        else
        {
            missingNames.Add(name);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class CaptureDerivedSampleTypeRule : InsightRule
{
    public override string Id => "capture/derived-sample-type";

    public override string Category => "Capture quality";

    public override string Threshold => "CPU samples carry no runtime ThreadSampleType, so managed/native was derived from symbol resolution";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!analysis.Contents.HasCpuSamples)
        {
            return this.NotApplicable("this capture recorded no CPU samples");
        }

        if (analysis.Contents.SampleTypeSource != "derived")
        {
            return this.BelowThreshold("sample type source is \"" + analysis.Contents.SampleTypeSource + "\"");
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Sample type source", "derived", 0));
        evidence.Add(new InsightEvidence("Threads classified", FormatCount(analysis.Threading.ThreadCount), analysis.Threading.ThreadCount));
        evidence.Add(new InsightEvidence("CPU samples", FormatCount(analysis.Cpu.TotalSampleCount), analysis.Cpu.TotalSampleCount));

        return this.Fire(
            InsightSeverity.Info,
            InsightConfidence.High,
            "Thread roles are derived, not read from the runtime - treat threading findings as unvalidated here",
            "This capture's samples carry no ThreadSampleType, so whether a thread was running managed code was "
            + "derived from whether each sample's leaf frame resolved to managed code. That is a real measurement "
            + "of the sampled instruction pointer, not a guess about what a method does - but the parked/blocked "
            + "thresholds were calibrated against the runtime's own signal and have not been validated against "
            + "this one. Threads here are more likely to be reported as Active than they would be from an "
            + "equivalent capture that carried the runtime's signal.",
            1.0,
            evidence,
            new List<string> { "For a threading investigation, prefer a capture that includes the CLR's own sample profiler." },
            new List<InsightLink> { new InsightLink("threading", "Threading view") },
            "sample type source is \"derived\"");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A mostly-idle process makes every CPU percentage in the report a share of
// very little. The percentages are not wrong; they just answer "of the CPU
// time that was spent, where did it go" rather than "is this process busy",
// and those read identically on the page.
public sealed class CaptureMostlyIdleRule : InsightRule
{
    public const double IdlePercentThreshold = 80.0;

    public override string Id => "capture/mostly-idle";

    public override string Category => "Capture quality";

    public override string Threshold => "idle share of CPU samples >= " + FormatPercent(IdlePercentThreshold);

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        if (!analysis.Contents.HasCpuSamples || !analysis.TimeBreakdown.HasCpuSampleBreakdown)
        {
            return this.NotApplicable("this capture has no CPU sample breakdown");
        }

        double idlePercent = analysis.TimeBreakdown.IdlePercent;
        string measured = "idle " + FormatPercent(idlePercent) + ", CPU-bound " + FormatPercent(analysis.TimeBreakdown.CpuBoundPercent);

        if (idlePercent < IdlePercentThreshold)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();
        evidence.Add(new InsightEvidence("Idle / waiting", FormatPercent(idlePercent), idlePercent));
        evidence.Add(new InsightEvidence("CPU bound", FormatPercent(analysis.TimeBreakdown.CpuBoundPercent), analysis.TimeBreakdown.CpuBoundPercent));
        evidence.Add(new InsightEvidence("CPU samples", FormatCount(analysis.Cpu.TotalSampleCount), analysis.Cpu.TotalSampleCount));

        return this.Fire(
            InsightSeverity.Info,
            InsightConfidence.High,
            FormatPercent(idlePercent) + " of sampled thread time was idle or waiting",
            "Most of this process's sampled time was spent blocked, not running. The CPU percentages elsewhere "
            + "in this report are shares of the remaining " + FormatPercent(analysis.TimeBreakdown.CpuBoundPercent)
            + " - a method at \"10% of CPU\" is 10% of a small number. If the complaint is latency rather than "
            + "CPU cost, the Threading and Contention views are the ones that will explain it.",
            idlePercent / IdlePercentThreshold,
            evidence,
            new List<string> { "Read CPU percentages as shares of CPU-bound time, not of wall clock." },
            new List<InsightLink> { new InsightLink("threading", "Threading view") },
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A single leaf frame owning essentially every sample, or essentially all lock
// wait. See CaptureContents for the real capture this was found on: a
// collect-linux capture whose CPU samples and contention events all carried
// the stack of the KERNEL function that records events, producing a confident
// "user_events_write_core.isra.0 is 100% of CPU" and an equally confident "one
// lock is serializing 181 threads". Both numbers were arithmetically correct
// and neither described the process.
//
// This is a Warning rather than Info, unlike the other capture-quality rules,
// because it is not a caveat on findings - it invalidates them. The rules that
// rank by leaf frame decline outright when it fires, and this is what explains
// their absence.
public sealed class CaptureCollapsedLeafAttributionRule : InsightRule
{
    public override string Id => "capture/collapsed-leaf-attribution";

    public override string Category => "Capture quality";

    public override string Threshold => "a single leaf frame accounts for >= 95% of CPU self samples, or >= 95% of all lock wait";

    public override InsightRuleResult Evaluate(CaptureAnalysis analysis)
    {
        CaptureContents contents = analysis.Contents;

        bool cpuCollapsed = contents.CpuLeafAttributionCollapsed;
        bool contentionCollapsed = contents.ContentionLeafAttributionCollapsed;

        if (!contents.HasCpuSamples && !contents.HasContentionEvents)
        {
            return this.NotApplicable("this capture has neither CPU samples nor contention events to attribute");
        }

        string measured = (contents.HasCpuSamples
                ? "top CPU leaf " + FormatPercent(analysis.Cpu.HotMethods.Count > 0 ? analysis.Cpu.HotMethods[0].SelfPercent : 0)
                : "no CPU samples")
            + ", " + (contents.HasContentionEvents
                ? "top contention leaf " + FormatPercent(analysis.Contention.TopSites.Count > 0 ? analysis.Contention.TopSites[0].PercentOfTotalWait : 0)
                : "no contention events");

        if (!cpuCollapsed && !contentionCollapsed)
        {
            return this.BelowThreshold(measured);
        }

        List<InsightEvidence> evidence = EvidenceList();

        if (cpuCollapsed)
        {
            evidence.Add(new InsightEvidence("CPU self time", contents.CollapsedCpuLeafFrame + " at " + FormatPercent(contents.CollapsedCpuLeafShare), contents.CollapsedCpuLeafShare));
        }

        if (contentionCollapsed)
        {
            evidence.Add(new InsightEvidence("Lock wait", contents.CollapsedContentionLeafFrame + " at " + FormatPercent(contents.CollapsedContentionLeafShare), contents.CollapsedContentionLeafShare));
        }

        string collapsedFrame = cpuCollapsed ? contents.CollapsedCpuLeafFrame : contents.CollapsedContentionLeafFrame;

        return this.Fire(
            InsightSeverity.Warning,
            InsightConfidence.High,
            "Stack attribution has collapsed onto one frame (" + collapsedFrame + ") - leaf-ranked findings are suppressed",
            "Essentially every stack in this signal bottoms out in the same function, which is not what a hot "
            + "method or a hot lock looks like. It means the stacks recorded here are not the stacks of the work "
            + "being measured. The usual cause is that each event carried the stack of whatever RECORDED it "
            + "rather than the stack of the thing being recorded, which is what happens when the unwind starts "
            + "inside the tracing machinery. Any ranking by innermost frame in this capture - hot methods, CPU "
            + "categories, contention sites - is describing that machinery. Aggregate figures that do not depend "
            + "on the leaf, such as GC pause, allocation volume and the thread-pool counters, are unaffected.",
            2.0,
            evidence,
            new List<string>
            {
                "Treat the CPU and Contention views for this capture as unreliable; the GC, allocation and thread-pool numbers are still sound.",
                "Re-capture with the CLR's own sample profiler if CPU attribution is what you need."
            },
            null,
            measured);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Insights.Rules)
