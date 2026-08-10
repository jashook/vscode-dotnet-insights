////////////////////////////////////////////////////////////////////////////////
// Module: TraceEventCpuSampleReader.cs
//
// Notes:
// Ground truth for CPU sample stacks, via Microsoft.Diagnostics.Tracing.
// TraceEvent - same rationale and same .etlx-conversion approach as
// TraceEventAllocationReader.cs (stack-walking a Microsoft-DotNETCore-
// SampleProfiler event needs TraceEvent's own code address resolution,
// which only exists on TraceLog's played-back event stream, not raw
// EventPipeEventSource). The strongly-typed event class for this provider's
// one real event (confirmed via reflection against the installed
// Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5 package - the type name
// itself is prefixed "Clr" for historical reasons even though it decodes
// Microsoft-DotNETCore-SampleProfiler, not a CLR provider event) is
// Microsoft.Diagnostics.Tracing.EventPipe.ClrThreadSampleTraceData.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GroundTruth {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Etlx;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class TraceEventCpuSampleReader
{
    public static List<CpuSampleTruthRecord> Read(string tracePath)
    {
        List<CpuSampleTruthRecord> records = new List<CpuSampleTruthRecord>();
        string etlxPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".etlx");

        try
        {
            TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath);

            using (TraceLog traceLog = new TraceLog(etlxPath))
            {
                foreach (TraceEvent evt in traceLog.Events)
                {
                    ClrThreadSampleTraceData sample = evt as ClrThreadSampleTraceData;
                    if (sample == null)
                    {
                        continue;
                    }

                    TraceCallStack stack = sample.CallStack();

                    CpuSampleTruthRecord record = new CpuSampleTruthRecord();
                    record.RelativeMSec = sample.TimeStampRelativeMSec;
                    record.ThreadId = sample.ThreadID;
                    // Deliberately raw, not paren-stripped here - see
                    // AllocationTruthRecord.LeafMethodName's own doc comment
                    // for why the diff test does the stripping itself,
                    // trying both forms.
                    record.LeafMethodName = stack != null ? stack.CodeAddress.FullMethodName : null;

                    // Walk Caller to the outermost frame, leaf first - the
                    // same order nettraceParser's own SampleEvent.Stack uses
                    // (see Blocks/StackBlock.cs), so the diff test can
                    // compare the two index-for-index without either side
                    // reversing.
                    for (TraceCallStack frame = stack; frame != null; frame = frame.Caller)
                    {
                        record.Frames.Add(frame.CodeAddress.FullMethodName);
                    }

                    records.Add(record);
                }
            }
        }
        finally
        {
            if (File.Exists(etlxPath))
            {
                File.Delete(etlxPath);
            }
        }

        // Stable multi-key sort so the diff test can zip this against
        // nettraceParser's own output positionally - RelativeMSec alone
        // isn't a unique key (distinct threads can be sampled in the same
        // millisecond), so ties are broken by ThreadId, matching the same
        // fields SampleProfileEventProjector's own SampleEvent carries.
        records.Sort((left, right) =>
        {
            int msecCompare = left.RelativeMSec.CompareTo(right.RelativeMSec);
            if (msecCompare != 0)
            {
                return msecCompare;
            }

            return left.ThreadId.CompareTo(right.ThreadId);
        });

        return records;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GroundTruth)
