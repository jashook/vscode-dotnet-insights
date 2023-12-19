////////////////////////////////////////////////////////////////////////////////
// Module: ev31_profiler.hpp
////////////////////////////////////////////////////////////////////////////////

#ifndef __EV31_PROFILER_HPP__
#define __EV31_PROFILER_HPP__

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#include "CComPtr.hpp"
#include "cor.h"
#include "corhlpr.h"
#include "corprof.h"
#include "profiler_pal.h"

#include <algorithm>
#include <atomic>
#include <iostream>
#include <iterator>
#include <string>
#include <sstream>

#include "assembly_tracker.hpp"
#include "method_tracker.hpp"
#include "profiler_io.hpp"

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

EXTERN_C void EnterWrapper(FunctionIDOrClientID functionIDOrClientID, COR_PRF_ELT_INFO eltInfo);
EXTERN_C void ExitWrapper(FunctionIDOrClientID functionIDOrClientID, COR_PRF_ELT_INFO eltInfo);
EXTERN_C void TailcallWrapper(FunctionIDOrClientID functionIDOrClientID, COR_PRF_ELT_INFO eltInfo);

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace ev31 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

class ev31_profiler : public ICorProfilerCallback8
{
    // Ctor / Dtor
    public:
        ev31_profiler();
        virtual ~ev31_profiler();
    
