using DotnetInsights;
using Prometheus;

Gauge GcAllocGauge = Metrics.CreateGauge("dn_insights_gc_alloc", "Gc Allocation Event", labelNames: new[] { "process_id", "process", "heap_index", "kind", "alloc_size_bytes", "type_name" });
Gauge JitEventGauge = Metrics.CreateGauge("dn_insights_jit_event", "Jit Event", labelNames: new[] { "process_id", "process", "is_tiered_up", "has_loaded", "method_id", "tier", "method_name" }); // value is load time
Gauge GcCollectionGauge = Metrics.CreateGauge("dn_insights_gc_collection", "Gc Collection Event", labelNames: new[] { "process_id", "process", "heap_index", "kind", "generation", "gen_0_min_size", "generation_size_loh", "generation_size_0", "generation_size_1", "generation_size_2", "id", "num_heaps", "pause_end_relative_msec", "pause_start_relative_msec", "pause_duration_msec", "reason", "total_heap_size", "total_promoted", "total_promoted_loh", "total_promoted_size_0", "total_promoted_size_1", "total_promoted_size_2", "type" });
Gauge GcCollectionHeapGauge = Metrics.CreateGauge("dn_insights_gc_collection_heaps", "Per Heap Data for Gc Collection", labelNames: new [] { "process_id", "id", "index", "gen", "size_before", "size_after", "obj_space_before", "fragmentation", "free_list_space_before", "free_list_space_after", "free_obj_space_before", "free_obj_space_after", "obj_size_after", "in", "out", "new_allocation", "surv_rate", "pinned_surv", "none_pinned_surv"});

void JitEventDataCallback(string processId, string processName, MethodJitInfo data)
{
    JitEventGauge.WithLabels(processId, processName, data.isTieredUp ? "tier 1" : "tier 0", data.HasLoaded ? "loaded" : "not_loaded", data.MethodId.ToString(), data.Tier == 0 ? "0" : "1", data.MethodName).Set(data.LoadTime);
}

void GcAllocEventDataCallback(string processId, string processName, AllocationInfo data)
{
    
}

void GcCollectEventDataCallback(string processId, string processName, GcInfo data)
{
    
}

using var server = new Prometheus.KestrelMetricServer(port: 1234);
server.Start();

var listener = new EventPipeBasedListener(listenForGcData: true, listenForAllocations: true, listenForJitEvents: true, JitEventDataCallback, GcAllocEventDataCallback, GcCollectEventDataCallback);
listener.Listen();

Console.WriteLine("Open http://localhost:1234/metrics in a web browser.");
Console.WriteLine("Press enter to exit.");
Console.ReadLine();