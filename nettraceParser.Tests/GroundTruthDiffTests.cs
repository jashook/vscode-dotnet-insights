////////////////////////////////////////////////////////////////////////////////
// Module: GroundTruthDiffTests.cs
//
// Notes:
// Diffs nettraceParser's own GcEventProjector.Project output against
// nettraceParser.GroundTruth's TraceEventGcReader.Read output for the same
// .nettrace file - the latter is computed via Microsoft.Diagnostics.Tracing.
// TraceEvent, the library PerfView's GC Stats view and dotnet-trace's own
// analyzers are built on, so a divergence here means nettraceParser's
// hand-rolled decoder (Gc/*.cs) disagrees with what PerfView would show for
// the same file - which is exactly the class of bug the fixed-value pins in
// RealCaptureTests.cs cannot catch, since those pins were derived from
// nettraceParser's own output in the first place and would happily pin a bug.
//
// Deliberately opt-in via an environment variable rather than a checked-in
// fixture: the whole point is to diff against real investigation captures
// (which run hundreds of MB to multiple GB and often contain
// production-sensitive data), never to check one into the repo. Point
// NETTRACE_GROUNDTRUTH_FIXTURE at a local .nettrace file to run this, e.g.:
//   NETTRACE_GROUNDTRUTH_FIXTURE=~/projects/Investigations/foo.nettrace \
//     dotnet test --filter GroundTruthDiffTests
// With the variable unset (the default, including in CI), this test is a
// silent no-op rather than a failure or a discovery-time skip - there is no
// fixture to diff against.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.GroundTruth;