    // Member methods
    public:
        ::HRESULT STDMETHODCALLTYPE Initialize(IUnknown* pICorProfilerInfoUnk) override;
        ::HRESULT STDMETHODCALLTYPE Shutdown() override;
        ::HRESULT STDMETHODCALLTYPE AppDomainCreationStarted(AppDomainID appDomainId) override;
        ::HRESULT STDMETHODCALLTYPE AppDomainCreationFinished(AppDomainID appDomainId, ::HRESULT hrStatus) override;
        ::HRESULT STDMETHODCALLTYPE AppDomainShutdownStarted(AppDomainID appDomainId) override;
        ::HRESULT STDMETHODCALLTYPE AppDomainShutdownFinished(AppDomainID appDomainId, ::HRESULT hrStatus) override;
        ::HRESULT STDMETHODCALLTYPE AssemblyLoadStarted(AssemblyID assemblyId) override;
        ::HRESULT STDMETHODCALLTYPE AssemblyLoadFinished(AssemblyID assemblyId, ::HRESULT hrStatus) override;
        ::HRESULT STDMETHODCALLTYPE AssemblyUnloadStarted(AssemblyID assemblyId) override;
        ::HRESULT STDMETHODCALLTYPE AssemblyUnloadFinished(AssemblyID assemblyId, ::HRESULT hrStatus) override;
        ::HRESULT STDMETHODCALLTYPE ModuleLoadStarted(ModuleID moduleId) override;
        ::HRESULT STDMETHODCALLTYPE ModuleLoadFinished(ModuleID moduleId, ::HRESULT hrStatus) override;
        ::HRESULT STDMETHODCALLTYPE ModuleUnloadStarted(ModuleID moduleId) override;
        ::HRESULT STDMETHODCALLTYPE ModuleUnloadFinished(ModuleID moduleId, ::HRESULT hrStatus) override;
        ::HRESULT STDMETHODCALLTYPE ModuleAttachedToAssembly(ModuleID moduleId, AssemblyID AssemblyId) override;
        ::HRESULT STDMETHODCALLTYPE ClassLoadStarted(ClassID classId) override;
        ::HRESULT STDMETHODCALLTYPE ClassLoadFinished(ClassID classId, ::HRESULT hrStatus) override;
        ::HRESULT STDMETHODCALLTYPE ClassUnloadStarted(ClassID classId) override;
        ::HRESULT STDMETHODCALLTYPE ClassUnloadFinished(ClassID classId, ::HRESULT hrStatus) override;
        ::HRESULT STDMETHODCALLTYPE FunctionUnloadStarted(FunctionID functionId) override;
        ::HRESULT STDMETHODCALLTYPE JITCompilationStarted(FunctionID functionId, BOOL fIsSafeToBlock) override;
        ::HRESULT STDMETHODCALLTYPE JITCompilationFinished(FunctionID functionId, ::HRESULT hrStatus, BOOL fIsSafeToBlock) override;
        ::HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchStarted(FunctionID functionId, BOOL* pbUseCachedFunction) override;
        ::HRESULT STDMETHODCALLTYPE JITCachedFunctionSearchFinished(FunctionID functionId, COR_PRF_JIT_CACHE result) override;
        ::HRESULT STDMETHODCALLTYPE JITFunctionPitched(FunctionID functionId) override;
        ::HRESULT STDMETHODCALLTYPE JITInlining(FunctionID callerId, FunctionID calleeId, BOOL* pfShouldInline) override;
        ::HRESULT STDMETHODCALLTYPE ThreadCreated(ThreadID threadId) override;
        ::HRESULT STDMETHODCALLTYPE ThreadDestroyed(ThreadID threadId) override;
        ::HRESULT STDMETHODCALLTYPE ThreadAssignedToOSThread(ThreadID managedThreadId, DWORD osThreadId) override;
        ::HRESULT STDMETHODCALLTYPE RemotingClientInvocationStarted() override;
        ::HRESULT STDMETHODCALLTYPE RemotingClientSendingMessage(GUID* pCookie, BOOL fIsAsync) override;
        ::HRESULT STDMETHODCALLTYPE RemotingClientReceivingReply(GUID* pCookie, BOOL fIsAsync) override;
        ::HRESULT STDMETHODCALLTYPE RemotingClientInvocationFinished() override;
        ::HRESULT STDMETHODCALLTYPE RemotingServerReceivingMessage(GUID* pCookie, BOOL fIsAsync) override;
        ::HRESULT STDMETHODCALLTYPE RemotingServerInvocationStarted() override;
        ::HRESULT STDMETHODCALLTYPE RemotingServerInvocationReturned() override;
        ::HRESULT STDMETHODCALLTYPE RemotingServerSendingReply(GUID* pCookie, BOOL fIsAsync) override;
        ::HRESULT STDMETHODCALLTYPE UnmanagedToManagedTransition(FunctionID functionId, COR_PRF_TRANSITION_REASON reason) override;
        ::HRESULT STDMETHODCALLTYPE ManagedToUnmanagedTransition(FunctionID functionId, COR_PRF_TRANSITION_REASON reason) override;
        ::HRESULT STDMETHODCALLTYPE RuntimeSuspendStarted(COR_PRF_SUSPEND_REASON suspendReason) override;
        ::HRESULT STDMETHODCALLTYPE RuntimeSuspendFinished() override;
        ::HRESULT STDMETHODCALLTYPE RuntimeSuspendAborted() override;
        ::HRESULT STDMETHODCALLTYPE RuntimeResumeStarted() override;
        ::HRESULT STDMETHODCALLTYPE RuntimeResumeFinished() override;
        ::HRESULT STDMETHODCALLTYPE RuntimeThreadSuspended(ThreadID threadId) override;
        ::HRESULT STDMETHODCALLTYPE RuntimeThreadResumed(ThreadID threadId) override;
        ::HRESULT STDMETHODCALLTYPE MovedReferences(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[], ObjectID newObjectIDRangeStart[], ULONG cObjectIDRangeLength[]) override;
        ::HRESULT STDMETHODCALLTYPE ObjectAllocated(ObjectID objectId, ClassID classId) override;
        ::HRESULT STDMETHODCALLTYPE ObjectsAllocatedByClass(ULONG cClassCount, ClassID classIds[], ULONG cObjects[]) override;
        ::HRESULT STDMETHODCALLTYPE ObjectReferences(ObjectID objectId, ClassID classId, ULONG cObjectRefs, ObjectID objectRefIds[]) override;
        ::HRESULT STDMETHODCALLTYPE RootReferences(ULONG cRootRefs, ObjectID rootRefIds[]) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionThrown(ObjectID thrownObjectId) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionEnter(FunctionID functionId) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionSearchFunctionLeave() override;
        ::HRESULT STDMETHODCALLTYPE ExceptionSearchFilterEnter(FunctionID functionId) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionSearchFilterLeave() override;
        ::HRESULT STDMETHODCALLTYPE ExceptionSearchCatcherFound(FunctionID functionId) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionOSHandlerEnter(UINT_PTR __unused) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionOSHandlerLeave(UINT_PTR __unused) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionEnter(FunctionID functionId) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionUnwindFunctionLeave() override;
        ::HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyEnter(FunctionID functionId) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionUnwindFinallyLeave() override;
        ::HRESULT STDMETHODCALLTYPE ExceptionCatcherEnter(FunctionID functionId, ObjectID objectId) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionCatcherLeave() override;
        ::HRESULT STDMETHODCALLTYPE COMClassicVTableCreated(ClassID wrappedClassId, REFGUID implementedIID, void* pVTable, ULONG cSlots) override;
        ::HRESULT STDMETHODCALLTYPE COMClassicVTableDestroyed(ClassID wrappedClassId, REFGUID implementedIID, void* pVTable) override;
        ::HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherFound() override;
        ::HRESULT STDMETHODCALLTYPE ExceptionCLRCatcherExecute() override;
        ::HRESULT STDMETHODCALLTYPE ThreadNameChanged(ThreadID threadId, ULONG cchName, WCHAR name[]) override;
        ::HRESULT STDMETHODCALLTYPE GarbageCollectionStarted(int cGenerations, BOOL generationCollected[], COR_PRF_GC_REASON reason) override;
        ::HRESULT STDMETHODCALLTYPE SurvivingReferences(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[], ULONG cObjectIDRangeLength[]) override;
        ::HRESULT STDMETHODCALLTYPE GarbageCollectionFinished() override;
        ::HRESULT STDMETHODCALLTYPE FinalizeableObjectQueued(DWORD finalizerFlags, ObjectID objectID) override;
        ::HRESULT STDMETHODCALLTYPE RootReferences2(ULONG cRootRefs, ObjectID rootRefIds[], COR_PRF_GC_ROOT_KIND rootKinds[], COR_PRF_GC_ROOT_FLAGS rootFlags[], UINT_PTR rootIds[]) override;
        ::HRESULT STDMETHODCALLTYPE HandleCreated(GCHandleID handleId, ObjectID initialObjectId) override;
        ::HRESULT STDMETHODCALLTYPE HandleDestroyed(GCHandleID handleId) override;
        ::HRESULT STDMETHODCALLTYPE InitializeForAttach(IUnknown* pCorProfilerInfoUnk, void* pvClientData, UINT cbClientData) override;
        ::HRESULT STDMETHODCALLTYPE ProfilerAttachComplete() override;
        ::HRESULT STDMETHODCALLTYPE ProfilerDetachSucceeded() override;
        ::HRESULT STDMETHODCALLTYPE ReJITCompilationStarted(FunctionID functionId, ReJITID rejitId, BOOL fIsSafeToBlock) override;
        ::HRESULT STDMETHODCALLTYPE GetReJITParameters(ModuleID moduleId, mdMethodDef methodId, ICorProfilerFunctionControl* pFunctionControl) override;
        ::HRESULT STDMETHODCALLTYPE ReJITCompilationFinished(FunctionID functionId, ReJITID rejitId, ::HRESULT hrStatus, BOOL fIsSafeToBlock) override;
        ::HRESULT STDMETHODCALLTYPE ReJITError(ModuleID moduleId, mdMethodDef methodId, FunctionID functionId, ::HRESULT hrStatus) override;
        ::HRESULT STDMETHODCALLTYPE MovedReferences2(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[], ObjectID newObjectIDRangeStart[], SIZE_T cObjectIDRangeLength[]) override;
        ::HRESULT STDMETHODCALLTYPE SurvivingReferences2(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[], SIZE_T cObjectIDRangeLength[]) override;
        ::HRESULT STDMETHODCALLTYPE ConditionalWeakTableElementReferences(ULONG cRootRefs, ObjectID keyRefIds[], ObjectID valueRefIds[], GCHandleID rootIds[]) override;
        ::HRESULT STDMETHODCALLTYPE GetAssemblyReferences(const WCHAR* wszAssemblyPath, ICorProfilerAssemblyReferenceProvider* pAsmRefProvider) override;
        ::HRESULT STDMETHODCALLTYPE ModuleInMemorySymbolsUpdated(ModuleID moduleId) override;

