////////////////////////////////////////////////////////////////////////////////
// Module: ev31_profiler.cpp
////////////////////////////////////////////////////////////////////////////////

#include "ev31_profiler.hpp"

#include "globals.hpp"

////////////////////////////////////////////////////////////////////////////////
// Ctor / Dtor
////////////////////////////////////////////////////////////////////////////////

ev31::ev31_profiler::ev31_profiler() : ref_count(0), profiler_info(nullptr)
{
}

ev31::ev31_profiler::~ev31_profiler()
{
    if (this->profiler_info != nullptr)
    {
        this->profiler_info->Release();
        this->profiler_info = nullptr;
    }
}

////////////////////////////////////////////////////////////////////////////////
// Member methods
////////////////////////////////////////////////////////////////////////////////

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::Initialize(IUnknown *pICorProfilerInfoUnk)
{
    ::HRESULT queryInterfaceResult = pICorProfilerInfoUnk->QueryInterface(__uuidof(ICorProfilerInfo8), reinterpret_cast<void **>(&this->profiler_info));

    if (FAILED(queryInterfaceResult))
    {
        return E_FAIL;
    }

    DWORD eventMask = COR_PRF_MONITOR_ENTERLEAVE | COR_PRF_ENABLE_FUNCTION_ARGS | COR_PRF_ENABLE_FUNCTION_RETVAL | COR_PRF_ENABLE_FRAME_INFO | COR_PRF_MONITOR_JIT_COMPILATION;

    auto hr = this->profiler_info->SetEventMask(eventMask);
    if (hr != S_OK)
    {
        std::cout << "ERROR: Profiler SetEventMask failed (HRESULT: " << hr << ")";
    }

    hr = this->profiler_info->SetEnterLeaveFunctionHooks3WithInfo(EnterWrapper, ExitWrapper, TailcallWrapper);

    if (hr != S_OK)
    {
        std::cout << "ERROR: Profiler SetEnterLeaveFunctionHooks3WithInfo faled (HRESULT: " << hr << ")";
    }

    global_profiler = this;

    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::Shutdown()
{
    if (this->profiler_info != nullptr)
    {
        this->profiler_info->Release();
        this->profiler_info = nullptr;
    }

    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::AppDomainCreationStarted(AppDomainID appDomainId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::AppDomainCreationFinished(AppDomainID appDomainId, ::HRESULT hrStatus)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::AppDomainShutdownStarted(AppDomainID appDomainId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::AppDomainShutdownFinished(AppDomainID appDomainId, ::HRESULT hrStatus)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::AssemblyLoadStarted(AssemblyID assemblyId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::AssemblyLoadFinished(AssemblyID assemblyId, ::HRESULT hrStatus)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::AssemblyUnloadStarted(AssemblyID assemblyId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::AssemblyUnloadFinished(AssemblyID assemblyId, ::HRESULT hrStatus)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ModuleLoadStarted(ModuleID moduleId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ModuleLoadFinished(ModuleID moduleId, ::HRESULT hrStatus)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ModuleUnloadStarted(ModuleID moduleId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ModuleUnloadFinished(ModuleID moduleId, ::HRESULT hrStatus)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ModuleAttachedToAssembly(ModuleID moduleId, AssemblyID AssemblyId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ClassLoadStarted(ClassID classId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ClassLoadFinished(ClassID classId, ::HRESULT hrStatus)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ClassUnloadStarted(ClassID classId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ClassUnloadFinished(ClassID classId, ::HRESULT hrStatus)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::FunctionUnloadStarted(FunctionID functionId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::JITCompilationStarted(FunctionID functionId, BOOL fIsSafeToBlock)
{
    std::cout << "JITCompilationStarted " << (std::size_t)functionId << std::endl;

    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::JITCompilationFinished(FunctionID functionId, ::HRESULT hrStatus, BOOL fIsSafeToBlock)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::JITCachedFunctionSearchStarted(FunctionID functionId, BOOL *pbUseCachedFunction)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::JITCachedFunctionSearchFinished(FunctionID functionId, COR_PRF_JIT_CACHE result)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::JITFunctionPitched(FunctionID functionId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::JITInlining(FunctionID callerId, FunctionID calleeId, BOOL *pfShouldInline)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ThreadCreated(ThreadID threadId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ThreadDestroyed(ThreadID threadId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ThreadAssignedToOSThread(ThreadID managedThreadId, DWORD osThreadId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RemotingClientInvocationStarted()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RemotingClientSendingMessage(GUID *pCookie, BOOL fIsAsync)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RemotingClientReceivingReply(GUID *pCookie, BOOL fIsAsync)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RemotingClientInvocationFinished()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RemotingServerReceivingMessage(GUID *pCookie, BOOL fIsAsync)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RemotingServerInvocationStarted()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RemotingServerInvocationReturned()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RemotingServerSendingReply(GUID *pCookie, BOOL fIsAsync)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::UnmanagedToManagedTransition(FunctionID functionId, COR_PRF_TRANSITION_REASON reason)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ManagedToUnmanagedTransition(FunctionID functionId, COR_PRF_TRANSITION_REASON reason)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RuntimeSuspendStarted(COR_PRF_SUSPEND_REASON suspendReason)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RuntimeSuspendFinished()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RuntimeSuspendAborted()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RuntimeResumeStarted()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RuntimeResumeFinished()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RuntimeThreadSuspended(ThreadID threadId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RuntimeThreadResumed(ThreadID threadId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::MovedReferences(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[], ObjectID newObjectIDRangeStart[], ULONG cObjectIDRangeLength[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ObjectAllocated(ObjectID objectId, ClassID classId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ObjectsAllocatedByClass(ULONG cClassCount, ClassID classIds[], ULONG cObjects[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ObjectReferences(ObjectID objectId, ClassID classId, ULONG cObjectRefs, ObjectID objectRefIds[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RootReferences(ULONG cRootRefs, ObjectID rootRefIds[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionThrown(ObjectID thrownObjectId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionSearchFunctionEnter(FunctionID functionId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionSearchFunctionLeave()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionSearchFilterEnter(FunctionID functionId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionSearchFilterLeave()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionSearchCatcherFound(FunctionID functionId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionOSHandlerEnter(UINT_PTR __unused)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionOSHandlerLeave(UINT_PTR __unused)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionUnwindFunctionEnter(FunctionID functionId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionUnwindFunctionLeave()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionUnwindFinallyEnter(FunctionID functionId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionUnwindFinallyLeave()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionCatcherEnter(FunctionID functionId, ObjectID objectId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionCatcherLeave()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::COMClassicVTableCreated(ClassID wrappedClassId, REFGUID implementedIID, void *pVTable, ULONG cSlots)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::COMClassicVTableDestroyed(ClassID wrappedClassId, REFGUID implementedIID, void *pVTable)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionCLRCatcherFound()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ExceptionCLRCatcherExecute()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ThreadNameChanged(ThreadID threadId, ULONG cchName, WCHAR name[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::GarbageCollectionStarted(int cGenerations, BOOL generationCollected[], COR_PRF_GC_REASON reason)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::SurvivingReferences(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[], ULONG cObjectIDRangeLength[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::GarbageCollectionFinished()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::FinalizeableObjectQueued(DWORD finalizerFlags, ObjectID objectID)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::RootReferences2(ULONG cRootRefs, ObjectID rootRefIds[], COR_PRF_GC_ROOT_KIND rootKinds[], COR_PRF_GC_ROOT_FLAGS rootFlags[], UINT_PTR rootIds[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::HandleCreated(GCHandleID handleId, ObjectID initialObjectId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::HandleDestroyed(GCHandleID handleId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::InitializeForAttach(IUnknown *pCorProfilerInfoUnk, void *pvClientData, UINT cbClientData)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ProfilerAttachComplete()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ProfilerDetachSucceeded()
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ReJITCompilationStarted(FunctionID functionId, ReJITID rejitId, BOOL fIsSafeToBlock)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::GetReJITParameters(ModuleID moduleId, mdMethodDef methodId, ICorProfilerFunctionControl *pFunctionControl)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ReJITCompilationFinished(FunctionID functionId, ReJITID rejitId, ::HRESULT hrStatus, BOOL fIsSafeToBlock)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ReJITError(ModuleID moduleId, mdMethodDef methodId, FunctionID functionId, ::HRESULT hrStatus)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::MovedReferences2(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[], ObjectID newObjectIDRangeStart[], SIZE_T cObjectIDRangeLength[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::SurvivingReferences2(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[], SIZE_T cObjectIDRangeLength[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ConditionalWeakTableElementReferences(ULONG cRootRefs, ObjectID keyRefIds[], ObjectID valueRefIds[], GCHandleID rootIds[])
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::GetAssemblyReferences(const WCHAR *wszAssemblyPath, ICorProfilerAssemblyReferenceProvider *pAsmRefProvider)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::ModuleInMemorySymbolsUpdated(ModuleID moduleId)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::DynamicMethodJITCompilationStarted(FunctionID functionId, BOOL fIsSafeToBlock, LPCBYTE ilHeader, ULONG cbILHeader)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::DynamicMethodJITCompilationFinished(FunctionID functionId, ::HRESULT hrStatus, BOOL fIsSafeToBlock)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::ev31_profiler::QueryInterface(REFIID riid, void** ppvObject)
{
    if (riid == __uuidof(ICorProfilerCallback8) ||
        riid == __uuidof(ICorProfilerCallback7) ||
        riid == __uuidof(ICorProfilerCallback6) ||
        riid == __uuidof(ICorProfilerCallback5) ||
        riid == __uuidof(ICorProfilerCallback4) ||
        riid == __uuidof(ICorProfilerCallback3) ||
        riid == __uuidof(ICorProfilerCallback2) ||
        riid == __uuidof(ICorProfilerCallback)  ||
        riid == IID_IUnknown)
    {
        *ppvObject = this;
        this->AddRef();
        return S_OK;
    }

    *ppvObject = nullptr;
    return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE ev31::ev31_profiler::AddRef()
{
    return std::atomic_fetch_add(&this->ref_count, 1) + 1;
}

ULONG STDMETHODCALLTYPE ev31::ev31_profiler::Release()
{
    int count = std::atomic_fetch_sub(&this->ref_count, 1) - 1;

    if (count <= 0)
    {
        delete this;
    }

    return count;
}

void ev31::ev31_profiler::EnterMethod(FunctionIDOrClientID function_id, COR_PRF_ELT_INFO elt_info)
{
    std::wstring method_name = this->get_method_name(function_id.functionID);
    method_tracker.start_method_timing((std::size_t)function_id.functionID, method_name);
}

void ev31::ev31_profiler::LeaveMethod(FunctionID function_id, COR_PRF_ELT_INFO elt_info)
{
    std::wstring method_name = this->get_method_name(function_id);
    double time = method_tracker.stop_method_timing(function_id, method_name);

    std::cout << "Exit " << (std::size_t)function_id << " " << time << std::endl;
}

////////////////////////////////////////////////////////////////////////////////
// Private member methods
////////////////////////////////////////////////////////////////////////////////

const std::wstring ev31::ev31_profiler::get_method_name(FunctionID function_id)
{
    ClassID class_id = 0;
    ModuleID module_id = 0;
    mdToken token = 0;

    IMetaDataImport* metadata_import = nullptr;

    this->profiler_info->GetFunctionInfo(function_id, &class_id, &module_id, &token);
    this->profiler_info->GetModuleMetaData(module_id, ofRead, IID_IMetaDataImport, (IUnknown**)&metadata_import);

    std::wstring return_value;
    return_value.reserve(1024);

    WCHAR name[1024];

    std::size_t name_size = 1024;
    ULONG copied_count = 0;

    metadata_import->GetMethodProps(token, nullptr, name, name_size, &copied_count, nullptr, nullptr, nullptr, nullptr, nullptr);

    for (std::size_t index = 0; index < copied_count; ++index)
    {
        return_value += name[index];
    }

    return return_value;
}