using Xunit;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class GroundTruthDiffTests
{
    private const string FixtureEnvVar = "NETTRACE_GROUNDTRUTH_FIXTURE";

    // Both sides compute PauseDurationMSec from the same underlying QPC delta
    // and frequency, just via independently-written arithmetic - this covers
    // float rounding differences, not a real disagreement.
    private const double PauseDurationToleranceMSec = 0.01;

    // parsedEvent.PauseStartRelativeMSec and truthRecord.PauseStartRelativeMSec
    // are both anchored to GCSuspendEEBegin now (see GcEventProjector.Project's
    // own comment on pauseStartQpcById) - compare against PauseStartRelativeMSec,
    // not StartRelativeMSec (GCStart's own, genuinely different, timestamp).
    private const double StartTimeToleranceMSec = 0.01;

    [Fact]
    public void GcEventProjector_Project_MatchesTraceEventGroundTruth()
    {
        string fixturePath = Environment.GetEnvironmentVariable(FixtureEnvVar);
        if (string.IsNullOrEmpty(fixturePath) || !File.Exists(fixturePath))
        {
            return;
        }

        NettraceFile file = NettraceFile.Read(fixturePath);
        long referenceQpc = file.Header.SyncTimeQPC;

        List<GcEvent> parsedEvents = GcEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc);
        List<GcTruthRecord> truthRecords = TraceEventGcReader.Read(fixturePath);

        Dictionary<int, GcTruthRecord> truthByNumber = new Dictionary<int, GcTruthRecord>();
        foreach (GcTruthRecord truthRecord in truthRecords)
        {
            truthByNumber[truthRecord.Number] = truthRecord;
        }

        List<string> diffs = new List<string>();

        foreach (GcEvent parsedEvent in parsedEvents)
        {
            if (!parsedEvent.HasEnd || !parsedEvent.HasHeapStats || !parsedEvent.HasGlobalHeapHistory)
            {
                continue;
            }

            GcTruthRecord truthRecord;
            if (!truthByNumber.TryGetValue(parsedEvent.Id, out truthRecord))
            {
                diffs.Add($"GC #{parsedEvent.Id}: present in nettraceParser output, missing from TraceEvent ground truth");
                continue;
            }

            truthByNumber.Remove(parsedEvent.Id);

            CompareField(diffs, parsedEvent.Id, "Generation", parsedEvent.Generation, truthRecord.Generation);
            CompareField(diffs, parsedEvent.Id, "Reason", (int)parsedEvent.Reason, truthRecord.Reason);
            CompareField(diffs, parsedEvent.Id, "Type", (int)parsedEvent.Type, truthRecord.Type);
            CompareField(diffs, parsedEvent.Id, "TotalHeapSize", parsedEvent.TotalHeapSize, truthRecord.TotalHeapSize);
            CompareField(diffs, parsedEvent.Id, "TotalPromoted", parsedEvent.TotalPromoted, truthRecord.TotalPromoted);
            CompareField(diffs, parsedEvent.Id, "GenerationSize0", parsedEvent.GenerationSize0, truthRecord.GenerationSize0);
            CompareField(diffs, parsedEvent.Id, "GenerationSize1", parsedEvent.GenerationSize1, truthRecord.GenerationSize1);
            CompareField(diffs, parsedEvent.Id, "GenerationSize2", parsedEvent.GenerationSize2, truthRecord.GenerationSize2);
            CompareField(diffs, parsedEvent.Id, "GenerationSize3", parsedEvent.GenerationSize3, truthRecord.GenerationSize3);
            CompareField(diffs, parsedEvent.Id, "GenerationSize4", parsedEvent.GenerationSize4, truthRecord.GenerationSize4);
            CompareField(diffs, parsedEvent.Id, "TotalPromotedSize0", parsedEvent.TotalPromotedSize0, truthRecord.TotalPromotedSize0);
            CompareField(diffs, parsedEvent.Id, "TotalPromotedSize1", parsedEvent.TotalPromotedSize1, truthRecord.TotalPromotedSize1);
            CompareField(diffs, parsedEvent.Id, "TotalPromotedSize2", parsedEvent.TotalPromotedSize2, truthRecord.TotalPromotedSize2);
            CompareField(diffs, parsedEvent.Id, "TotalPromotedSize3", parsedEvent.TotalPromotedSize3, truthRecord.TotalPromotedSize3);
            CompareField(diffs, parsedEvent.Id, "TotalPromotedSize4", parsedEvent.TotalPromotedSize4, truthRecord.TotalPromotedSize4);
            CompareField(diffs, parsedEvent.Id, "PinnedObjectCount", parsedEvent.PinnedObjectCount, truthRecord.PinnedObjectCount);

            // Only comparable when ground truth itself resolved a
            // GCGlobalHeapHistory for this GC - see HasGlobalHeapHistory's
            // own doc comment on GcTruthRecord.
            if (truthRecord.HasGlobalHeapHistory)
            {
                CompareField(diffs, parsedEvent.Id, "NumHeaps", parsedEvent.NumHeaps, truthRecord.NumHeaps);
                CompareField(diffs, parsedEvent.Id, "FinalYoungestDesired", parsedEvent.FinalYoungestDesired, truthRecord.FinalYoungestDesired);
                CompareField(diffs, parsedEvent.Id, "GlobalMechanisms", (int)parsedEvent.GlobalMechanisms, truthRecord.GlobalMechanisms);
            }

            double pauseDelta = Math.Abs(parsedEvent.PauseDurationMSec - truthRecord.PauseDurationMSec);
            if (pauseDelta > PauseDurationToleranceMSec)
            {
                diffs.Add($"GC #{parsedEvent.Id}: PauseDurationMSec differs by {pauseDelta:F4}ms (nettraceParser={parsedEvent.PauseDurationMSec:F4}, groundTruth={truthRecord.PauseDurationMSec:F4})");
            }

            double startDelta = Math.Abs(parsedEvent.PauseStartRelativeMSec - truthRecord.PauseStartRelativeMSec);
            if (startDelta > StartTimeToleranceMSec)
            {
                diffs.Add($"GC #{parsedEvent.Id}: PauseStartRelativeMSec differs by {startDelta:F4}ms (nettraceParser={parsedEvent.PauseStartRelativeMSec:F4}, groundTruth={truthRecord.PauseStartRelativeMSec:F4})");
            }
        }

        foreach (GcTruthRecord orphanTruthRecord in truthByNumber.Values)
        {
            diffs.Add($"GC #{orphanTruthRecord.Number}: present in TraceEvent ground truth, missing from nettraceParser output");
        }

        Assert.True(diffs.Count == 0, $"{diffs.Count} field mismatch(es) between nettraceParser and TraceEvent ground truth (fixture: {fixturePath}):\n" + string.Join("\n", diffs));
    }

    private static void CompareField(List<string> diffs, int gcId, string fieldName, long parsedValue, long truthValue)
    {
        if (parsedValue != truthValue)
        {
            diffs.Add($"GC #{gcId}: {fieldName} differs (nettraceParser={parsedValue}, groundTruth={truthValue})");
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
