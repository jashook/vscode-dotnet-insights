////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpReaderTests.cs
//
// Notes:
// Diffs GcDumpReader's decode of a real `.gcdump` against `dotnet-gcdump
// report`'s own output for the same file - the tool that WROTE the format,
// used here as an independent second implementation. This is the same
// approach GroundTruthDiffTests.cs takes for `.nettrace` against TraceEvent,
// and for the same reason: a pinned-value test derived from this reader's own
// output would pin a bug just as happily as a correct value.
//
// Opt-in via GCDUMP_GROUNDTRUTH_FIXTURE (a local .gcdump path), so nothing has
// to be checked in and the test is a silent no-op by default:
//   GCDUMP_GROUNDTRUTH_FIXTURE=~/path/to/some.gcdump \
//     dotnet test --filter GcDumpReaderTests
//
// WHAT IS AND IS NOT COMPARABLE, from dotnet-gcdump's own PrintReportHelper.cs:
//
//   - "GC Heap bytes" is memoryGraph.TotalSize. Directly comparable, exactly.
//   - "GC Heap objects" is memoryGraph.NodeCount - the RAW node count, which
//     includes the synthetic [.NET Roots] node that is not an object on the
//     heap at all. This reader excludes that node from its census (see
//     TypeCensusBuilder.cs), so the expected relationship is
//     theirs == ours + 1, asserted explicitly below rather than papered over.
//   - The per-row "Object Bytes" column is `type.Size`, the TYPE's declared
//     size - NOT the total bytes of that type's instances. It does not sum to
//     the heap and is only equal to (size x count) for fixed-size types.
//     Comparing it as a per-type total would be comparing two different
//     quantities, so this test does not.
//   - The per-row "Count" column comes from GetHistogramByType() and IS the
//     true per-type instance count. That is the strong per-type oracle here,
//     and it is what the main test below diffs, type by type.
//   - Rows whose type has Size == 0 are skipped by their report entirely, so
//     this test only diffs the types their output actually contains.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using DotnetInsights.NetTrace.GcDump;

