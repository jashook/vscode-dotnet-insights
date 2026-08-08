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

using System.Collections.Generic;

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

    // The tick's FULL call stack, leaf frame first (Frames[0] ==
    // LeafMethodName), walking TraceCallStack.Caller to the outermost frame.
    // Empty, never null, when the tick wasn't stack-walked.
    //
    // LeafMethodName above only ever proved that the *allocation site* was
    // resolved correctly - which is what the StackId-recycling investigation
    // needed at the time. It says nothing about whether the CALLERS above
    // that leaf are right, and the drill-down view's whole caller tree is
    // built from exactly those callers, so a chain could be silently wrong
    // (wrong order, truncated, frames dropped) with the leaf-only diff still
    // reporting 0 mismatches. This closes that gap.
    public List<string> Frames = new List<string>();
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GroundTruth)
