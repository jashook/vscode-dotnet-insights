////////////////////////////////////////////////////////////////////////////////
// Module: TraceEventExceptionReader.cs
//
// Notes:
// Ground truth for ExceptionThrown_V1 events and their throw-site stacks,
// via Microsoft.Diagnostics.Tracing.TraceEvent - same rationale and same
// .etlx-conversion approach as TraceEventAllocationReader.cs (stack
// resolution only exists on TraceLog's played-back event stream, not raw
// EventPipeEventSource). The .etlx conversion is a real, sometimes
// multi-minute cost on a large capture - acceptable here since this whole
// reader is opt-in (see GroundTruthDiffTests.cs's NETTRACE_GROUNDTRUTH_FIXTURE
// gate), never run in CI.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GroundTruth {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class TraceEventExceptionReader
{
    public static List<ExceptionTruthRecord> Read(string tracePath)
    {
        List<ExceptionTruthRecord> records = new List<ExceptionTruthRecord>();
        string etlxPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".etlx");

        try
        {
            TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath);

            using (TraceLog traceLog = new TraceLog(etlxPath))
            {
                foreach (TraceEvent evt in traceLog.Events)
                {
                    ExceptionTraceData exceptionData = evt as ExceptionTraceData;
                    if (exceptionData == null)
                    {
                        continue;
                    }

                    TraceCallStack stack = exceptionData.CallStack();

                    ExceptionTruthRecord record = new ExceptionTruthRecord();
                    record.RelativeMSec = exceptionData.TimeStampRelativeMSec;
                    record.ExceptionType = exceptionData.ExceptionType;
                    record.ExceptionMessage = exceptionData.ExceptionMessage;
                    record.HResult = exceptionData.ExceptionHRESULT;
                    record.Flags = (int)exceptionData.ExceptionFlags;
                    // Deliberately raw, not paren-stripped here - see
                    // AllocationTruthRecord.LeafMethodName's own doc comment
                    // for why the diff test does the stripping itself.
                    record.LeafMethodName = stack != null ? stack.CodeAddress.FullMethodName : null;

                    // Walk Caller to the outermost frame, leaf (throw site)
                    // first - the same order nettraceParser's own
                    // ExceptionEvent.Stack uses (see Blocks/StackBlock.cs),
                    // so the diff test can compare the two index-for-index
                    // without either side reversing.
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
        // isn't a unique key (this load generator throws several exceptions
        // within the same millisecond), so ties are broken by
        // ExceptionType/ExceptionMessage, matching the tie-break
        // nettraceParser's own event list already preserves via stable
        // file-order-then-sort.
        records.Sort((left, right) =>
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

        return records;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GroundTruth)
