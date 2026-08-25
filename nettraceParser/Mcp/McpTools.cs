////////////////////////////////////////////////////////////////////////////////
// Module: McpTools.cs
//
// Notes:
// The tools an agent can call, and their answers. Every one of them reads
// CaptureAnalysis (see Analysis/CaptureAnalysis.cs) and nothing else, which is
// what makes it impossible for a tool and an insight rule to disagree about a
// capture - they are reading the same fields.
//
// A QUESTION THE ANALYSIS CANNOT ANSWER GETS NO TOOL. There is deliberately no
// get_method_callers, no get_type_call_paths and no "what was thread X doing
// at time T": the caller trees and per-sample timeline live in the --json
// export, not in this model, and a tool that answered those from what IS here
// would be guessing. An agent that finds no tool for a question says so; an
// agent handed a tool that returns a confident wrong answer does not, and
// cannot be corrected by the person reading its output. The tool list names
// the views that do hold that detail instead.
//
// ANSWERS ARE JSON, not prose. A model reads these, and prose would have to be
// re-parsed to be acted on. The numbers carry their units in the field names
// for the same reason the text report spells them out.
//
// EVERY TOOL TAKES AN OPTIONAL `capture`. It defaults to the most recently
// opened one, so the common single-capture case needs no argument threading -
// but a session comparing two captures can name them explicitly rather than
// relying on order.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Mcp {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json;

