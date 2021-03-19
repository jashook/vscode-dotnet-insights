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
    private class GcInfo
    {
        public GCKind Kind { get; set; }
        public int Generation { get; set; }
        public int HeapNumber { get; set; }
        public int Id { get; set; }
        public double PauseEndRelativeMSec { get; set; }
        public double PauseStartRelativeMSec { get; set; }
        public double PauseDurationMSec { get; set; }
        public GCReason Reason { get; set; }
        public GCType Type { get; set; }
    }

    private string ProcessName { get; set; }
    private string SessionName { get; set; }

    private Dictionary<int, Dictionary<int, GcInfo>> GcInfoByProcess { get; set; }

    public long ProcessId { get; set; }

    public static TraceEventSession Session { get; private set; }

    public ProcessBasedListener(long processId = -1)
    {
        if (processId <= 0)
        {
            this.ProcessId = -1;
        }
        else
        {
            this.ProcessId = processId;
        }

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

        GcInfoByProcess = new Dictionary<int, Dictionary<int, GcInfo>>();
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
                if (this.ProcessId == -1 || allocData.ProcessID == this.ProcessId)
                {
                    Console.WriteLine($"[{allocData.ProcessID}] Allocated: {allocData.TypeName} - {allocData.AllocationKind} - {allocData.AllocationAmount64}");
                }
            });

            // IObservable<GCHeapStatsTraceData> gcCollectStream = session.Source.Clr.Observe<GCHeapStatsTraceData>();

            // gcCollectStream.Subscribe(collectData =>
            // {
            //     if (this.ProcessId == -1 || collectData.ProcessID == this.ProcessId)
            //     {
            //         Trace.Assert(collectData.Depth >= 0 && collectData.Depth <= 2);
            //         Console.WriteLine($"[{collectData.ProcessID}]: Type: Gen {collectData.Depth} Gen0Size: {collectData.GenerationSize0}, Gen0Promoted: {collectData.TotalPromotedSize0}, Gen1Size: {collectData.GenerationSize1}, Gen1Promoted: {collectData.TotalPromotedSize1}, Gen2Size: {collectData.GenerationSize2}, Gen2Survived: {collectData.TotalPromotedSize2}, LOHSize: {collectData.GenerationSize3}, LOHSurvived: {collectData.TotalPromotedSize3}");
            //     }
            // });
            
            session.Source.Clr.GCHeapStats += (GCHeapStatsTraceData data) =>
            {
                return;
            };

            session.Source.Clr.GCGlobalHeapHistory += (GCGlobalHeapHistoryTraceData data) => 
            {
                return;
            };

            session.Source.Clr.GCPerHeapHistory += (GCPerHeapHistoryTraceData data) =>
            {
                return;
            };

            session.Source.Clr.GCStart += (GCStartTraceData data) =>
            {
                GcInfo info = new GcInfo();
                info.Generation = data.Depth;
                info.HeapNumber = data.ProcessorNumber;
                info.Id = data.Count;
                info.PauseDurationMSec = 0;
                info.PauseEndRelativeMSec = 0;
                info.PauseStartRelativeMSec = data.TimeStampRelativeMSec;
                info.Reason = data.Reason;
                info.Type = data.Type;

                var gcsPerProc = this.GetGcForProcess(data.ProcessID);
                gcsPerProc.Add(info.Id, info);
            };

            session.Source.Clr.GCStop += (GCEndTraceData data) =>
            {
                var gcsPerProc = this.GetGcForProcess(data.ProcessID, create: false);

                if (gcsPerProc != null)
                {
                    GcInfo info = null;
                    if (gcsPerProc.TryGetValue(data.Count, out info))
                    {
                        info.PauseEndRelativeMSec = data.TimeStampRelativeMSec;
                        info.PauseDurationMSec = info.PauseEndRelativeMSec - info.PauseStartRelativeMSec;
                        return;
                    }
                }
            };

            session.Source.Process();
        }
    }

    private Dictionary<int, GcInfo> GetGcForProcess(int processId, bool create=true)
    {
        Dictionary<int, GcInfo> perProcessInfo = null;
        if (this.GcInfoByProcess.TryGetValue(processId, out perProcessInfo))
        {
            return perProcessInfo;
        }
        else
        {
            if (create)
            {
                perProcessInfo = new Dictionary<int, GcInfo>();
                this.GcInfoByProcess.Add(processId, perProcessInfo);
            }

            return perProcessInfo;
        }
    }

}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // End of namespace (DotnetInsights)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
