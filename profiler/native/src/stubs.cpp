#include "CComPtr.hpp"
#include "cor.h"
#include "corhlpr.h"
#include "corprof.h"
#include "profiler_pal.h"

#include <iostream>

////////////////////////////////////////////////////////////////////////////////
// Stubs
////////////////////////////////////////////////////////////////////////////////

PROFILER_STUB EnterStub(FunctionIDOrClientID functionId, COR_PRF_ELT_INFO eltInfo)
{
    std::cout << std::endl << "Enter " << (UINT64)functionId.functionID;
}

PROFILER_STUB ExitStub(FunctionID functionId, COR_PRF_ELT_INFO eltInfo)
{
    std::cout << "Exit " << (UINT64)functionId;
}

PROFILER_STUB TailcallStub(FunctionID functionId, COR_PRF_ELT_INFO eltInfo)
{
    std::cout << "Tailcall " << (UINT64)functionId;
}