using Xunit;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class GcDumpReaderTests
{
    private const string FixtureEnvVar = "GCDUMP_GROUNDTRUTH_FIXTURE";

    [Fact]
    public void GcDumpReader_Read_TotalsMatchDotnetGcDumpReport()
    {
        string fixturePath = ResolveFixturePath();

        if (fixturePath == null)
        {
            return;
        }

        GcDumpReadResult readResult = GcDumpReader.Read(fixturePath);
        Assert.True(readResult.Succeeded, readResult.ErrorMessage);

        GroundTruthReport groundTruth = RunDotnetGcDumpReport(fixturePath);

        if (groundTruth == null)
        {
            // dotnet-gcdump is a global tool this repo does not install. With
            // it absent there is nothing to diff against, so this degrades to
            // a no-op the same way a missing fixture does.
            return;
        }

        GcDumpAnalysis analysis = GcDumpAnalysisBuilder.Build(readResult.File.Graph);

        Assert.Equal(groundTruth.TotalBytes, analysis.TotalLiveBytes);

        // See this file's header: their count includes the synthetic
        // [.NET Roots] node, ours deliberately does not.
        Assert.Equal(groundTruth.TotalObjects, analysis.TotalLiveObjects + 1);
        Assert.Equal(groundTruth.TotalObjects, readResult.File.Graph.NodeCount);
    }

    [Fact]
    public void GcDumpReader_Read_PerTypeInstanceCountsMatchDotnetGcDumpReport()
    {
        string fixturePath = ResolveFixturePath();

        if (fixturePath == null)
        {
            return;
        }

        GcDumpReadResult readResult = GcDumpReader.Read(fixturePath);
        Assert.True(readResult.Succeeded, readResult.ErrorMessage);

        GroundTruthReport groundTruth = RunDotnetGcDumpReport(fixturePath);

        if (groundTruth == null)
        {
            return;
        }

        GcDumpAnalysis analysis = GcDumpAnalysisBuilder.Build(readResult.File.Graph);

        Dictionary<string, long> ourCountByType = new Dictionary<string, long>();

        for (int censusIndex = 0; censusIndex < analysis.Census.Count; ++censusIndex)
        {
            TypeCensusEntry entry = analysis.Census[censusIndex];
            string key = MakeTypeKey(entry.TypeName, entry.ModuleName);

            long existingCount;
            ourCountByType.TryGetValue(key, out existingCount);
            ourCountByType[key] = existingCount + entry.InstanceCount;
        }

        List<string> mismatches = new List<string>();

        foreach (KeyValuePair<string, long> expected in groundTruth.CountByType)
        {
            long ourCount;
            ourCountByType.TryGetValue(expected.Key, out ourCount);

            if (ourCount != expected.Value)
            {
                mismatches.Add($"{expected.Key}: dotnet-gcdump={expected.Value} nettraceParser={ourCount}");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            $"{mismatches.Count} of {groundTruth.CountByType.Count} types disagree on instance count:{Environment.NewLine}" +
            string.Join(Environment.NewLine, mismatches.GetRange(0, Math.Min(20, mismatches.Count))));
    }

    // Every object must be accounted for exactly once: reachable objects carry
    // a dominator, unreachable ones do not. This does not need dotnet-gcdump,
    // so unlike the diffs above it runs whenever a fixture is present.
    [Fact]
    public void GcDumpReader_Read_CensusAccountsForEveryNonRootNode()
    {
        string fixturePath = ResolveFixturePath();

        if (fixturePath == null)
        {
            return;
        }

        GcDumpReadResult readResult = GcDumpReader.Read(fixturePath);
        Assert.True(readResult.Succeeded, readResult.ErrorMessage);

        HeapGraph graph = readResult.File.Graph;
        GcDumpAnalysis analysis = GcDumpAnalysisBuilder.Build(graph);

        long censusObjects = 0;
        long censusBytes = 0;

        for (int censusIndex = 0; censusIndex < analysis.Census.Count; ++censusIndex)
        {
            censusObjects += analysis.Census[censusIndex].InstanceCount;
            censusBytes += analysis.Census[censusIndex].ExclusiveBytes;
        }

        Assert.Equal(graph.NodeCount - 1, censusObjects);
        Assert.Equal(analysis.TotalLiveObjects, censusObjects);
        Assert.Equal(analysis.TotalLiveBytes, censusBytes);

        // The file's own header field, independent of the per-node decode -
        // so this catches a blob misread that still happened to produce
        // self-consistent per-node sizes.
        Assert.Equal(graph.TotalSize, censusBytes);
    }

    // Every edge must land inside the graph, and the CSR index must be
    // internally consistent - the two invariants a subtly wrong node-blob
    // decode would break first.
    [Fact]
    public void GcDumpReader_Read_AdjacencyIsWellFormed()
    {
        string fixturePath = ResolveFixturePath();

        if (fixturePath == null)
        {
            return;
        }

        GcDumpReadResult readResult = GcDumpReader.Read(fixturePath);
        Assert.True(readResult.Succeeded, readResult.ErrorMessage);

        HeapGraph graph = readResult.File.Graph;

        Assert.Equal(0, graph.ChildStart[0]);
        Assert.Equal(graph.ChildTarget.Length, graph.ChildStart[graph.NodeCount]);

        for (int nodeIndex = 0; nodeIndex < graph.NodeCount; ++nodeIndex)
        {
            Assert.True(graph.ChildStart[nodeIndex] <= graph.ChildStart[nodeIndex + 1]);
        }

        for (int edgeIndex = 0; edgeIndex < graph.ChildTarget.Length; ++edgeIndex)
        {
            int target = graph.ChildTarget[edgeIndex];
            Assert.InRange(target, 0, graph.NodeCount - 1);
        }

        for (int nodeIndex = 0; nodeIndex < graph.NodeCount; ++nodeIndex)
        {
            Assert.InRange(graph.NodeTypeIndex[nodeIndex], 0, graph.TypeCount - 1);
        }
    }

    ////////////////////////////////////////////////////////////////////////////

    private static string MakeTypeKey(string typeName, string moduleName)
    {
        // dotnet-gcdump prints only the module's file name, so the key has to
        // be built from the same thing on both sides.
        return $"{typeName} {GetFileName(moduleName)}";
    }

    private static string GetFileName(string modulePath)
    {
        if (string.IsNullOrEmpty(modulePath))
        {
            return "";
        }

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

    private static string ResolveFixturePath()
    {
        string fixturePath = Environment.GetEnvironmentVariable(FixtureEnvVar);

        if (string.IsNullOrWhiteSpace(fixturePath))
        {
            return null;
        }

        fixturePath = fixturePath.Trim();

        if (fixturePath.StartsWith("~/", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            fixturePath = Path.Combine(home, fixturePath.Substring(2));
        }

        if (!File.Exists(fixturePath))
        {
            return null;
        }

        return fixturePath;
    }

    ////////////////////////////////////////////////////////////////////////////

    private sealed class GroundTruthReport
    {
        public long TotalBytes;
        public long TotalObjects;
        public Dictionary<string, long> CountByType = new Dictionary<string, long>();
    }

    // Header rows: "<n>  GC Heap bytes" / "<n>  GC Heap objects".
    private static readonly Regex SummaryPattern = new Regex(@"^\s*([\d,]+)\s\s(GC Heap bytes|GC Heap objects)\s*$", RegexOptions.Compiled);

    // Detail rows: "<typeSize>  <count>  <TypeName>  [module.dll]", where the
    // module suffix is absent when the type has no module.
    private static readonly Regex DetailPattern = new Regex(@"^\s*([\d,]+)\s\s+([\d,]+)\s\s(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex ModuleSuffixPattern = new Regex(@"\s\s\[([^\]]*)\]$", RegexOptions.Compiled);

    private static GroundTruthReport RunDotnetGcDumpReport(string fixturePath)
    {
        string output = RunDotnetGcDump(fixturePath);

        if (output == null)
        {
            return null;
        }

        GroundTruthReport report = new GroundTruthReport();
        bool sawSummary = false;

        foreach (string line in output.Split('\n'))
        {
            Match summaryMatch = SummaryPattern.Match(line);

            if (summaryMatch.Success)
            {
                long value = long.Parse(summaryMatch.Groups[1].Value.Replace(",", ""));

                if (summaryMatch.Groups[2].Value == "GC Heap bytes")
                {
                    report.TotalBytes = value;
                }
                else
                {
                    report.TotalObjects = value;
                }

                sawSummary = true;
                continue;
            }

            Match detailMatch = DetailPattern.Match(line);

            if (!detailMatch.Success)
            {
                continue;
            }

            long instanceCount = long.Parse(detailMatch.Groups[2].Value.Replace(",", ""));
            string remainder = detailMatch.Groups[3].Value;

            string moduleName = "";
            Match moduleMatch = ModuleSuffixPattern.Match(remainder);

            if (moduleMatch.Success)
            {
                moduleName = moduleMatch.Groups[1].Value;
                remainder = remainder.Substring(0, moduleMatch.Index);
            }

            string key = $"{remainder} {moduleName}";

            long existingCount;
            report.CountByType.TryGetValue(key, out existingCount);
            report.CountByType[key] = existingCount + instanceCount;
        }

        if (!sawSummary || report.CountByType.Count == 0)
        {
            return null;
        }

        return report;
    }

    private static string RunDotnetGcDump(string fixturePath)
    {
        // The global-tool install location, which is not normally on a test
        // host's PATH.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string toolPath = Path.Combine(home, ".dotnet", "tools", "dotnet-gcdump");

        if (!File.Exists(toolPath))
        {
            return null;
        }

        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = toolPath;
        startInfo.ArgumentList.Add("report");
        startInfo.ArgumentList.Add(fixturePath);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        using (Process process = Process.Start(startInfo))
        {
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return null;
            }

            return output;
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
