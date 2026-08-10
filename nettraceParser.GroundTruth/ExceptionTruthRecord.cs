////////////////////////////////////////////////////////////////////////////////
// Module: ExceptionTruthRecord.cs
//
// Notes:
// Same plain-POCO-of-primitives approach as AllocationTruthRecord.cs, for the
// same reason - consumers (the diff test in nettraceParser.Tests) shouldn't
// need a PackageReference on TraceEvent just to read a comparison result.
//
// LeafMethodName/Frames follow AllocationTruthRecord's own documented
// convention exactly (TraceEvent's raw, non-paren-stripped
// CodeAddress.FullMethodName) - see that file's header comment for why the
// diff test does the stripping itself rather than baking in one fixed
// normalization here.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GroundTruth {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ExceptionTruthRecord
{
    public double RelativeMSec;
    public string ExceptionType;
    public string ExceptionMessage;
    public int HResult;
    public int Flags;

    // Null when TraceEvent itself couldn't resolve a call stack for this
    // throw (not stack-walked) - mirrors ExceptionEvent.Stack.Length == 0 on
    // nettraceParser's side.
    public string LeafMethodName;

    // The throw's FULL call stack, leaf (throw site) frame first
    // (Frames[0] == LeafMethodName), walking TraceCallStack.Caller to the
    // outermost frame. Empty, never null, when the throw wasn't
    // stack-walked.
    public List<string> Frames = new List<string>();
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GroundTruth)
