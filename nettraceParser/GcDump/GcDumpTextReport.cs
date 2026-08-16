////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpTextReport.cs
//
// Notes:
// The human-readable `--gcdump` output, for the plain CLI path (no --json).
//
// Column layout and header wording deliberately mirror `dotnet-gcdump
// report`'s own HeapStat output, because that tool is this reader's
// ground-truth oracle (see GcDumpReaderTests.cs). Matching the shape means a
// disagreement can be found by eye, side by side in two terminals, before
// anyone writes a test for it - which is how the format was verified in the
// first place.
//
// The extra columns dotnet-gcdump does not have (retained bytes, and the
// unreachable-object warning) are appended rather than interleaved, so the
// leading columns still line up against it.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpTextReport
{
    private const int MaxRowsPrinted = 200;

    public static void Write(TextWriter writer, GcDumpFile file, GcDumpAnalysis analysis)
    {
        HeapGraph graph = file.Graph;
        GcDumpMetadata metadata = file.Metadata;

        writer.WriteLine();

        if (!string.IsNullOrEmpty(metadata.ProcessName))
        {
            writer.WriteLine($"  Process:   {metadata.ProcessName} (pid {metadata.ProcessId})");
        }

        if (!string.IsNullOrEmpty(metadata.MachineName))
        {
            writer.WriteLine($"  Machine:   {metadata.MachineName}");
        }

        if (metadata.TimeCollectedTicks > 0)
        {
            // Local time, matching the .nettrace JSON path's own convention
            // (see CLAUDE.md) - this is read by a person on the machine
            // looking at it, so a UTC timestamp would just be wrong by
            // however many hours their offset is.
            DateTime collectedLocal = new DateTime(metadata.TimeCollectedTicks, DateTimeKind.Utc).ToLocalTime();
            writer.WriteLine($"  Collected: {collectedLocal:o}");
        }

        writer.WriteLine();
        writer.WriteLine($"  {analysis.TotalLiveBytes,15:N0}  GC Heap bytes");
        writer.WriteLine($"  {analysis.TotalLiveObjects,15:N0}  GC Heap objects");
        writer.WriteLine($"  {graph.EdgeCount,15:N0}  References");
        writer.WriteLine($"  {graph.TypeCount,15:N0}  Types");

        if (metadata.IsSampled)
        {
            writer.WriteLine();
            writer.WriteLine($"  NOTE: this dump was SAMPLED (count x{metadata.AverageCountMultiplier:F2}, size x{metadata.AverageSizeMultiplier:F2}).");
            writer.WriteLine("        Counts and sizes below are estimates, not exact.");
        }

        if (analysis.UnreachableObjects > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"  WARNING: {analysis.UnreachableObjects:N0} objects ({analysis.UnreachableBytes:N0} bytes) are not reachable");
            writer.WriteLine("           from the root, so their retained sizes are reported as 0.");
        }

        writer.WriteLine();
        writer.WriteLine($"  {"Object Bytes",15}  {"Retained Bytes",15}  {"Largest",13}  {"Count",9}  Type");

        List<TypeCensusEntry> census = analysis.Census;
        int rowsToPrint = census.Count < MaxRowsPrinted ? census.Count : MaxRowsPrinted;

        for (int rowIndex = 0; rowIndex < rowsToPrint; ++rowIndex)
        {
            TypeCensusEntry entry = census[rowIndex];

            string moduleSuffix = string.IsNullOrEmpty(entry.ModuleName) ? "" : $"  [{GetFileName(entry.ModuleName)}]";
            writer.WriteLine($"  {entry.ExclusiveBytes,15:N0}  {entry.RetainedBytes,15:N0}  {entry.MaxInstanceRetainedBytes,13:N0}  {entry.InstanceCount,9:N0}  {entry.TypeName}{moduleSuffix}");
        }

        if (census.Count > rowsToPrint)
        {
            writer.WriteLine();
            writer.WriteLine($"  ... {census.Count - rowsToPrint:N0} further types omitted (use --json for the full census).");
        }

        writer.WriteLine();
    }

    // Path.GetFileName would do this, but a module name from a .gcdump is
    // whatever the capturing machine's OS wrote - a Windows path can arrive
    // on a reader running on macOS, where Path.GetFileName does not treat '\'
    // as a separator and would return the whole path.
    private static string GetFileName(string modulePath)
    {
        int lastSeparator = -1;

        for (int charIndex = modulePath.Length - 1; charIndex >= 0; --charIndex)
        {
            if (modulePath[charIndex] == '/' || modulePath[charIndex] == '\\')
            {
                lastSeparator = charIndex;
                break;
            }
        }

        if (lastSeparator < 0)
        {
            return modulePath;
        }

        return modulePath.Substring(lastSeparator + 1);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
