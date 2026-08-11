////////////////////////////////////////////////////////////////////////////////
// Module: CaptureDiffJsonExporter.cs
//
// Notes:
// Writes the "--diff" payload the extension's diff webview consumes.
//
// This payload is deliberately small - a few hundred named rows per dimension
// rather than the millions of events behind them, and no allocation ticks, no
// flame trees and no per-lock segments. That is the whole point of computing
// the diff in this process: a single capture's own --json output is already
// ~53MB, and NettraceJsonStreamReader.ts documents a past incident where an
// oversized payload hit V8's ~537M-character string ceiling and surfaced to
// the user as a bogus "corrupted file" error. Two of those could never be
// shipped to a webview, so only the comparison ever crosses the boundary.
//
// Written straight into a Utf8JsonWriter rather than through a JsonNode tree,
// per CLAUDE.md's serialization rule.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Diff {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class CaptureDiffJsonExporter
{
    private const int OutputFileStreamBufferSize = 1024 * 1024;

    public static void WriteToFile(string outputPath, CaptureDiff diff)
    {
        using (FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, OutputFileStreamBufferSize))
        using (Utf8JsonWriter writer = new Utf8JsonWriter(fileStream))
        {
            Write(writer, diff);
        }
    }

    public static void Write(Utf8JsonWriter writer, CaptureDiff diff)
    {
        writer.WriteStartObject();

        // Marks this payload as a diff rather than a single capture. The
        // extension reads one field to decide which renderer to use, instead
        // of inferring it from which keys happen to be present.
        writer.WriteString("payloadKind", "nettraceDiff");

        writer.WritePropertyName("baseline");
        WriteCaptureSummary(writer, diff.Baseline);

        writer.WritePropertyName("comparison");
        WriteCaptureSummary(writer, diff.Comparison);

        WriteCoverage(writer, diff);

        WriteRows(writer, "eventTypes", diff.EventTypes);
        WriteRows(writer, "allocationTypes", diff.AllocationTypes);
        WriteRows(writer, "exceptionTypes", diff.ExceptionTypes);
        WriteRows(writer, "cpuMethods", diff.CpuMethods);
        WriteRows(writer, "contentionSites", diff.ContentionSites);
        WriteRows(writer, "locks", diff.Locks);

        writer.WriteEndObject();
    }

    // Per-dimension "did this capture record this at all" flags.
    //
    // Without these a dimension the baseline never enabled is indistinguishable
    // from one that genuinely went from nothing to something: every row lands
    // as "added", which reads as a regression that appeared. Two real captures
    // of the same service hit exactly this - the older one was taken without
    // the CPU-sampling and contention providers, so a truthful row-level diff
    // reported all 54 contention sites as new. A dimension with no baseline
    // data is not comparable, and the UI has to be able to say so instead of
    // implying a change that was never measured.
    private static void WriteCoverage(Utf8JsonWriter writer, CaptureDiff diff)
    {
        writer.WritePropertyName("coverage");
        writer.WriteStartObject();

        WriteCoverageEntry(writer, "gc", diff.Baseline.TotalGcCount > 0, diff.Comparison.TotalGcCount > 0);
        WriteCoverageEntry(writer, "allocations", diff.Baseline.TotalAllocationTickCount > 0, diff.Comparison.TotalAllocationTickCount > 0);
        WriteCoverageEntry(writer, "exceptions", diff.Baseline.TotalExceptionCount > 0, diff.Comparison.TotalExceptionCount > 0);
        WriteCoverageEntry(writer, "cpu", diff.Baseline.TotalCpuSampleCount > 0, diff.Comparison.TotalCpuSampleCount > 0);
        WriteCoverageEntry(writer, "contention", diff.Baseline.TotalContentionCount > 0, diff.Comparison.TotalContentionCount > 0);

        writer.WriteEndObject();
    }

    private static void WriteCoverageEntry(Utf8JsonWriter writer, string name, bool baselineHasData, bool comparisonHasData)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteBoolean("baselineHasData", baselineHasData);
        writer.WriteBoolean("comparisonHasData", comparisonHasData);
        // True only when both sides actually recorded the dimension - the one
        // flag the renderer needs to decide between showing deltas and showing
        // "not captured in the baseline".
        writer.WriteBoolean("comparable", baselineHasData && comparisonHasData);
        writer.WriteEndObject();
    }

    private static void WriteCaptureSummary(Utf8JsonWriter writer, CaptureProfile profile)
    {
        writer.WriteStartObject();
        writer.WriteString("filePath", profile.FilePath);
        writer.WriteString("processName", profile.ProcessName);
        writer.WriteNumber("captureDurationMSec", profile.CaptureDurationMSec);
        writer.WriteNumber("totalEventCount", profile.TotalEventCount);

        writer.WriteNumber("totalGcCount", profile.TotalGcCount);
        writer.WriteNumber("totalGcPauseMSec", profile.TotalGcPauseMSec);
        writer.WriteNumber("totalAllocationTickCount", profile.TotalAllocationTickCount);
        writer.WriteNumber("totalAllocatedBytes", profile.TotalAllocatedBytes);
        writer.WriteNumber("totalExceptionCount", profile.TotalExceptionCount);
        writer.WriteNumber("totalCpuSampleCount", profile.TotalCpuSampleCount);
        writer.WriteNumber("totalContentionCount", profile.TotalContentionCount);
        writer.WriteNumber("totalContentionWaitMSec", profile.TotalContentionWaitMSec);

        writer.WriteBoolean("hasTimeBreakdown", profile.HasTimeBreakdown);
        writer.WriteNumber("gcPercent", profile.GcPercent);
        writer.WriteNumber("contentionPercent", profile.ContentionPercent);
        writer.WriteBoolean("hasCpuBreakdown", profile.HasCpuBreakdown);
        writer.WriteNumber("idlePercent", profile.IdlePercent);
        writer.WriteNumber("cpuBoundPercent", profile.CpuBoundPercent);

        writer.WritePropertyName("gcGenerations");
        writer.WriteStartArray();

        for (int generationIndex = 0; generationIndex < profile.GcGenerations.Count; ++generationIndex)
        {
            GcGenerationProfile generationProfile = profile.GcGenerations[generationIndex];

            writer.WriteStartObject();
            writer.WriteNumber("generation", generationProfile.Generation);
            writer.WriteNumber("count", generationProfile.Count);
            writer.WriteNumber("totalPauseMSec", generationProfile.TotalPauseMSec);
            writer.WriteNumber("maxPauseMSec", generationProfile.MaxPauseMSec);
            writer.WriteNumber("totalPromotedBytes", generationProfile.TotalPromotedBytes);
            writer.WriteNumber("finalHeapSizeBytes", generationProfile.FinalHeapSizeBytes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRows(Utf8JsonWriter writer, string propertyName, List<DiffRow> rows)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();

        for (int rowIndex = 0; rowIndex < rows.Count; ++rowIndex)
        {
            DiffRow row = rows[rowIndex];

            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("kind", KindName(row.Kind));
            writer.WriteNumber("baselineCount", row.BaselineCount);
            writer.WriteNumber("comparisonCount", row.ComparisonCount);
            writer.WriteNumber("baselineAmount", row.BaselineAmount);
            writer.WriteNumber("comparisonAmount", row.ComparisonAmount);
            writer.WriteNumber("deltaCount", row.DeltaCount);
            writer.WriteNumber("deltaAmount", row.DeltaAmount);
            writer.WriteNumber("baselineAmountPerSecond", row.BaselineAmountPerSecond);
            writer.WriteNumber("comparisonAmountPerSecond", row.ComparisonAmountPerSecond);
            writer.WriteNumber("deltaAmountPerSecond", row.DeltaAmountPerSecond);

            // NaN means "no baseline to be relative to" (an Added row).
            // System.Text.Json refuses to write NaN as a number at all, so it
            // goes out as null and the renderer shows "new" rather than a
            // fabricated percentage.
            if (double.IsNaN(row.PercentChange) || double.IsInfinity(row.PercentChange))
            {
                writer.WriteNull("percentChange");
            }
            else
            {
                writer.WriteNumber("percentChange", row.PercentChange);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string KindName(DiffRowKind kind)
    {
        if (kind == DiffRowKind.Added)
        {
            return "added";
        }

        if (kind == DiffRowKind.Removed)
        {
            return "removed";
        }

        return "matched";
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Diff)
