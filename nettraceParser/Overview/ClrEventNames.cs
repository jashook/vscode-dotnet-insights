////////////////////////////////////////////////////////////////////////////////
// Module: ClrEventNames.cs
//
// Notes:
// EventId -> human-readable name for the two CLR providers, used by
// EventOverviewBuilder.cs to label the Overview tab's per-event-type
// breakdown. Without this, every manifest-based CLR event shows up as a
// bare "EventID 30" - technically honest but useless for actually
// understanding what a capture contains.
//
// Names are "Task/Opcode" exactly as Microsoft.Diagnostics.Tracing.TraceEvent
// reports them (e.g. "GC/AllocationTick", "Exception/Start"), which is also
// what PerfView and dotnet-trace display - so a name shown here can be
// cross-referenced against those tools directly rather than being a
// third, private naming scheme this repo invented.
//
// GENERATED, not hand-written: every entry below was enumerated from
// TraceEvent 3.2.5's own ClrTraceEventParser/ClrRundownTraceEventParser
// templates (via their EnumerateTemplates hook) and emitted mechanically,
// rather than transcribed by hand from a manifest - the same "read
// TraceEvent as reference, never take it as a dependency" convention
// Gc/ClrGcTypes.cs and Rundown/ClrMethodRundown.cs already follow.
// nettraceParser itself still has no TraceEvent dependency; only the
// throwaway generator did.
//
// Verified against a real 737MB/4.29M-event capture: every EventId actually
// observed in it (24 distinct runtime + 10 distinct rundown) resolves to a
// real name here, with no "EventID {n}" fallbacks left in the Overview.
//
// These tables are intentionally COMPLETE rather than limited to the events
// this parser decodes - the Overview's whole job is to report what's in the
// file, including the (many) event types nettraceParser has no decoder for.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Overview {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class ClrEventNames
{
    public const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";
    public const string ClrRundownProviderName = "Microsoft-Windows-DotNETRuntimeRundown";

    // Microsoft-Windows-DotNETRuntime
    private static readonly Dictionary<int, string> RuntimeEventNames = new Dictionary<int, string>
    {
        { 1, "GC/Start" },
        { 2, "GC/Stop" },
        { 3, "GC/RestartEEStop" },
        { 4, "GC/HeapStats" },
        { 5, "GC/CreateSegment" },
        { 6, "GC/FreeSegment" },
        { 7, "GC/RestartEEStart" },
        { 8, "GC/SuspendEEStop" },
        { 9, "GC/SuspendEEStart" },
        { 10, "GC/AllocationTick" },
        { 11, "GC/CreateConcurrentThread" },
        { 12, "GC/TerminateConcurrentThread" },
        { 13, "GC/FinalizersStop" },
        { 14, "GC/FinalizersStart" },
        { 15, "Type/BulkType" },
        { 16, "GC/BulkRootEdge" },
        { 17, "GC/BulkRootConditionalWeakTableElementEdge" },
        { 18, "GC/BulkNode" },
        { 19, "GC/BulkEdge" },
        { 20, "GC/SampledObjectAllocation" },
        { 21, "GC/BulkSurvivingObjectRanges" },
        { 22, "GC/BulkMovedObjectRanges" },
        { 23, "GC/GenerationRange" },
        { 25, "GC/MarkStackRoots" },
        { 26, "GC/MarkFinalizeQueueRoots" },
        { 27, "GC/MarkHandles" },
        { 28, "GC/MarkCards" },
        { 29, "GC/FinalizeObject" },
        { 30, "GC/SetGCHandle" },
        { 31, "GC/DestoryGCHandle" },
        { 32, "GC/SampledObjectAllocation" },
        { 33, "GC/PinObjectAtGCTime" },
        { 34, "GC/PinPlugAtGCTime" },
        { 35, "GC/Triggered" },
        { 36, "GC/BulkRootCCW" },
        { 37, "GC/BulkRCW" },
        { 38, "GC/BulkRootStaticVar" },
        { 44, "IOThreadCreation/Start" },
        { 45, "IOThreadCreation/Stop" },
        { 46, "IOThreadRetirement/Start" },
        { 47, "IOThreadRetirement/Stop" },
        { 50, "ThreadPoolWorkerThread/Start" },
        { 51, "ThreadPoolWorkerThread/Stop" },
        { 52, "ThreadPoolWorkerThreadRetirement/Start" },
        { 53, "ThreadPoolWorkerThreadRetirement/Stop" },
        { 54, "ThreadPoolWorkerThreadAdjustment/Sample" },
        { 55, "ThreadPoolWorkerThreadAdjustment/Adjustment" },
        { 56, "ThreadPoolWorkerThreadAdjustment/Stats" },
        { 57, "ThreadPoolWorkerThread/Wait" },
        { 58, "YieldProcessorMeasurement" },
        { 59, "ThreadPoolMinMaxThreads" },
        { 60, "ThreadPoolWorkingThreadCount/Start" },
        { 61, "ThreadPool/Enqueue" },
        { 62, "ThreadPool/Dequeue" },
        { 63, "ThreadPool/IOEnqueue" },
        { 64, "ThreadPool/IODequeue" },
        { 65, "ThreadPool/IOPack" },
        { 70, "Thread/Creating" },
        { 71, "Thread/Running" },
        { 72, "Method/MethodDetails" },
        { 73, "TypeLoad/Start" },
        { 74, "TypeLoad/Stop" },
        { 80, "Exception/Start" },
        { 81, "Contention/Start" },
        { 82, "ClrStack/Walk" },
        { 83, "AppDomainResourceManagement/MemAllocated" },
        { 84, "AppDomainResourceManagement/MemSurvived" },
        { 85, "AppDomainResourceManagement/ThreadCreated" },
        { 86, "AppDomainResourceManagement/ThreadTerminated" },
        { 87, "AppDomainResourceManagement/DomainEnter" },
        { 88, "ILStub/StubGenerated" },
        { 89, "ILStub/StubCacheHit" },
        { 90, "Contention/LockCreated" },
        { 91, "Contention/Stop" },
        { 135, "Method/DCStartCompleteV2" },
        { 136, "Method/DCStopCompleteV2" },
        { 137, "Method/DCStartV2" },
        { 138, "Method/DCStopV2" },
        { 139, "Method/DCStartVerboseV2" },
        { 140, "Method/DCStopVerboseV2" },
        { 141, "Method/Load" },
        { 142, "Method/Unload" },
        { 143, "Method/LoadVerbose" },
        { 144, "Method/UnloadVerbose" },
        { 145, "Method/JittingStarted" },
        { 146, "Method/MemoryAllocatedForJitCode" },
        { 149, "Loader/ModuleDCStartV2" },
        { 150, "Loader/ModuleDCStopV2" },
        { 151, "Loader/DomainModuleLoad" },
        { 152, "Loader/ModuleLoad" },
        { 153, "Loader/ModuleUnload" },
        { 154, "Loader/AssemblyLoad" },
        { 155, "Loader/AssemblyUnload" },
        { 156, "Loader/AppDomainLoad" },
        { 157, "Loader/AppDomainUnload" },
        { 159, "Method/R2RGetEntryPoint" },
        { 160, "Method/R2RGetEntryPointStart" },
        { 181, "StrongNameVerification/Start" },
        { 182, "StrongNameVerification/Stop" },
        { 183, "AuthenticodeVerification/Start" },
        { 184, "AuthenticodeVerification/Stop" },
        { 185, "Method/InliningSucceeded" },
        { 186, "Method/InliningFailedAnsi" },
        { 187, "Runtime/Start" },
        { 188, "Method/TailCallSucceeded" },
        { 189, "Method/TailCallFailed" },
        { 190, "Method/ILToNativeMap" },
        { 192, "Method/InliningFailed" },
        { 202, "GC/MarkWithType" },
        { 203, "GC/Join" },
        { 204, "GC/PerHeapHistory" },
        { 205, "GC/GlobalHeapHistory" },
        { 206, "GC/GenAwareBegin" },
        { 207, "GC/GenAwareEnd" },
        { 208, "GC/LOHCompact" },
        { 209, "GC/FitBucketInfo" },
        { 250, "ExceptionCatch/Start" },
        { 251, "ExceptionCatch/Stop" },
        { 252, "ExceptionFinally/Start" },
        { 253, "ExceptionFinally/Stop" },
        { 254, "ExceptionFilter/Start" },
        { 255, "ExceptionFilter/Stop" },
        { 256, "Exception/Stop" },
        { 260, "CodeSymbols/Start" },
        { 270, "EventSourceEvent" },
        { 280, "TieredCompilation/Settings" },
        { 281, "TieredCompilation/Pause" },
        { 282, "TieredCompilation/Resume" },
        { 283, "TieredCompilation/BackgroundJitStart" },
        { 284, "TieredCompilation/BackgroundJitStop" },
        { 290, "AssemblyLoader/Start" },
        { 291, "AssemblyLoader/Stop" },
        { 292, "AssemblyLoader/ResolutionAttempted" },
        { 293, "AssemblyLoader/AssemblyLoadContextResolvingHandlerInvoked" },
        { 294, "AssemblyLoader/AppDomainAssemblyResolveHandlerInvoked" },
        { 295, "AssemblyLoader/AssemblyLoadFromResolveHandlerInvoked" },
        { 296, "AssemblyLoader/KnownPathProbed" },
        { 297, "JitInstrumentationData/InstrumentationData" },
        { 298, "JitInstrumentationDataVerbose/InstrumentationData" },
        { 300, "ExecutionCheckpoint/ExecutionCheckpoint" },
        { 301, "WaitHandleWait/Start" },
        { 302, "WaitHandleWait/Stop" },
        { 303, "AllocationSampling" },
        { 65535, "GC/RestartEEStop" },
    };

    // Microsoft-Windows-DotNETRuntimeRundown - note this is a genuinely
    // DIFFERENT id space from the runtime provider above, not an extension
    // of it: id 144 means "Method/UnloadVerbose" on the runtime provider but
    // "Method/DCStopVerbose" on this one, so the two must never share a
    // lookup table.
    private static readonly Dictionary<int, string> RundownEventNames = new Dictionary<int, string>
    {
        { 0, "ClrStack/Walk" },
        { 10, "GC/SettingsRundown" },
        { 141, "Method/DCStart" },
        { 142, "Method/DCStop" },
        { 143, "Method/DCStartVerbose" },
        { 144, "Method/DCStopVerbose" },
        { 145, "Method/DCStartComplete" },
        { 146, "Method/DCStopComplete" },
        { 147, "Method/DCStartInit" },
        { 148, "Method/DCStopInit" },
        { 149, "Method/ILToNativeMapDCStart" },
        { 150, "Method/ILToNativeMapDCStop" },
        { 151, "Loader/DomainModuleDCStart" },
        { 152, "Loader/DomainModuleDCStop" },
        { 153, "Loader/ModuleDCStart" },
        { 154, "Loader/ModuleDCStop" },
        { 155, "Loader/AssemblyDCStart" },
        { 156, "Loader/AssemblyDCStop" },
        { 157, "Loader/AppDomainDCStart" },
        { 158, "Loader/AppDomainDCStop" },
        { 159, "Loader/ThreadDCStop" },
        { 187, "Runtime/Start" },
        { 188, "CodeSymbolsRundown/Start" },
        { 280, "TieredCompilationRundown/SettingsDCStart" },
        { 300, "ExecutionCheckpointRundown/ExecutionCheckpointDCEnd" },
    };

    // True (with name set) only for a known provider+id pair. Callers are
    // expected to fall back to their own "EventID {n}" style placeholder -
    // this deliberately does not invent one, so the caller controls how an
    // unknown event is presented.
    public static bool TryGetName(string providerName, int eventId, out string name)
    {
        if (providerName == ClrProviderName)
        {
            return RuntimeEventNames.TryGetValue(eventId, out name);
        }

        if (providerName == ClrRundownProviderName)
        {
            return RundownEventNames.TryGetValue(eventId, out name);
        }

        name = null;
        return false;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Overview)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
