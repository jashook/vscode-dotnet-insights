////////////////////////////////////////////////////////////////////////////////
// Module: TraceEventAllocationReader.cs
//
// Notes:
// Ground truth for allocation-tick stacks, via Microsoft.Diagnostics.Tracing.
// TraceEvent - same rationale as TraceEventGcReader.cs. Unlike GC events
// (read directly off EventPipeEventSource via TraceLoadedDotNetRuntime),
// resolving a GCAllocationTick's call stack needs TraceEvent's own code
// address resolution, which only exists on TraceLog's played-back event
// stream - so this reader first converts the .nettrace file to a temporary
// .etlx (TraceLog.CreateFromEventPipeDataFile), same as PerfView/dotnet-trace
// themselves do internally, then reads TraceLog.Events looking for
// GCAllocationTickTraceData and walks its .CallStack(). The .etlx conversion
// is a real, sometimes multi-minute cost on a large capture - acceptable here
// since this whole reader is opt-in (see GroundTruthDiffTests.cs's
// NETTRACE_GROUNDTRUTH_FIXTURE gate), never run in CI.
//
// This is the exact same approach used ad hoc during the investigation that
// found nettraceParser's StackId-recycling bug (a single whole-file
// Dictionary<int, long[]> silently overwritten by StackBlock's own StackId
// reuse across sequence-point boundaries - see EventBlock.cs's own comment)
// - promoted here from a throwaway diagnostic script into permanent
// regression coverage so that class of bug can't silently reappear.
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

public static class TraceEventAllocationReader
{
    public static List<AllocationTruthRecord> Read(string tracePath)
    {
        List<AllocationTruthRecord> records = new List<AllocationTruthRecord>();
        string etlxPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".etlx");

        try
        {
            TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath);

            using (TraceLog traceLog = new TraceLog(etlxPath))
            {
                foreach (TraceEvent evt in traceLog.Events)
                {
                    GCAllocationTickTraceData tick = evt as GCAllocationTickTraceData;
                    if (tick == null)
                    {
                        continue;
                    }

                    TraceCallStack stack = tick.CallStack();

                    AllocationTruthRecord record = new AllocationTruthRecord();
                    record.RelativeMSec = tick.TimeStampRelativeMSec;
                    record.AllocationAmount = tick.AllocationAmount64;
                    record.TypeName = tick.TypeName;
                    // Deliberately raw, not paren-stripped here - see
                    // AllocationTruthRecord.LeafMethodName's own doc comment
                    // for why the diff test does the stripping itself,
                    // trying both forms.
                    record.LeafMethodName = stack != null ? stack.CodeAddress.FullMethodName : null;
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
        // isn't a unique key (distinct heaps/threads can tick in the same
        // millisecond), so ties are broken by AllocationAmount/TypeName,
        // matching the tie-break nettraceParser's own event list already
        // preserves via stable file-order-then-sort.
        records.Sort((left, right) =>
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

        return records;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GroundTruth)
