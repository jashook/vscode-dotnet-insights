////////////////////////////////////////////////////////////////////////////////
// Module: CpuSampleTruthRecord.cs
//
// Notes:
// Same plain-POCO-of-primitives approach as AllocationTruthRecord.cs, for the
// same reason - consumers (the diff test in nettraceParser.Tests) shouldn't
// need a PackageReference on TraceEvent just to read a comparison result.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GroundTruth {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class CpuSampleTruthRecord
{
    public double RelativeMSec;
    public int ThreadId;

    // Null when TraceEvent itself couldn't resolve a call stack for this
    // sample - mirrors SampleEvent.Stack.Length == 0 on nettraceParser's
    // side; the diff test treats "no stack on either side" as agreement,
    // not a skip.
    public string LeafMethodName;

    // The sample's FULL call stack, leaf frame first (Frames[0] ==
    // LeafMethodName), walking TraceCallStack.Caller to the outermost frame -
    // same order nettraceParser's own SampleEvent.Stack/frameIds carry (see
    // EventRecord.Stack's own doc comment). Empty, never null, when the
    // sample wasn't stack-walked.
    public List<string> Frames = new List<string>();
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GroundTruth)
