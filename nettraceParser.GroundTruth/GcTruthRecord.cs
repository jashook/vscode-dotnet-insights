////////////////////////////////////////////////////////////////////////////////
// Module: GcTruthRecord.cs
//
// Notes:
// Deliberately a plain POCO of primitives - not a wrapper around TraceEvent's
// own Microsoft.Diagnostics.Tracing.Analysis.GC.TraceGC. Consumers (the diff
// test in nettraceParser.Tests) compare this against nettraceParser's own
// Gc/GcEventProjector.cs GcEvent type field-by-field, and should not need a
// PackageReference on TraceEvent themselves just to read a comparison result -
// only this project (and Program.cs's --json mode) touches TraceEvent
// directly. Field names intentionally mirror GcEvent's own (see
// nettraceParser/Gc/GcEventProjector.cs) so the diff test can pair them up
// mechanically instead of hand-maintaining a name-mapping table.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GroundTruth {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class GcTruthRecord
{
    public int Number;
    public int Generation;
    public int Reason;
    public int Type;
    public double PauseDurationMSec;

    // TraceGC.PauseStartRelativeMSec - anchored to GCSuspendEEBegin (when
    // thread suspension was requested), NOT GCStart. Compare against
    // nettraceParser's own PauseStartRelativeMSec (suspend-anchored too),
    // not StartRelativeMSec below - they're genuinely different timestamps,
    // not two names for the same thing.
    public double PauseStartRelativeMSec;

    // TraceGC.StartRelativeMSec - GCStart's own elapsed-since-capture-start
    // time. Deliberately tracked separately from PauseStartRelativeMSec/
    // PauseDurationMSec (a real, still-open semantic difference) because a
    // ~2x-inflated-timestamp class of bug (every event's decoded QPC
    // roughly doubled) went completely undetected until this field existed
    // to catch it: every other field this record carries is anchor/
    // timestamp-invariant.
    public double StartRelativeMSec;

    public long TotalHeapSize;
    public long TotalPromoted;
    public long GenerationSize0;
    public long GenerationSize1;
    public long GenerationSize2;
    public long GenerationSize3;
    public long GenerationSize4;
    public long TotalPromotedSize0;
    public long TotalPromotedSize1;
    public long TotalPromotedSize2;
    public long TotalPromotedSize3;
    public long TotalPromotedSize4;
    public int PinnedObjectCount;

    // False for a GC TraceEvent itself couldn't associate a distinct
    // GCGlobalHeapHistory with (seen on some background GCs) - when false,
    // NumHeaps/FinalYoungestDesired/GlobalMechanisms below are meaningless
    // zero defaults, not real ground truth, and shouldn't be compared
    // against.
    public bool HasGlobalHeapHistory;
    public int NumHeaps;
    public long FinalYoungestDesired;
    public int GlobalMechanisms;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GroundTruth)
