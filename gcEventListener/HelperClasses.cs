////////////////////////////////////////////////////////////////////////////////
// Module: HelperClasses.cs
//
////////////////////////////////////////////////////////////////////////////////


namespace DotnetInsights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Parsers.Clr;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

internal class Generation
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

internal class HeapInfo
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

internal class GcInfo
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

    public bool ProcessedPerHeap { get; set; }

    public bool ProcessedGcHeapInfo { get; set; }

    public List<HeapInfo> Heaps { get; set; }

    public GcInfo()
    {
        this.Heaps = new List<HeapInfo>();
        this.ProcessedPerHeap = false;
        this.ProcessedGcHeapInfo = false;
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

internal class ProcessInfo
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

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

}