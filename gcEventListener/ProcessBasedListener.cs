////////////////////////////////////////////////////////////////////////////////
// Module: EventListener.cs
//
// Notes:
// Scope a particular event to a specific process instead of publishing
// information for all the providers on the system.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;

using System.Reactive.Linq;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ProcessBasedListener
{   
    private string ProcessName { get; set; }
    private string SessionName { get; set; }

    public long ProcessId { get; set; }

    public static TraceEventSession Session { get; private set; }

    public ProcessBasedListener(long processId)
    {
        this.ProcessId = processId;

        this.SessionName = "DotnetInsightsEventListener";

        try
        {
            Process proc = Process.GetProcessById((int)this.ProcessId);

            if (proc != null)
            {
                this.ProcessName = proc.ProcessName;
            }

            if (string.IsNullOrWhiteSpace(this.ProcessName))
            {
                this.ProcessName = proc.ProcessName.ToString();
            }
        }
        catch(Exception e)
        {

        }
    }

    public void Listen()
    {
        if (!TraceEventSession.IsElevated() ?? false)
        {
            Console.WriteLine("ETW Event listening required Privilidged Access. Please run as Administrator.");
            return;
        }

        using (var session = new TraceEventSession(this.SessionName))
        {
            ProcessBasedListener.Session = session;

            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e) { session.Dispose(); };
            session.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, (ulong)ClrTraceEventParser.Keywords.GC);

            IObservable<GCAllocationTickTraceData> gcAllocStream = session.Source.Clr.Observe<GCAllocationTickTraceData>();

            gcAllocStream.Subscribe(allocData =>
            {
                if (allocData.ProcessID == this.ProcessId)
                {
                    Console.WriteLine($"[{this.ProcessName} - {this.ProcessId}] Allocated: {allocData.TypeName} - {allocData.AllocationKind} - {allocData.AllocationAmount64}");
                }
            });

            IObservable<GCHeapStatsTraceData> gcCollectStream = session.Source.Clr.Observe<GCHeapStatsTraceData>();

            gcCollectStream.Subscribe(collectData =>
            {
                if (collectData.ProcessID == this.ProcessId)
                {
                    Console.WriteLine($"[{this.ProcessName} - {this.ProcessId}]: Gen0Size: {collectData.GenerationSize0}, Gen0Promoted: {collectData.TotalPromotedSize0}, Gen1Size: {collectData.GenerationSize1}, Gen1Promoted: {collectData.TotalPromotedSize1}, Gen2Size: {collectData.GenerationSize2}, Gen2Survived: {collectData.TotalPromotedSize2}, LOHSize: {collectData.GenerationSize3}, LOHSurvived: {collectData.TotalPromotedSize3}");
                }
            });

            session.Source.Process();
        }
    }

}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // End of namespace (DotnetInsights)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
