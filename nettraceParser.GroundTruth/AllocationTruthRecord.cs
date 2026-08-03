////////////////////////////////////////////////////////////////////////////////
// Module: AllocationTruthRecord.cs
//
// Notes:
// Same plain-POCO-of-primitives approach as GcTruthRecord.cs, for the same
// reason - consumers (the diff test in nettraceParser.Tests) shouldn't need a
// PackageReference on TraceEvent just to read a comparison result.
//
// LeafMethodName is TraceEvent's own raw CodeAddress.FullMethodName,
// deliberately NOT paren-stripped here - nettraceParser's own
// MethodSymbolTable.Resolve (see ClrMethodRundown.cs's DisplayName) normally
// carries just Namespace.MethodName with no parameter signature, but for a
// dynamic/Reflection.Emit method (no real metadata token to split a
// signature away from) the CLR's own MethodLoad event bakes the parameter
// list directly into the method's Name field, so nettraceParser's
// DisplayName keeps it too in that one case. Blindly stripping at the first
// '(' on this side (as an earlier version of this reader did) is correct
// for ordinary methods but silently truncates real content for that case -
// the diff test compares against both the raw and paren-stripped forms of
// this field instead of guessing which one applies.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GroundTruth {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class AllocationTruthRecord
{
    public double RelativeMSec;
    public long AllocationAmount;
    public string TypeName;

    // Null when TraceEvent itself couldn't resolve a call stack for this
    // tick (not stack-walked) - mirrors AllocationEvent.Stack.Length == 0 on
    // nettraceParser's side; the diff test treats "no stack on either side"
    // as agreement, not a skip.
    public string LeafMethodName;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GroundTruth)
