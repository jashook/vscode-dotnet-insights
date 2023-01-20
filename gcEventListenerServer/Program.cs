using DotnetInsights;
using Prometheus;

using System;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

Gauge GcAllocGauge = Metrics.CreateGauge("dn_insights_gc_alloc", "Gc Allocation Event", labelNames: new[] { "process_id", "process", "heap_index", "kind", "type_name" });
Gauge JitEventGauge = Metrics.CreateGauge("dn_insights_jit_event", "Jit Event", labelNames: new[] { "process_id", "process", "is_tiered_up", "has_loaded", "method_id", "tier", "method_name" });

Gauge GcCollectionInfoGauge = Metrics.CreateGauge("dn_insights_gc_collection_info", "Gc Collection Overall", labelNames: new[] { "process_id", "process", "gc_id", "generation", "kind", "reason", "type", "num_heaps"});

Counter GcCollectionCounter = Metrics.CreateCounter("dn_insights_gc_collection_count", "Gc Collection Count by Process", labelNames: new[] { "process_id", "process"});

Gauge GcCollectionGen0MinSizeGauge = Metrics.CreateGauge("dn_insights_gc_collection_gen_0_min_size", "Gc Collection Gen0Size", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionLohSizeGauge = Metrics.CreateGauge("dn_insights_gc_collection_loh_size", "Gc Collection LOH Size", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionGen0SizeGauge = Metrics.CreateGauge("dn_insights_gc_collection_gen_0_size", "Gc Collection Gen0 Size", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionGen1SizeGauge = Metrics.CreateGauge("dn_insights_gc_collection_gen_1_size", "Gc Collection Gen1 Size", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionGen2SizeGauge = Metrics.CreateGauge("dn_insights_gc_collection_gen_2_size", "Gc Collection Gen2 Size", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionPauseTimeEndRelativeMsecGauge = Metrics.CreateGauge("dn_insights_gc_collection_pause_time_end_relative_msec", "Gc Collection PauseTimeEndRelative MSec", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionPauseTimeStartRelativeMSecGauge = Metrics.CreateGauge("dn_insights_gc_collection_pause_time_start_relative_msec", "Gc Collection PauseTimeStartRelative MSec", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionPauseTimeGauge = Metrics.CreateGauge("dn_insights_gc_collection_pause_time_msec", "Gc Collection Pause Time MSec", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionHeapSizeGauge = Metrics.CreateGauge("dn_insights_gc_collection_heap_size", "Gc Collection HeapSize", labelNames: new[] { "process_id", "gc_id" });

Gauge GcCollectionPromotedGauge = Metrics.CreateGauge("dn_insights_gc_collection_total_promoted", "Gc Collection Total Promoted Size", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionPromotedLohGauge = Metrics.CreateGauge("dn_insights_gc_collection_loh_promoted", "Gc Collection LOH Promoted Size", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionPromotedGen0Gauge = Metrics.CreateGauge("dn_insights_gc_collection_gen_0_promoted", "Gc Collection Gen 0 Promoted Size", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionPromotedGen1Gauge = Metrics.CreateGauge("dn_insights_gc_collection_gen_1_promoted", "Gc Collection Gen 1 Promoted Size", labelNames: new[] { "process_id", "gc_id" });
Gauge GcCollectionPromotedGen2Gauge = Metrics.CreateGauge("dn_insights_gc_collection_gen_2_promoted", "Gc Collection Gen 2 Promoted Size", labelNames: new[] { "process_id", "gc_id" });

Gauge GcCollectionHeapSizeBeforeGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_size_before", "Per Heap Data for Gc Collection - Size Before", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });
Gauge GcCollectionHeapSizeAfterGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_size_after", "Per Heap Data for Gc Collection - Size After", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });
Gauge GcCollectionHeapObjSpaceBeforeGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_obj_space_before", "Per Heap Data for Gc Collection - Object Space Before", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });

Gauge GcCollectionHeapFreeListSpaceBeforeGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_free_list_space_before", "Per Heap Data for Gc Collection - Free List Space Before", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });
Gauge GcCollectionHeapFreeListSpaceAfterGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_free_list_space_after", "Per Heap Data for Gc Collection - Free List Space after", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });

Gauge GcCollectionHeapFreeObjSizeBeforeGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_free_obj_size_before", "Per Heap Data for Gc Collection - Free Obj size Before", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });
Gauge GcCollectionHeapFreeObjSizeAfterGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_free_obj_size_after", "Per Heap Data for Gc Collection - Free Obj size after", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });

Gauge GcCollectionHeapObjSizeAfterGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_obj_size_after", "Per Heap Data for Gc Collection - Obj Size After", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });
Gauge GcCollectionHeapFragmentationGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_fragmentation", "Per Heap Data for Gc Collection - Fragmentation", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });

Gauge GcCollectionHeapInGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_in", "Per Heap Data for Gc Collection - In", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });
Gauge GcCollectionHeapOutGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_out", "Per Heap Data for Gc Collection - Out", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });

Gauge GcCollectionHeapNewAllocationSizeGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_new_allocation_size", "Per Heap Data for Gc Collection - New Allocation Size", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });
Gauge GcCollectionHeapSurvivalRateGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_survival_rate", "Per Heap Data for Gc Collection - Survival Rate", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });
Gauge GcCollectionHeapPinnedSurvivalGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_pinned_survival", "Per Heap Data for Gc Collection - Pinned Survival", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });
Gauge GcCollectionHeapNonPinnedSurvivalGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps_non_pinned_survival", "Per Heap Data for Gc Collection - Non Pinned Survival", labelNames: new [] { "process_id", "gc_id", "heap_index", "generation" });

void JitEventDataCallback(string processId, string processName, MethodJitInfo data)
{
    JitEventGauge.WithLabels(processId, processName, data.isTieredUp ? "yes" : "no", data.HasLoaded ? "loaded" : "not_loaded", data.MethodId.ToString(), data.Tier == 0 ? "0" : "1", data.MethodName).Set(data.LoadTime);
}

void GcAllocEventDataCallback(string processId, string processName, AllocationInfo data)
{
    GcAllocGauge.WithLabels(processId, processName, data.HeapIndex.ToString(), data.Kind == GCAllocationKind.Small ? "small" : "large", data.TypeName).Set(data.AllocSizeBytes);
}

void GcCollectEventDataCallback(string processId, string processName, GcInfo data)
{
    string reason = "unknown";
    if (data.Reason == GCReason.AllocSmall)
    {
        reason = "AllocSmall";
    }
    else if (data.Reason == GCReason.Induced)
    {
        reason = "Induced";
    }
    else if (data.Reason == GCReason.LowMemory)
    {
        reason = "LowMemory";
    }
    else if (data.Reason == GCReason.Empty)
    {
        reason = "Empty";
    }
    else if (data.Reason == GCReason.AllocLarge)
    {
        reason = "AllocLarge";
    }
    else if (data.Reason == GCReason.OutOfSpaceSOH)
    {
        reason = "OutOfSpaceSOH";
    }
    else if (data.Reason == GCReason.OutOfSpaceLOH)
    {
        reason = "OutOfSpaceLOH";
    }
    else if (data.Reason == GCReason.InducedNotForced)
    {
        reason = "InducedNotForced";
    }
    else if (data.Reason == GCReason.Internal)
    {
        reason = "Internal";
    }
    else if (data.Reason == GCReason.InducedLowMemory)
    {
        reason = "InducedLowMemory";
    }
    else if (data.Reason == GCReason.InducedCompacting)
    {
        reason = "InducedCompacting";
    }
    else if (data.Reason == GCReason.LowMemoryHost)
    {
        reason = "LowMemoryHost";
    }
    else if (data.Reason == GCReason.PMFullGC)
    {
        reason = "PMFullGC";
    }

    string kind = "unknown";
    if (data.Kind == GCKind.Any)
    {
        kind = "any";
    }
    else if (data.Kind == GCKind.Background)
    {
        kind = "background";
    }
    else if (data.Kind == GCKind.Ephemeral)
    {
        kind = "ephemeral";
    }
    else if (data.Kind == GCKind.FullBlocking)
    {
        kind = "full-blocking";
    }

    string gcId = data.Id.ToString();

    GcCollectionInfoGauge.WithLabels(processId, processName, gcId, data.Generation.ToString(), kind, reason, data.Type == GCType.BackgroundGC ? "Background" : "Blocking", data.NumHeaps.ToString()).Set(data.PauseDurationMSec);

    GcCollectionCounter.WithLabels(processId, processName).Inc();

    GcCollectionGen0MinSizeGauge.WithLabels(processId, gcId).Set(data.Gen0MinSize);
    GcCollectionLohSizeGauge.WithLabels(processId, gcId).Set(data.GenerationSizeLOH);
    GcCollectionGen0SizeGauge.WithLabels(processId, gcId).Set(data.GenerationSize0);
    GcCollectionGen1SizeGauge.WithLabels(processId, gcId).Set(data.GenerationSize1);
    GcCollectionGen2SizeGauge.WithLabels(processId, gcId).Set(data.GenerationSize2);
    GcCollectionPauseTimeEndRelativeMsecGauge.WithLabels(processId, gcId).Set(data.PauseEndRelativeMSec);
    GcCollectionPauseTimeStartRelativeMSecGauge.WithLabels(processId, gcId).Set(data.PauseStartRelativeMSec);
    GcCollectionHeapSizeGauge.WithLabels(processId, gcId).Set(data.TotalHeapSize);
    GcCollectionPromotedGauge.WithLabels(processId, gcId).Set(data.TotalPromoted);
    GcCollectionPromotedLohGauge.WithLabels(processId, gcId).Set(data.TotalPromotedLOH);
    GcCollectionPromotedGen0Gauge.WithLabels(processId, gcId).Set(data.TotalPromotedSize0);
    GcCollectionPromotedGen1Gauge.WithLabels(processId, gcId).Set(data.TotalPromotedSize1);
    GcCollectionPromotedGen2Gauge.WithLabels(processId, gcId).Set(data.TotalPromotedSize2);

    int index = 0;
    foreach (HeapInfo heap in data.Heaps)
    {
        string heapIndex = index.ToString();
        ++index;

        foreach (var gen in heap.GenData)
        {
            string generationNumber = gen.Id.ToString();

            GcCollectionHeapSizeBeforeGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.SizeBefore);
            GcCollectionHeapSizeAfterGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.SizeAfter);
            GcCollectionHeapObjSpaceBeforeGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.ObjSpaceBefore);
            GcCollectionHeapFreeListSpaceBeforeGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.FreeListSpaceBefore);
            GcCollectionHeapFreeListSpaceAfterGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.FreeListSpaceAfter);
            GcCollectionHeapFreeObjSizeBeforeGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.FreeObjSpaceBefore);
            GcCollectionHeapFreeObjSizeAfterGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.FreeObjSpaceAfter);
            GcCollectionHeapObjSizeAfterGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.ObjSizeAfter);
            GcCollectionHeapFragmentationGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.Fragmentation);
            GcCollectionHeapInGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.In);
            GcCollectionHeapOutGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.Out);
            GcCollectionHeapNewAllocationSizeGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.NewAllocation);
            GcCollectionHeapSurvivalRateGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.SurvRate);
            GcCollectionHeapPinnedSurvivalGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.PinnedSurv);
            GcCollectionHeapNonPinnedSurvivalGauge.WithLabels(processId, gcId, heapIndex, generationNumber).Set(gen.NonePinnedSurv);
        }
    }

}

using var server = new Prometheus.KestrelMetricServer(port: 1234);
server.Start();

var listener = new EventPipeBasedListener(listenForGcData: true, listenForAllocations: true, listenForJitEvents: true, JitEventDataCallback, GcAllocEventDataCallback, GcCollectEventDataCallback);
listener.Listen();

Console.WriteLine("Open http://localhost:1234/metrics in a web browser.");
Console.WriteLine("Press enter to exit.");
Console.ReadLine();