using DotnetInsights.NetTrace.Analysis;
using DotnetInsights.NetTrace.Insights;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class McpToolDefinition
{
    public string Name = "";
    public string Description = "";

    // Written straight into tools/list as the tool's inputSchema. Hand-written
    // JSON Schema rather than generated: the descriptions in it are what a
    // model reads to decide whether a tool answers its question, and they are
    // worth writing deliberately.
    public Action<Utf8JsonWriter> WriteInputSchema = writer => { writer.WriteStartObject(); writer.WriteString("type", "object"); writer.WriteEndObject(); };
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class McpTools
{
    public const int DefaultLimit = 20;
    public const int MaximumLimit = 200;

    public static List<McpToolDefinition> AllTools()
    {
        List<McpToolDefinition> tools = new List<McpToolDefinition>();

        tools.Add(new McpToolDefinition
        {
            Name = "open_capture",
            Description =
                "Parse a .nettrace capture and make it available to the other tools. Returns a summary of what the capture contains. "
                + "Parsing a large capture takes several seconds the first time and is cached afterwards, so call this once per capture. "
                + "Every other tool defaults to the most recently opened capture.",
            WriteInputSchema = writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                WriteStringProperty(writer, "path", "Absolute path to a .nettrace file.");
                writer.WriteEndObject();
                writer.WritePropertyName("required");
                writer.WriteStartArray();
                writer.WriteStringValue("path");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        });

        tools.Add(Simple("list_open_captures", "List the captures already opened in this session, most recent first."));

        tools.Add(WithCapture(
            "get_capture_summary",
            "Overall shape of the capture: duration, event count, format version, and which classes of event it actually recorded. "
            + "Read this before drawing conclusions from an empty result elsewhere - a capture that did not record allocation events "
            + "is not a process that did not allocate."));

        tools.Add(new McpToolDefinition
        {
            Name = "get_insights",
            Description =
                "The ranked findings from the built-in rule engine - the best starting point for 'what is wrong with this capture'. "
                + "Each finding carries the measurement it fired on, the threshold, and suggested actions. "
                + "Also returns every rule that did NOT fire, with what it measured or why it had no data, so a silent area can be "
                + "distinguished from an unmeasured one.",
            WriteInputSchema = writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                WriteCaptureProperty(writer);
                WriteStringProperty(writer, "severity", "Minimum severity to return: info, warning or critical. Defaults to info (all).");
                WriteBooleanProperty(writer, "includeRules", "Include the full list of rules that did not fire. Defaults to true.");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        });

        tools.Add(WithCapture(
            "get_time_breakdown",
            "Where the capture's wall-clock and sampled thread time went: GC pause, lock contention, idle versus CPU-bound."));

        tools.Add(new McpToolDefinition
        {
            Name = "query_gcs",
            Description =
                "Individual garbage collections, newest-pause-first, with generation, reason, pause duration and heap sizes. "
                + "Filterable, because a capture can contain tens of thousands of them.",
            WriteInputSchema = writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                WriteCaptureProperty(writer);
                WriteIntegerProperty(writer, "generation", "Only collections of this generation (0, 1 or 2).");
                WriteNumberProperty(writer, "minPauseMSec", "Only collections whose pause was at least this many milliseconds.");
                WriteBooleanProperty(writer, "inducedOnly", "Only collections triggered by an explicit GC.Collect call.");
                WriteLimitProperty(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        });

        tools.Add(WithCapture(
            "get_gc_summary",
            "Aggregate GC statistics: counts and pause per generation, the worst single pause, induced collections, "
            + "Server GC heap balance, heap growth trend and fragmentation."));

        tools.Add(WithCapture(
            "get_cpu_categories",
            "CPU time bucketed into coarse categories (GC, allocation, JIT, TLS/crypto, locking, kernel, application code, ...). "
            + "selfPercent attributes each sample to its innermost frame and sums to 100%; onStackPercent counts a sample toward every "
            + "category on its stack and deliberately sums to more."));

        tools.Add(new McpToolDefinition
        {
            Name = "get_hot_methods",
            Description =
                "Methods ranked by CPU self time. Note that the sample profiler samples parked threads too, so blocking primitives "
                + "(the thread pool's park, semaphore waits) can dominate the raw ranking without representing any work - they are "
                + "flagged with isIdleWait and excluded by default.",
            WriteInputSchema = writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                WriteCaptureProperty(writer);
                WriteLimitProperty(writer);
                WriteBooleanProperty(writer, "includeIdleWait", "Include blocking primitives in the ranking. Defaults to false.");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        });

        tools.Add(new McpToolDefinition
        {
            Name = "get_threads",
            Description =
                "Threads classified by what they spent the capture doing, most actionable first. Roles are 'Active', "
                + "'Blocked pool worker' (the thread-pool starvation case), 'Blocked', 'Idle pool worker', 'Parked' and "
                + "'Runtime infrastructure'. Each carries the evidence behind its classification and its dominant stack.",
            WriteInputSchema = writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                WriteCaptureProperty(writer);
                WriteStringProperty(writer, "role", "Only threads with this role, matched case-insensitively on the role name.");
                WriteBooleanProperty(writer, "excludeBenign", "Omit threads parked by design. Defaults to false.");
                WriteLimitProperty(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        });

        tools.Add(WithCapture(
            "get_allocation_types",
            "Types ranked by sampled allocated bytes. These figures come from the runtime's allocation-tick sampling "
            + "(roughly one tick per 100KB allocated), so they estimate allocation volume rather than measure it.",
            includeLimit: true));

        tools.Add(WithCapture(
            "get_contention_sites",
            "Lock contention sites ranked by total wait time, with how many distinct threads waited at each. "
            + "Ranked by TIME rather than event count on purpose: many contentions that each resolve in microseconds are ambient and "
            + "explain nothing.",
            includeLimit: true));

        tools.Add(WithCapture(
            "get_exception_types",
            "Exception types ranked by how often they were thrown. These are first-chance throws - every exception raised, "
            + "including ones caught and handled immediately.",
            includeLimit: true));

        return tools;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Schema helpers
    ////////////////////////////////////////////////////////////////////////////

    private static McpToolDefinition Simple(string name, string description)
    {
        return new McpToolDefinition { Name = name, Description = description };
    }

    private static McpToolDefinition WithCapture(string name, string description, bool includeLimit = false)
    {
        return new McpToolDefinition
        {
            Name = name,
            Description = description,
            WriteInputSchema = writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                WriteCaptureProperty(writer);

                if (includeLimit)
                {
                    WriteLimitProperty(writer);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        };
    }

    private static void WriteCaptureProperty(Utf8JsonWriter writer)
    {
        WriteStringProperty(writer, "capture", "Path of an already-opened capture. Defaults to the most recently opened one.");
    }

    private static void WriteLimitProperty(Utf8JsonWriter writer)
    {
        WriteIntegerProperty(writer, "limit", "Maximum rows to return. Defaults to " + DefaultLimit + ", capped at " + MaximumLimit + ".");
    }

    private static void WriteStringProperty(Utf8JsonWriter writer, string name, string description)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteIntegerProperty(Utf8JsonWriter writer, string name, string description)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("type", "integer");
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteNumberProperty(Utf8JsonWriter writer, string name, string description)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("type", "number");
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteBooleanProperty(Utf8JsonWriter writer, string name, string description)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("type", "boolean");
        writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    public static int ClampLimit(int requested)
    {
        if (requested <= 0)
        {
            return DefaultLimit;
        }

        return requested > MaximumLimit ? MaximumLimit : requested;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Answers
    ////////////////////////////////////////////////////////////////////////////

    public static void WriteCaptureSummary(Utf8JsonWriter writer, CaptureAnalysis analysis)
    {
        writer.WriteStartObject();

        writer.WriteString("capture", analysis.SourcePath);
        writer.WriteString("processName", analysis.ProcessName);
        writer.WriteNumber("nettraceFormatVersion", analysis.FormatVersion);
        writer.WriteBoolean("hasCaptureDuration", analysis.HasCaptureDuration);
        writer.WriteNumber("captureDurationMSec", analysis.CaptureDurationMSec);
        writer.WriteNumber("totalEventCount", analysis.TotalEventCount);
        writer.WriteNumber("processorCount", analysis.NumberOfProcessors);

        writer.WritePropertyName("recorded");
        writer.WriteStartObject();
        writer.WriteBoolean("gcEvents", analysis.Contents.HasGcEvents);
        writer.WriteBoolean("allocationTicks", analysis.Contents.HasAllocationTicks);
        writer.WriteBoolean("cpuSamples", analysis.Contents.HasCpuSamples);
        writer.WriteBoolean("contentionEvents", analysis.Contents.HasContentionEvents);
        writer.WriteBoolean("exceptionEvents", analysis.Contents.HasExceptionEvents);
        writer.WriteBoolean("threadPoolEvents", analysis.Contents.HasThreadPoolEvents);
        writer.WriteEndObject();

        writer.WriteString("sampleTypeSource", analysis.Contents.SampleTypeSource);

        // Surfaced at the top level because it invalidates whole answers
        // rather than qualifying them - see CaptureContents.
        if (analysis.Contents.CpuLeafAttributionCollapsed || analysis.Contents.ContentionLeafAttributionCollapsed)
        {
            writer.WritePropertyName("warning");
            writer.WriteStartObject();
            writer.WriteString("kind", "collapsedLeafAttribution");
            writer.WriteString("detail",
                "Nearly every stack in this capture bottoms out in one frame, so rankings by innermost frame (hot methods, "
                + "CPU categories, contention sites) describe the tracing machinery rather than the process. Aggregate figures "
                + "such as GC pause, allocation volume and the thread-pool counters are unaffected.");

            if (analysis.Contents.CpuLeafAttributionCollapsed)
            {
                writer.WriteString("cpuLeafFrame", analysis.Contents.CollapsedCpuLeafFrame);
            }

            if (analysis.Contents.ContentionLeafAttributionCollapsed)
            {
                writer.WriteString("contentionLeafFrame", analysis.Contents.CollapsedContentionLeafFrame);
            }

            writer.WriteEndObject();
        }

        writer.WriteNumber("gcCount", analysis.Gc.Count);
        writer.WriteNumber("cpuSampleCount", analysis.Cpu.TotalSampleCount);
        writer.WriteNumber("threadCount", analysis.Threading.ThreadCount);
        writer.WriteNumber("exceptionCount", analysis.Exceptions.TotalCount);
        writer.WriteNumber("contentionCount", analysis.Contention.TotalContentionCount);
        writer.WriteNumber("allocationTickCount", analysis.Allocation.TotalTickCount);

        writer.WriteEndObject();
    }

    public static void WriteTimeBreakdown(Utf8JsonWriter writer, CaptureAnalysis analysis)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("hasCaptureDuration", analysis.TimeBreakdown.HasCaptureDuration);
        writer.WriteNumber("captureDurationMSec", analysis.TimeBreakdown.CaptureDurationMSec);
        writer.WriteNumber("gcPausePercentOfCapture", analysis.TimeBreakdown.GcPercent);
        writer.WriteNumber("gcPauseMSec", analysis.TimeBreakdown.GcPauseMSec);
        writer.WriteNumber("contentionPercentOfCapture", analysis.TimeBreakdown.ContentionPercent);
        writer.WriteNumber("contentionWaitMSec", analysis.TimeBreakdown.ContentionWaitMSec);
        writer.WriteNumber("averageThreadsBlocked", analysis.TimeBreakdown.AverageThreadsBlocked);
        writer.WriteBoolean("hasCpuSampleBreakdown", analysis.TimeBreakdown.HasCpuSampleBreakdown);
        writer.WriteNumber("idlePercentOfSamples", analysis.TimeBreakdown.IdlePercent);
        writer.WriteNumber("cpuBoundPercentOfSamples", analysis.TimeBreakdown.CpuBoundPercent);
        writer.WriteEndObject();
    }

    public static void WriteGcSummary(Utf8JsonWriter writer, CaptureAnalysis analysis)
    {
        GcAnalysis gc = analysis.Gc;

        writer.WriteStartObject();
        writer.WriteNumber("collectionCount", gc.Count);
        writer.WriteNumber("totalPauseMSec", gc.TotalPauseMSec);

        writer.WritePropertyName("byGeneration");
        writer.WriteStartArray();

        for (int generation = 0; generation < gc.CountByGeneration.Length; ++generation)
        {
            writer.WriteStartObject();
            writer.WriteNumber("generation", generation);
            writer.WriteNumber("count", gc.CountByGeneration[generation]);
            writer.WriteNumber("pauseMSec", gc.PauseByGeneration[generation]);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteNumber("maxPauseMSec", gc.MaxPauseMSec);
        writer.WriteNumber("maxPauseGcId", gc.MaxPauseGcId);
        writer.WriteNumber("maxPauseGeneration", gc.MaxPauseGeneration);
        writer.WriteString("maxPauseReason", gc.MaxPauseReason);

        writer.WriteNumber("inducedCount", gc.InducedCount);
        writer.WriteNumber("lohTriggeredCount", gc.LohTriggeredCount);
        writer.WriteNumber("blockingGen2Count", gc.BlockingGen2Count);
        writer.WriteNumber("blockingGen2PauseMSec", gc.BlockingGen2PauseMSec);
        writer.WriteNumber("backgroundGen2Count", gc.BackgroundGen2Count);

        writer.WriteBoolean("isServerGc", gc.IsServerGc);
        writer.WriteNumber("heapCount", gc.HeapCount);
        writer.WriteNumber("worstHeapPromotionSpread", gc.WorstHeapImbalance);
        writer.WriteNumber("worstHeapPromotionSpreadGcId", gc.WorstHeapImbalanceGcId);

        writer.WriteNumber("firstHeapSizeBytes", gc.FirstHeapSizeBytes);
        writer.WriteNumber("lastHeapSizeBytes", gc.LastHeapSizeBytes);
        writer.WriteNumber("peakHeapSizeBytes", gc.PeakHeapSizeBytes);
        writer.WriteBoolean("hasHeapGrowthFit", gc.HasHeapGrowthFit);
        writer.WriteNumber("heapGrowthBytesPerSecond", gc.HeapGrowthBytesPerSecond);
        writer.WriteNumber("heapGrowthRSquared", gc.HeapGrowthRSquared);

        writer.WriteBoolean("hasFragmentation", gc.HasFragmentation);
        writer.WriteNumber("finalFragmentationBytes", gc.FinalFragmentationBytes);
        writer.WriteNumber("finalGen2AndLohBytes", gc.FinalGen2AndLohBytes);

        writer.WriteEndObject();
    }

    public static void WriteGcs(Utf8JsonWriter writer, CaptureAnalysis analysis, int generation, double minPauseMSec, bool inducedOnly, int limit)
    {
        List<GcRecord> matching = new List<GcRecord>();

        for (int gcIndex = 0; gcIndex < analysis.Gc.Gcs.Count; ++gcIndex)
        {
            GcRecord record = analysis.Gc.Gcs[gcIndex];

            if (generation >= 0 && record.Generation != generation)
            {
                continue;
            }

            if (record.PauseDurationMSec < minPauseMSec)
            {
                continue;
            }

            if (inducedOnly && !record.IsInduced)
            {
                continue;
            }

            matching.Add(record);
        }

        // Longest pause first: with tens of thousands of collections and a
        // capped result, the expensive ones are what a caller almost always
        // wants, and returning the first N chronologically would silently
        // answer a different question.
        matching.Sort((GcRecord left, GcRecord right) =>
        {
            int byPause = right.PauseDurationMSec.CompareTo(left.PauseDurationMSec);
            return byPause != 0 ? byPause : left.Id.CompareTo(right.Id);
        });

        writer.WriteStartObject();
        writer.WriteNumber("matchingCount", matching.Count);
        writer.WriteNumber("returnedCount", Math.Min(matching.Count, limit));
        writer.WriteString("orderedBy", "pauseDurationMSec descending");

        writer.WritePropertyName("collections");
        writer.WriteStartArray();

        for (int rankIndex = 0; rankIndex < matching.Count && rankIndex < limit; ++rankIndex)
        {
            GcRecord record = matching[rankIndex];

            writer.WriteStartObject();
            writer.WriteNumber("id", record.Id);
            writer.WriteNumber("generation", record.Generation);
            writer.WriteString("reason", record.Reason);
            writer.WriteString("type", record.Type);
            writer.WriteBoolean("isInduced", record.IsInduced);
            writer.WriteBoolean("isBackground", record.IsBackground);
            writer.WriteNumber("pauseDurationMSec", record.PauseDurationMSec);
            writer.WriteNumber("pauseStartRelativeMSec", record.PauseStartRelativeMSec);
            writer.WriteNumber("totalHeapSizeBytes", record.TotalHeapSize);
            writer.WriteNumber("totalPromotedBytes", record.TotalPromoted);
            writer.WriteNumber("gen0SizeBytes", record.GenerationSize0);
            writer.WriteNumber("gen1SizeBytes", record.GenerationSize1);
            writer.WriteNumber("gen2SizeBytes", record.GenerationSize2);
            writer.WriteNumber("lohSizeBytes", record.GenerationSizeLOH);
            writer.WriteNumber("heapCount", record.NumHeaps);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void WriteCpuCategories(Utf8JsonWriter writer, CaptureAnalysis analysis)
    {
        writer.WriteStartObject();
        writer.WriteNumber("totalSampleCount", analysis.Cpu.TotalSampleCount);
        writer.WriteNumber("unresolvedPercent", analysis.Cpu.UnresolvedPercent);

        writer.WritePropertyName("categories");
        writer.WriteStartArray();

        for (int categoryIndex = 0; categoryIndex < analysis.Cpu.Categories.Count; ++categoryIndex)
        {
            CpuCategoryRecord record = analysis.Cpu.Categories[categoryIndex];

            writer.WriteStartObject();
            writer.WriteString("name", record.Name);
            writer.WriteNumber("selfSamples", record.SelfSamples);
            writer.WriteNumber("selfPercent", record.SelfPercent);
            writer.WriteNumber("onStackPercent", record.OnStackPercent);

            writer.WritePropertyName("topMethods");
            writer.WriteStartArray();

            for (int methodIndex = 0; methodIndex < record.TopMethods.Count; ++methodIndex)
            {
                writer.WriteStartObject();
                writer.WriteString("name", record.TopMethods[methodIndex].Name);
                writer.WriteNumber("selfPercent", record.TopMethods[methodIndex].SelfPercent);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("unresolvedModules");
        writer.WriteStartArray();

        for (int moduleIndex = 0; moduleIndex < analysis.Cpu.UnresolvedModules.Count; ++moduleIndex)
        {
            writer.WriteStartObject();
            writer.WriteString("module", analysis.Cpu.UnresolvedModules[moduleIndex].ModuleName);
            writer.WriteNumber("selfPercent", analysis.Cpu.UnresolvedModules[moduleIndex].SelfPercent);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void WriteHotMethods(Utf8JsonWriter writer, CaptureAnalysis analysis, int limit, bool includeIdleWait)
    {
        writer.WriteStartObject();
        writer.WriteNumber("totalSampleCount", analysis.Cpu.TotalSampleCount);
        writer.WriteBoolean("includesBlockingPrimitives", includeIdleWait);

        double idleShare = 0;

        for (int methodIndex = 0; methodIndex < analysis.Cpu.HotMethods.Count; ++methodIndex)
        {
            if (analysis.Cpu.HotMethods[methodIndex].IsIdleWait)
            {
                idleShare += analysis.Cpu.HotMethods[methodIndex].SelfPercent;
            }
        }

        // Always reported, even when the blocking frames are excluded from the
        // list: it changes how the remaining percentages should be read. 12% of
        // all samples is a much larger share of the RUNNING ones when 85% of
        // threads were parked.
        writer.WriteNumber("percentOfSamplesInBlockingPrimitives", idleShare);

        writer.WritePropertyName("methods");
        writer.WriteStartArray();

        int emitted = 0;

        for (int methodIndex = 0; methodIndex < analysis.Cpu.HotMethods.Count && emitted < limit; ++methodIndex)
        {
            HotMethodRecord record = analysis.Cpu.HotMethods[methodIndex];

            if (record.IsIdleWait && !includeIdleWait)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("name", record.Name);
            writer.WriteNumber("selfSamples", record.SelfSamples);
            writer.WriteNumber("selfPercent", record.SelfPercent);
            writer.WriteBoolean("isIdleWait", record.IsIdleWait);
            writer.WriteEndObject();

            ++emitted;
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void WriteThreads(Utf8JsonWriter writer, CaptureAnalysis analysis, string role, bool excludeBenign, int limit)
    {
        writer.WriteStartObject();
        writer.WriteNumber("threadCount", analysis.Threading.ThreadCount);
        writer.WriteBoolean("hasSampleTypeData", analysis.Threading.HasSampleTypeData);
        writer.WriteString("sampleTypeSource", analysis.Contents.SampleTypeSource);

        writer.WritePropertyName("roleCounts");
        writer.WriteStartObject();
        writer.WriteNumber("active", analysis.Threading.ActiveThreadCount);
        writer.WriteNumber("blockedPoolWorker", analysis.Threading.BlockedPoolWorkerCount);
        writer.WriteNumber("blocked", analysis.Threading.BlockedThreadCount);
        writer.WriteNumber("idlePoolWorker", analysis.Threading.IdlePoolWorkerCount);
        writer.WriteNumber("benignlyParked", analysis.Threading.BenignlyParkedThreadCount);
        writer.WriteEndObject();

        writer.WriteNumber("stallDrivenAdjustmentCount", analysis.Threading.StallDrivenAdjustmentCount);
        writer.WriteNumber("peakActiveWorkerThreads", analysis.Threading.PeakActiveWorkerThreads);
        writer.WriteNumber("minActiveWorkerThreads", analysis.Threading.MinActiveWorkerThreads);

        writer.WritePropertyName("threads");
        writer.WriteStartArray();

        int emitted = 0;

        for (int threadIndex = 0; threadIndex < analysis.Threading.Threads.Count && emitted < limit; ++threadIndex)
        {
            ThreadRecord record = analysis.Threading.Threads[threadIndex];

            if (role != null && !string.Equals(record.Role, role, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (excludeBenign && record.IsBenignlyParked)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteNumber("threadId", record.ThreadId);
            writer.WriteString("role", record.Role);
            writer.WriteBoolean("isBenignlyParked", record.IsBenignlyParked);
            writer.WriteBoolean("isPoolWorker", record.IsPoolWorker);
            writer.WriteNumber("sampleCount", record.SampleCount);
            writer.WriteNumber("managedFraction", record.ManagedFraction);
            writer.WriteNumber("waitFraction", record.WaitFraction);
            writer.WriteNumber("poolParkFraction", record.PoolParkFraction);
            writer.WriteNumber("contentionCount", record.ContentionCount);
            writer.WriteNumber("contentionWaitMSec", record.ContentionWaitMSec);
            writer.WriteNumber("contentionShareOfLife", record.ContentionShareOfLife);
            writer.WriteNumber("longestContinuousIdleMSec", record.LongestContinuousIdleMSec);

            writer.WritePropertyName("topStacks");
            writer.WriteStartArray();

            for (int stackRank = 0; stackRank < record.TopStacks.Count; ++stackRank)
            {
                ThreadStackRecord stack = record.TopStacks[stackRank];

                writer.WriteStartObject();
                writer.WriteNumber("shareOfThreadSamples", stack.Share);

                writer.WritePropertyName("frames");
                writer.WriteStartArray();

                for (int frameIndex = 0; frameIndex < stack.Frames.Count; ++frameIndex)
                {
                    writer.WriteStringValue(stack.Frames[frameIndex]);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            ++emitted;
        }

        writer.WriteEndArray();
        writer.WriteNumber("returnedCount", emitted);
        writer.WriteEndObject();
    }

    public static void WriteAllocationTypes(Utf8JsonWriter writer, CaptureAnalysis analysis, int limit)
    {
        writer.WriteStartObject();
        writer.WriteString("note", "Byte figures are SAMPLED - the runtime emits roughly one allocation tick per 100KB allocated.");
        writer.WriteNumber("totalSampledBytes", analysis.Allocation.TotalSampledBytes);
        writer.WriteNumber("totalTickCount", analysis.Allocation.TotalTickCount);
        writer.WriteNumber("distinctTypeCount", analysis.Allocation.DistinctTypeCount);
        writer.WriteNumber("largeObjectSampledBytes", analysis.Allocation.LargeObjectSampledBytes);

        writer.WritePropertyName("types");
        writer.WriteStartArray();

        for (int typeIndex = 0; typeIndex < analysis.Allocation.TopTypes.Count && typeIndex < limit; ++typeIndex)
        {
            AllocatedTypeRecord record = analysis.Allocation.TopTypes[typeIndex];

            writer.WriteStartObject();
            writer.WriteString("typeName", record.TypeName);
            writer.WriteNumber("sampledBytes", record.TotalBytes);
            writer.WriteNumber("percentOfSampledBytes", record.PercentOfTotalBytes);
            writer.WriteNumber("tickCount", record.TickCount);
            writer.WriteNumber("largeObjectTickCount", record.LargeCount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void WriteContentionSites(Utf8JsonWriter writer, CaptureAnalysis analysis, int limit)
    {
        writer.WriteStartObject();
        writer.WriteNumber("totalContentionCount", analysis.Contention.TotalContentionCount);
        writer.WriteNumber("totalWaitMSec", analysis.Contention.TotalWaitMSec);
        writer.WriteNumber("distinctSiteCount", analysis.Contention.DistinctSiteCount);

        writer.WritePropertyName("sites");
        writer.WriteStartArray();

        for (int siteIndex = 0; siteIndex < analysis.Contention.TopSites.Count && siteIndex < limit; ++siteIndex)
        {
            ContentionSiteRecord record = analysis.Contention.TopSites[siteIndex];

            writer.WriteStartObject();
            writer.WriteString("siteName", record.SiteName);
            writer.WriteNumber("totalWaitMSec", record.TotalWaitMSec);
            writer.WriteNumber("percentOfTotalWait", record.PercentOfTotalWait);
            writer.WriteNumber("contentionCount", record.ContentionCount);
            writer.WriteNumber("averageWaitMSec", record.AverageWaitMSec);
            writer.WriteNumber("maxWaitMSec", record.MaxWaitMSec);
            writer.WriteNumber("waiterThreadCount", record.WaiterThreadCount);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void WriteExceptionTypes(Utf8JsonWriter writer, CaptureAnalysis analysis, int limit)
    {
        writer.WriteStartObject();
        writer.WriteString("note", "First-chance throws: every exception raised, including ones caught and handled immediately.");
        writer.WriteNumber("totalCount", analysis.Exceptions.TotalCount);
        writer.WriteNumber("distinctTypeCount", analysis.Exceptions.DistinctTypeCount);

        writer.WritePropertyName("types");
        writer.WriteStartArray();

        for (int typeIndex = 0; typeIndex < analysis.Exceptions.TopTypes.Count && typeIndex < limit; ++typeIndex)
        {
            ExceptionTypeRecord record = analysis.Exceptions.TopTypes[typeIndex];

            writer.WriteStartObject();
            writer.WriteString("typeName", record.TypeName);
            writer.WriteNumber("count", record.Count);
            writer.WriteNumber("percentOfTotal", record.PercentOfTotal);
            writer.WriteString("sampleMessage", record.SampleMessage);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static void WriteInsights(Utf8JsonWriter writer, InsightReport report, InsightSeverity minimumSeverity, bool includeRules)
    {
        writer.WriteStartObject();
        writer.WriteNumber("firedCount", report.FiredCount);
        writer.WriteNumber("criticalCount", report.CriticalCount);
        writer.WriteNumber("warningCount", report.WarningCount);
        writer.WriteNumber("infoCount", report.InfoCount);

        writer.WritePropertyName("insights");
        writer.WriteStartArray();

        for (int insightIndex = 0; insightIndex < report.Insights.Count; ++insightIndex)
        {
            Insight insight = report.Insights[insightIndex];

            if (insight.Severity < minimumSeverity)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("id", insight.Id);
            writer.WriteString("category", insight.Category);
            writer.WriteString("severity", InsightReportText.SeverityLabel(insight.Severity).ToLowerInvariant());
            writer.WriteString("confidence", InsightReportText.ConfidenceLabel(insight.Confidence));
            writer.WriteString("headline", insight.Headline);
            writer.WriteString("firesWhen", insight.Threshold);
            writer.WriteString("detail", insight.Detail);

            writer.WritePropertyName("evidence");
            writer.WriteStartObject();

            for (int evidenceIndex = 0; evidenceIndex < insight.Evidence.Count; ++evidenceIndex)
            {
                InsightEvidence evidence = insight.Evidence[evidenceIndex];
                writer.WriteString(evidence.Label.Trim(), evidence.Value);
            }

            writer.WriteEndObject();

            writer.WritePropertyName("actions");
            writer.WriteStartArray();

            for (int actionIndex = 0; actionIndex < insight.Actions.Count; ++actionIndex)
            {
                writer.WriteStringValue(insight.Actions[actionIndex]);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // The rules that did NOT fire, with what they measured. This is not
        // padding: without it an agent cannot tell "the rule ran and this
        // capture is clean" from "the rule had no data", and will confidently
        // report the first when the truth is the second.
        if (includeRules)
        {
            writer.WritePropertyName("rulesEvaluated");
            writer.WriteStartArray();

            for (int resultIndex = 0; resultIndex < report.Results.Count; ++resultIndex)
            {
                InsightRuleResult result = report.Results[resultIndex];

                writer.WriteStartObject();
                writer.WriteString("id", result.RuleId);
                writer.WriteString("outcome", InsightReportJson.OutcomeName(result.Outcome));
                writer.WriteString("firesWhen", result.Threshold);

                if (result.Outcome == InsightRuleOutcome.NotApplicable)
                {
                    writer.WriteString("noDataBecause", result.NotApplicableReason);
                }
                else
                {
                    writer.WriteString("measured", result.Measured);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Mcp)
