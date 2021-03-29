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
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.IO;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class ProcessBasedListener
{
    private class Generation
    {
        public int Id { get; set; }
        public long SizeBefore { get; set; }
        public long SizeAfter { get; set; }
        public long ObjSpaceBefore { get; set; }
        public long Fragmentation { get; set; }
        public long FreeListSpaceBefore { get; set; }
        public long FreeListSpaceAfter { get; set; }
        public long FreeObjSpaceBefore { get; set; }
        public long FreeObjSpaceAfter { get; set; }
        public long ObjSizeAfter { get; set; }
        public long In { get; set; }
        public long Out { get; set; }
        public long NewAllocation { get; set; }
        public long SurvRate { get; set; }
        public long PinnedSurv { get; set; }
        public long NonePinnedSurv { get; set; }

        public string ToJsonString()
        {
            return $"{{\"Id\":{Id},\"SizeBefore\":{SizeBefore},\"SizeAfter\":{SizeAfter},\"ObjSpaceBefore\":{ObjSpaceBefore},\"Fragmentation\":{Fragmentation},\"FreeListSpaceBefore\":{FreeListSpaceBefore},\"FreeListSpaceAfter\":{FreeListSpaceAfter},\"FreeObjSpaceBefore\":{FreeObjSpaceBefore},\"FreeObjSpaceAfter\":{FreeObjSpaceAfter},\"ObjSizeAfter\":{ObjSizeAfter},\"In\":{In},\"Out\":{Out},\"NewAllocation\":{NewAllocation},\"SurvRate\":{SurvRate},\"PinnedSurv\":{PinnedSurv},\"NonePinnedSurv\":{NonePinnedSurv}}}";
        }
    }

    private class HeapInfo
    {
        public int HeapIndex { get; set; }
        public List<Generation> GenData { get; set; }

        public HeapInfo()
        {
            GenData = new List<Generation>();
        }

        public string ToJsonString()
        {
            List<string> strings = new List<string>();

            foreach (var gen in GenData)
            {
                strings.Add(gen.ToJsonString());
            }
            

            return $"{{\"Index\":\"{HeapIndex}\",\"Generations\":[{string.Join(',', strings)}]}}";
        }
    }

    private class GcInfo
    {
        public GCKind Kind { get; set; }
        public int Generation { get; set; }
        public long Gen0MinSize { get; set; }
        public long GenerationSizeLOH { get; set; }
        public long GenerationSize0 { get; set; }
        public long GenerationSize1 { get; set; }
        public long GenerationSize2 { get; set; }
        public int Id { get; set; }
        public int NumHeaps { get; set; }
        public double PauseEndRelativeMSec { get; set; }
        public double PauseStartRelativeMSec { get; set; }
        public double PauseDurationMSec { get; set; }
        public GCReason Reason { get; set; }
        public long TotalHeapSize { get; set; }
        public long TotalPromoted { get; set; }
        public long TotalPromotedLOH { get; set; }
        public long TotalPromotedSize0 { get; set; }
        public long TotalPromotedSize1 { get; set; }
        public long TotalPromotedSize2 { get; set; }
        public GCType Type { get; set; }

        public bool Processed { get; set; }

        public List<HeapInfo> Heaps { get; set; }

        public GcInfo()
        {
            Heaps = new List<HeapInfo>();
            Processed = false;
        }

        public string ToJsonString()
        {
            string kind = null;
            if (this.Kind == GCKind.Background)
            {
                kind = "Background";
            }
            else
            {
                kind = "Blocking";
            }

            string reason = null;
            if (this.Reason == GCReason.AllocSmall)
            {
                reason = "AllocSmall";
            }
            else if (this.Reason == GCReason.Induced)
            {
                reason = "Induced";
            }
            else if (this.Reason == GCReason.LowMemory)
            {
                reason = "LowMemory";
            }
            else if (this.Reason == GCReason.Empty)
            {
                reason = "Empty";
            }
            else if (this.Reason == GCReason.AllocLarge)
            {
                reason = "AllocLarge";
            }
            else if (this.Reason == GCReason.OutOfSpaceSOH)
            {
                reason = "OutOfSpaceSOH";
            }
            else if (this.Reason == GCReason.OutOfSpaceLOH)
            {
                reason = "OutOfSpaceLOH";
            }
            else if (this.Reason == GCReason.InducedNotForced)
            {
                reason = "InducedNotForced";
            }
            else if (this.Reason == GCReason.Internal)
            {
                reason = "Internal";
            }
            else if (this.Reason == GCReason.InducedLowMemory)
            {
                reason = "InducedLowMemory";
            }
            else if (this.Reason == GCReason.InducedCompacting)
            {
                reason = "InducedCompacting";
            }
            else if (this.Reason == GCReason.LowMemoryHost)
            {
                reason = "LowMemoryHost";
            }
            else if (this.Reason == GCReason.PMFullGC)
            {
                reason = "PMFullGC";
            }

            List<string> heaps = new List<string>();
            foreach (HeapInfo heap in Heaps)
            {
                heaps.Add(heap.ToJsonString());
            }

            string heapString = $"[{string.Join(',', heaps)}]";

            return $"{{\"kind\":\"{kind}\",\"generation\":\"{Generation}\",\"Gen0MinSize\":\"{Gen0MinSize}\",\"GenerationSizeLOH\":\"{GenerationSizeLOH}\",\"GenerationSize0\":\"{GenerationSize0}\",\"GenerationSize1\":\"{GenerationSize1}\",\"GenerationSize2\":\"{GenerationSize2}\",\"Id\":\"{Id}\",\"NumHeaps\":\"{NumHeaps}\",\"PauseEndRelativeMSec\":\"{PauseEndRelativeMSec}\",\"PauseStartRelativeMSec\":\"{PauseStartRelativeMSec}\",\"PauseDurationMSec\":\"{PauseDurationMSec}\",\"Reason\":\"{reason}\",\"TotalHeapSize\":\"{TotalHeapSize}\",\"TotalPromoted\":\"{TotalPromoted}\",\"TotalPromotedLOH\":\"{TotalPromotedLOH}\",\"TotalPromotedSize0\":\"{TotalPromotedSize0}\",\"TotalPromotedSize1\":\"{TotalPromotedSize1}\",\"TotalPromotedSize2\":\"{TotalPromotedSize2}\",\"Type\":\"{Type}\",\"Heaps\":{heapString}}}";
        }
    }

    private class ProcessInfo
    {
        public int ProcessId { get; set; }

        public Dictionary<int, GcInfo> GCs { get; set; }

        public GcInfo CurrentGC { get; set; }

        public ProcessInfo(int processId)
        {
            this.ProcessId = processId;
            GCs = new Dictionary<int, GcInfo>();
        }
    }

    private string SessionName { get; set; }

    private Dictionary<int, ProcessInfo> Processes { get; set; }

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

        Processes = new Dictionary<int, ProcessInfo>();
    }

    public void Listen(Action<string> cb)
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
            
            session.Source.Clr.GCHeapStats += (GCHeapStatsTraceData data) =>
            {
                var processInfo = this.GetGcForProcess(data.ProcessID);

                if (processInfo.CurrentGC == null) return;

                var info = processInfo.CurrentGC;

                info.TotalHeapSize = data.TotalHeapSize;
                info.TotalPromoted = data.TotalPromoted;
                info.GenerationSize0 = data.GenerationSize0;
                info.GenerationSize1 = data.GenerationSize1;
                info.GenerationSize2 = data.GenerationSize2;
                info.GenerationSizeLOH = data.GenerationSize3;
                info.TotalPromotedSize0 = data.TotalPromotedSize0;
                info.TotalPromotedSize1 = data.TotalPromotedSize1;
                info.TotalPromotedSize2 = data.TotalPromotedSize2;
                info.TotalPromotedLOH = data.TotalPromotedSize3;

                if (info.Heaps.Count == info.NumHeaps && !info.Processed)
                {
                    this.ProcessCurrentGc(data.ProcessID, info, cb);
                }
            };

            session.Source.Clr.GCGlobalHeapHistory += (GCGlobalHeapHistoryTraceData data) => 
            {
                var processInfo = this.GetGcForProcess(data.ProcessID);

                if (processInfo.CurrentGC == null) return;

                processInfo.CurrentGC.NumHeaps = data.NumHeaps;
                processInfo.CurrentGC.Gen0MinSize = data.FinalYoungestDesired;
            };

            session.Source.Clr.GCPerHeapHistory += (GCPerHeapHistoryTraceData data) =>
            {
                var processInfo = this.GetGcForProcess(data.ProcessID);

                if (processInfo.CurrentGC == null) return;

                var heapInfo = processInfo.CurrentGC.Heaps;

                string heapData = data.ToString();
                var xmlSplit = heapData.Split("\r\n");

                HeapInfo currentHeap = new HeapInfo();
                for (int index = 0; index < xmlSplit.Length; ++index)
                {
                    string currentLine = xmlSplit[index];

                    string[] dataSplit = currentLine.Split("GenData ");
                    if (dataSplit.Length <= 1) continue;

                    Generation currentGen = new Generation();

                    string[] kvSplit = dataSplit[1].Split(' ');

                    for (int keyIndex = 0; keyIndex < kvSplit.Length; ++keyIndex)
                    {
                        string kv = kvSplit[keyIndex];

                        string replacedStr = kv.Replace("\"", "");
                        replacedStr = replacedStr.Replace("/>", "");
                        string[] eSplit = replacedStr.Split('=');

                        string key = eSplit[0];
                        string value = eSplit[1];
                        value = value.Replace(",", "");

                        if (keyIndex == 0)
                        {
                            Debug.Assert(key == "Name");

                            int genId = 0;
                            if (value == "Gen0") genId = 0;
                            else if (value == "Gen1") genId = 1;
                            else if (value == "Gen2") genId = 2;
                            else if (value == "GenLargeObj") genId = 3;
                            else genId = 4;

                            currentGen.Id = genId;
                        }
                        else if (keyIndex == 1)
                        {
                            Debug.Assert(key == "SizeBefore");

                            currentGen.SizeBefore = long.Parse(value);
                        }
                        else if (keyIndex == 2)
                        {
                            Debug.Assert(key == "SizeAfter");

                            currentGen.SizeAfter = long.Parse(value);
                        }
                        else if (keyIndex == 3)
                        {
                            Debug.Assert(key == "ObjSpaceBefore");

                            currentGen.ObjSpaceBefore = long.Parse(value);
                        }
                        else if (keyIndex == 4)
                        {
                            Debug.Assert(key == "Fragmentation");

                            currentGen.Fragmentation = long.Parse(value);
                        }
                        else if (keyIndex == 5)
                        {
                            Debug.Assert(key == "ObjSizeAfter");

                            currentGen.ObjSizeAfter = long.Parse(value);
                        }
                        else if (keyIndex == 6)
                        {
                            Debug.Assert(key == "FreeListSpaceBefore");

                            currentGen.FreeListSpaceBefore = long.Parse(value);
                        }
                        else if (keyIndex == 7)
                        {
                            Debug.Assert(key == "FreeObjSpaceBefore");

                            currentGen.FreeObjSpaceBefore = long.Parse(value);
                        }
                        else if (keyIndex == 8)
                        {
                            Debug.Assert(key == "FreeListSpaceAfter");

                            currentGen.FreeListSpaceAfter = long.Parse(value);
                        }
                        else if (keyIndex == 9)
                        {
                            Debug.Assert(key == "FreeObjSpaceAfter");

                            currentGen.FreeObjSpaceAfter = long.Parse(value);
                        }
                        else if (keyIndex == 10)
                        {
                            Debug.Assert(key == "In");

                            currentGen.In = long.Parse(value);
                        }
                        else if (keyIndex == 11)
                        {
                            Debug.Assert(key == "Out");

                            currentGen.Out = long.Parse(value);
                        }
                        else if (keyIndex == 12)
                        {
                            Debug.Assert(key == "NewAllocation");

                            currentGen.NewAllocation = long.Parse(value);
                        }
                        else if (keyIndex == 13)
                        {
                            Debug.Assert(key == "SurvRate");

                            currentGen.SurvRate = long.Parse(value);
                        }
                        else if (keyIndex == 14)
                        {
                            Debug.Assert(key == "PinnedSurv");

                            currentGen.PinnedSurv = long.Parse(value);
                        }
                        else if (keyIndex == 15)
                        {
                            Debug.Assert(key == "NonePinnedSurv");

                            currentGen.NonePinnedSurv = long.Parse(value);
                        }
                    }

                    currentHeap.GenData.Add(currentGen);
                }

                processInfo.CurrentGC.Heaps.Add(currentHeap);

                if (processInfo.CurrentGC.Heaps.Count == processInfo.CurrentGC.NumHeaps && !processInfo.CurrentGC.Processed)
                {
                    this.ProcessCurrentGc(data.ProcessID, processInfo.CurrentGC, cb);
                }
            };

            session.Source.Clr.GCStart += (GCStartTraceData data) =>
            {
                Console.WriteLine(data.ProcessID);
                GcInfo info = new GcInfo();
                info.Generation = data.Depth;
                info.Id = data.Count;
                info.PauseDurationMSec = 0;
                info.PauseEndRelativeMSec = 0;
                info.PauseStartRelativeMSec = data.TimeStampRelativeMSec;
                info.Reason = data.Reason;
                info.Type = data.Type;

                info.Kind = info.Generation <= 1 ? GCKind.Ephemeral : GCKind.FullBlocking;

                var processInfo = this.GetGcForProcess(data.ProcessID);

                try
                {
                    processInfo.GCs.Add(info.Id, info);
                }
                catch(Exception e)
                {
                    // Most likely the existing process died, and another managed process
                    // took its place.
                    processInfo.GCs = new Dictionary<int, GcInfo>();
                    
                    processInfo.GCs.Add(info.Id, info);
                }

                if (processInfo.CurrentGC != null && processInfo.CurrentGC.NumHeaps != processInfo.CurrentGC.Heaps.Count)
                {
                    // We have started processing another gc before finishing the first on
                    Debug.Assert(!processInfo.CurrentGC.Processed);
                    Debug.Assert(false);
                }

                processInfo.CurrentGC = info;
            };

            session.Source.Clr.GCStop += (GCEndTraceData data) =>
            {
                var processInfo = this.GetGcForProcess(data.ProcessID, create: false);

                if (processInfo != null)
                {
                    GcInfo info = null;
                    if (processInfo.GCs.TryGetValue(data.Count, out info))
                    {
                        info.PauseEndRelativeMSec = data.TimeStampRelativeMSec;
                        info.PauseDurationMSec = info.PauseEndRelativeMSec - info.PauseStartRelativeMSec;
                    }
                }
            };

            session.Source.Process();
        }
    }

    private ProcessInfo GetGcForProcess(int processId, bool create=true)
    {
        ProcessInfo info = null;
        if (this.Processes.TryGetValue(processId, out info))
        {
            return info;
        }
        else
        {
            if (create)
            {
                info = new ProcessInfo(processId);
                this.Processes.Add(processId, info);
            }

            return info;
        }
    }

    private void ProcessCurrentGc(int processId, GcInfo info, Action<string> cb)
    {
        string processName = null;

        try
        {
            Process proc = Process.GetProcessById(processId);

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + proc.Id))
            using (ManagementObjectCollection objects = searcher.Get())
            {
                processName = objects.Cast<ManagementBaseObject>().SingleOrDefault()?["CommandLine"]?.ToString();
            }

            if (string.IsNullOrWhiteSpace(processName))
            {
                int i = 0;
            }

        }
        catch(Exception e)
        {

        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = "";
            Console.WriteLine($"Unable to get info for {processId}");
        }
        else
        {
            processName = processName.Replace("\"", "");
            processName = processName.Replace("\\", "\\\\");
        }

        string returnData = $"{{\"ProcessID\": {processId}, \"ProcessName\": \"{processName}\", \"data\": {info.ToJsonString()}}}";

        Console.WriteLine(processId);

        cb(returnData);

        info.Processed = true;
    }

}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // End of namespace (DotnetInsights)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
