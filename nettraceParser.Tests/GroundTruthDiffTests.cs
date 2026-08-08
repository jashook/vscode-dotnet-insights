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

using DotnetInsights.NetTrace.Exceptions;
using DotnetInsights.NetTrace.Gc;
using DotnetInsights.NetTrace.GroundTruth;
using DotnetInsights.NetTrace.Rundown;

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

    // Regression coverage for the StackId-recycling bug: NettraceFile used to
    // hand every event a raw StackId int, resolved later against a single
    // whole-file Dictionary<int, long[]> that a later StackBlock's own
    // sequence-point-scoped StackId reuse would silently overwrite - see
    // NetTraceFormat_v5.md's own StackBlock section ("Events are only
    // allowed to refer to a stack id if there is no sequence point in
    // between the event and the stack") and EventBlock.cs's own comment on
    // why resolution now happens eagerly, at parse time, instead. Confirmed
    // via this same TraceEvent-based comparison on a real production
    // capture: 0 of 30 sampled ticks' leaf frames agreed before the fix,
    // 100% agreed after - this test exists so that result can never silently
    // regress again.
    [Fact]
    public void AllocationEventProjector_Project_StackLeafFramesMatchTraceEventGroundTruth()
    {
        string fixturePath = Environment.GetEnvironmentVariable(FixtureEnvVar);
        if (string.IsNullOrEmpty(fixturePath) || !File.Exists(fixturePath))
        {
            return;
        }

        NettraceFile file = NettraceFile.Read(fixturePath);
        long referenceQpc = file.Header.SyncTimeQPC;

        List<AllocationEvent> parsedEvents = AllocationEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc);
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, referenceQpc);

        List<(double RelativeMSec, long AllocationAmount, string TypeName, string LeafMethodName)> parsedTuples = new List<(double, long, string, string)>(parsedEvents.Count);
        foreach (AllocationEvent parsedEvent in parsedEvents)
        {
            string leaf = parsedEvent.Stack.Length > 0 ? symbolTable.Resolve(parsedEvent.Stack[0], parsedEvent.RelativeMSec) : null;
            parsedTuples.Add((parsedEvent.RelativeMSec, parsedEvent.AllocationAmount, parsedEvent.TypeName, leaf));
        }

        // Same (RelativeMSec, AllocationAmount, TypeName) tie-break ordering
        // as TraceEventAllocationReader.Read - both sides compute
        // RelativeMSec from the same underlying QPC delta/frequency (see
        // NettraceHeader.SyncTimeQPC's own doc comment on this repo's
        // earlier timestamp-decode bug), so a positional zip after this sort
        // pairs up the same real tick on both sides.
        parsedTuples.Sort((left, right) =>
        {
            int msecCompare = left.RelativeMSec.CompareTo(right.RelativeMSec);
            if (msecCompare != 0)
            {
                return msecCompare;
            }

            int amountCompare = left.AllocationAmount.CompareTo(right.AllocationAmount);
            if (amountCompare != 0)
            {
                return amountCompare;
            }

            return string.CompareOrdinal(left.TypeName, right.TypeName);
        });

        List<AllocationTruthRecord> truthRecords = TraceEventAllocationReader.Read(fixturePath);

        Assert.True(parsedTuples.Count == truthRecords.Count, $"Tick count differs: nettraceParser={parsedTuples.Count}, groundTruth={truthRecords.Count} (fixture: {fixturePath})");

        List<string> diffs = new List<string>();
        int compareCount = Math.Min(parsedTuples.Count, truthRecords.Count);

        for (int tickIndex = 0; tickIndex < compareCount; ++tickIndex)
        {
            (double RelativeMSec, long AllocationAmount, string TypeName, string LeafMethodName) parsedTuple = parsedTuples[tickIndex];
            AllocationTruthRecord truthRecord = truthRecords[tickIndex];

            // Compare against both the raw ground-truth name and its
            // paren-stripped form - see AllocationTruthRecord.LeafMethodName's
            // own doc comment on why a single fixed normalization doesn't
            // work for dynamic/Reflection.Emit methods.
            if (parsedTuple.LeafMethodName != truthRecord.LeafMethodName && parsedTuple.LeafMethodName != StripParams(truthRecord.LeafMethodName))
            {
                diffs.Add($"Tick @{parsedTuple.RelativeMSec:F4}ms ({parsedTuple.TypeName}, {parsedTuple.AllocationAmount} bytes): leaf frame differs (nettraceParser={parsedTuple.LeafMethodName ?? "<no stack>"}, groundTruth={truthRecord.LeafMethodName ?? "<no stack>"})");
            }
        }

        Assert.True(diffs.Count == 0, $"{diffs.Count} of {compareCount} allocation-tick leaf frame(s) mismatched between nettraceParser and TraceEvent ground truth (fixture: {fixturePath}):\n" + string.Join("\n", diffs.Count > 50 ? diffs.GetRange(0, 50) : diffs));
    }

    // TraceEvent's FullMethodName includes the full parameter signature
    // (e.g. "Namespace.Type.Method(class System.String)") for an ordinary
    // method - nettraceParser's own MethodSymbolTable.Resolve (see
    // ClrMethodRundown.cs's DisplayName) only carries Namespace.MethodName
    // in that case. Strip at the LAST '(' rather than the first: an ordinary
    // method's own name never contains one (C# generic arguments use <>,
    // never parens), so this is a no-op difference for it, but a dynamic/
    // Reflection.Emit method's Name field can itself already contain a
    // parenthesized fake signature as literal text (e.g.
    // "dynamicClass.Void .ctor(System.String)") with TraceEvent then
    // appending the method's real JIT signature after it (e.g.
    // "...(System.String)(class System.Object[])") - stripping at the first
    // '(' would truncate into that literal name text; the last '(' is
    // always the start of the real, appended signature on both kinds of
    // method.
    private static string StripParams(string fullMethodName)
    {
        if (string.IsNullOrEmpty(fullMethodName))
        {
            return fullMethodName;
        }

        int parenIndex = fullMethodName.LastIndexOf('(');
        return parenIndex >= 0 ? fullMethodName.Substring(0, parenIndex) : fullMethodName;
    }

    // Diffs each allocation tick's ENTIRE resolved call stack - every caller
    // frame, in order - against TraceEvent, not just its leaf.
    //
    // The leaf-only test above is what the StackId-recycling investigation
    // needed, and it passes cleanly, but it can only ever prove the
    // allocation *site* resolved correctly. The drill-down view's whole
    // caller tree (AllocationJsonExporter's BuildCallerTree) is built from
    // the frames ABOVE that leaf, so a chain that's truncated, mis-ordered,
    // or missing frames would render a confidently wrong caller tree while
    // the leaf-only diff still reported zero mismatches. Comparing full
    // chains is the only thing that actually covers what the drill-down
    // renders.
    [Fact]
    public void AllocationEventProjector_Project_FullStacksMatchTraceEventGroundTruth()
    {
        string fixturePath = Environment.GetEnvironmentVariable(FixtureEnvVar);
        if (string.IsNullOrEmpty(fixturePath) || !File.Exists(fixturePath))
        {
            return;
        }

        NettraceFile file = NettraceFile.Read(fixturePath);
        long referenceQpc = file.Header.SyncTimeQPC;

        List<AllocationEvent> parsedEvents = AllocationEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc);
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, referenceQpc);

        List<(double RelativeMSec, long AllocationAmount, string TypeName, List<string> Frames)> parsedTuples = new List<(double, long, string, List<string>)>(parsedEvents.Count);
        foreach (AllocationEvent parsedEvent in parsedEvents)
        {
            List<string> frames = new List<string>(parsedEvent.Stack.Length);
            for (int frameIndex = 0; frameIndex < parsedEvent.Stack.Length; ++frameIndex)
            {
                frames.Add(symbolTable.Resolve(parsedEvent.Stack[frameIndex], parsedEvent.RelativeMSec));
            }

            parsedTuples.Add((parsedEvent.RelativeMSec, parsedEvent.AllocationAmount, parsedEvent.TypeName, frames));
        }

        // Same ordering/zip strategy as the leaf-only test above.
        parsedTuples.Sort((left, right) =>
        {
            int msecCompare = left.RelativeMSec.CompareTo(right.RelativeMSec);
            if (msecCompare != 0)
            {
                return msecCompare;
            }

            int amountCompare = left.AllocationAmount.CompareTo(right.AllocationAmount);
            if (amountCompare != 0)
            {
                return amountCompare;
            }

            return string.CompareOrdinal(left.TypeName, right.TypeName);
        });

        List<AllocationTruthRecord> truthRecords = TraceEventAllocationReader.Read(fixturePath);

        Assert.True(parsedTuples.Count == truthRecords.Count, $"Tick count differs: nettraceParser={parsedTuples.Count}, groundTruth={truthRecords.Count} (fixture: {fixturePath})");

        List<string> diffs = new List<string>();
        int depthMismatchCount = 0;
        int frameMismatchCount = 0;
        int compareCount = Math.Min(parsedTuples.Count, truthRecords.Count);

        for (int tickIndex = 0; tickIndex < compareCount; ++tickIndex)
        {
            (double RelativeMSec, long AllocationAmount, string TypeName, List<string> Frames) parsedTuple = parsedTuples[tickIndex];
            AllocationTruthRecord truthRecord = truthRecords[tickIndex];

            if (parsedTuple.Frames.Count != truthRecord.Frames.Count)
            {
                ++depthMismatchCount;
                if (diffs.Count < 50)
                {
                    diffs.Add($"Tick @{parsedTuple.RelativeMSec:F4}ms ({parsedTuple.TypeName}): stack DEPTH differs (nettraceParser={parsedTuple.Frames.Count} frames, groundTruth={truthRecord.Frames.Count} frames)");
                }

                continue;
            }

            for (int frameIndex = 0; frameIndex < parsedTuple.Frames.Count; ++frameIndex)
            {
                string parsedFrame = parsedTuple.Frames[frameIndex];
                string truthFrame = truthRecord.Frames[frameIndex];

                if (parsedFrame != truthFrame && parsedFrame != StripParams(truthFrame))
                {
                    ++frameMismatchCount;
                    if (diffs.Count < 50)
                    {
                        diffs.Add($"Tick @{parsedTuple.RelativeMSec:F4}ms ({parsedTuple.TypeName}): frame[{frameIndex}] differs (nettraceParser={parsedFrame ?? "<null>"}, groundTruth={truthFrame ?? "<null>"})");
                    }

                    break;
                }
            }
        }

        Assert.True(diffs.Count == 0, $"Full-stack mismatch across {compareCount} ticks: {depthMismatchCount} with differing depth, {frameMismatchCount} with a differing frame (fixture: {fixturePath}):\n" + string.Join("\n", diffs));
    }

    // Exception-event analog of the allocation full-stack test above -
    // diffs ExceptionEventProjector's decoded fields AND each throw's
    // entire resolved call stack against TraceEvent. This is the same
    // pattern that caught the StackId-recycling bug for allocation ticks
    // (see that test's own header comment); running it here too is what
    // actually confirms ExceptionThrown_V1's own StackId is resolved
    // correctly, not just that its fixed-offset payload fields decode to
    // the right values (RealCaptureTests.cs's pinned-value coverage already
    // exercises that half against a known-good sample, but a pin can't
    // catch a bug shared between "what this code computes" and "what its
    // own pin expects").
    [Fact]
    public void ExceptionEventProjector_Project_MatchesTraceEventGroundTruth()
    {
        string fixturePath = Environment.GetEnvironmentVariable(FixtureEnvVar);
        if (string.IsNullOrEmpty(fixturePath) || !File.Exists(fixturePath))
        {
            return;
        }

        NettraceFile file = NettraceFile.Read(fixturePath);
        long referenceQpc = file.Header.SyncTimeQPC;

        List<ExceptionEvent> parsedEvents = ExceptionEventProjector.Project(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, file.Header.SyncTimeUtc, referenceQpc);
        MethodSymbolTable symbolTable = MethodSymbolTable.Build(file.Events, file.Header.PointerSize, file.Header.QPCFrequency, referenceQpc);

        List<(double RelativeMSec, string ExceptionType, string ExceptionMessage, int HResult, int Flags, List<string> Frames)> parsedTuples = new List<(double, string, string, int, int, List<string>)>(parsedEvents.Count);
        foreach (ExceptionEvent parsedEvent in parsedEvents)
        {
            List<string> frames = new List<string>(parsedEvent.Stack.Length);
            for (int frameIndex = 0; frameIndex < parsedEvent.Stack.Length; ++frameIndex)
            {
                frames.Add(symbolTable.Resolve(parsedEvent.Stack[frameIndex], parsedEvent.RelativeMSec));
            }

            parsedTuples.Add((parsedEvent.RelativeMSec, parsedEvent.ExceptionType, parsedEvent.ExceptionMessage, parsedEvent.HResult, (int)parsedEvent.Flags, frames));
        }

        // Same tie-break ordering as TraceEventExceptionReader.Read - both
        // sides compute RelativeMSec from the same underlying QPC
        // delta/frequency, so a positional zip after this sort pairs up the
        // same real throw on both sides.
        parsedTuples.Sort((left, right) =>
        {
            int msecCompare = left.RelativeMSec.CompareTo(right.RelativeMSec);
            if (msecCompare != 0)
            {
                return msecCompare;
            }

            int typeCompare = string.CompareOrdinal(left.ExceptionType, right.ExceptionType);
            if (typeCompare != 0)
            {
                return typeCompare;
            }

            return string.CompareOrdinal(left.ExceptionMessage, right.ExceptionMessage);
        });

        List<ExceptionTruthRecord> truthRecords = TraceEventExceptionReader.Read(fixturePath);

        Assert.True(parsedTuples.Count == truthRecords.Count, $"Exception count differs: nettraceParser={parsedTuples.Count}, groundTruth={truthRecords.Count} (fixture: {fixturePath})");

        List<string> diffs = new List<string>();
        int compareCount = Math.Min(parsedTuples.Count, truthRecords.Count);

        for (int exceptionIndex = 0; exceptionIndex < compareCount; ++exceptionIndex)
        {
            (double RelativeMSec, string ExceptionType, string ExceptionMessage, int HResult, int Flags, List<string> Frames) parsedTuple = parsedTuples[exceptionIndex];
            ExceptionTruthRecord truthRecord = truthRecords[exceptionIndex];

            if (parsedTuple.ExceptionType != truthRecord.ExceptionType)
            {
                diffs.Add($"Exception @{parsedTuple.RelativeMSec:F4}ms: ExceptionType differs (nettraceParser={parsedTuple.ExceptionType}, groundTruth={truthRecord.ExceptionType})");
            }

            if (parsedTuple.ExceptionMessage != truthRecord.ExceptionMessage)
            {
                diffs.Add($"Exception @{parsedTuple.RelativeMSec:F4}ms ({parsedTuple.ExceptionType}): ExceptionMessage differs (nettraceParser={parsedTuple.ExceptionMessage}, groundTruth={truthRecord.ExceptionMessage})");
            }

            if (parsedTuple.HResult != truthRecord.HResult)
            {
                diffs.Add($"Exception @{parsedTuple.RelativeMSec:F4}ms ({parsedTuple.ExceptionType}): HResult differs (nettraceParser=0x{parsedTuple.HResult:X}, groundTruth=0x{truthRecord.HResult:X})");
            }

            if (parsedTuple.Flags != truthRecord.Flags)
            {
                diffs.Add($"Exception @{parsedTuple.RelativeMSec:F4}ms ({parsedTuple.ExceptionType}): Flags differs (nettraceParser={parsedTuple.Flags}, groundTruth={truthRecord.Flags})");
            }

            if (parsedTuple.Frames.Count != truthRecord.Frames.Count)
            {
                diffs.Add($"Exception @{parsedTuple.RelativeMSec:F4}ms ({parsedTuple.ExceptionType}): stack DEPTH differs (nettraceParser={parsedTuple.Frames.Count} frames, groundTruth={truthRecord.Frames.Count} frames)");
                continue;
            }

            for (int frameIndex = 0; frameIndex < parsedTuple.Frames.Count; ++frameIndex)
            {
                string parsedFrame = parsedTuple.Frames[frameIndex];
                string truthFrame = truthRecord.Frames[frameIndex];

                if (parsedFrame != truthFrame && parsedFrame != StripParams(truthFrame))
                {
                    diffs.Add($"Exception @{parsedTuple.RelativeMSec:F4}ms ({parsedTuple.ExceptionType}): frame[{frameIndex}] differs (nettraceParser={parsedFrame ?? "<null>"}, groundTruth={truthFrame ?? "<null>"})");
                    break;
                }
            }
        }

        Assert.True(diffs.Count == 0, $"{diffs.Count} mismatch(es) across {compareCount} exceptions between nettraceParser and TraceEvent ground truth (fixture: {fixturePath}):\n" + string.Join("\n", diffs.Count > 50 ? diffs.GetRange(0, 50) : diffs));
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