        ::HRESULT STDMETHODCALLTYPE DynamicMethodJITCompilationStarted(FunctionID functionId, BOOL fIsSafeToBlock, LPCBYTE ilHeader, ULONG cbILHeader) override;
        ::HRESULT STDMETHODCALLTYPE DynamicMethodJITCompilationFinished(FunctionID functionId, ::HRESULT hrStatus, BOOL fIsSafeToBlock) override;

        ::HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **ppvObject) override;
        ULONG STDMETHODCALLTYPE AddRef(void) override;
        ULONG STDMETHODCALLTYPE Release(void) override;

        void EnterMethod(FunctionIDOrClientID function_id, COR_PRF_ELT_INFO elt_info);
        void LeaveMethod(FunctionID function_id, COR_PRF_ELT_INFO elt_info);

    // Private member methods
    private:
        const std::wstring get_assembly_name(AssemblyID assembly_id);
        const std::wstring get_method_name(FunctionID function_id, bool is_dynamic_method=false);

    // Member variables
    private:
        std::atomic<int> ref_count;
        ev31::assembly_tracker assembly_tracker;
        ev31::method_tracker method_tracker;
        ICorProfilerInfo8* profiler_info;
        // ev31::profiler_io io;
}; // (ev31_profiler)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // namespace (ev31)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#endif // __EV31_PROFILER_HPP__

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////