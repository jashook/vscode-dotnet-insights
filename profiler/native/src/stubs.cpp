////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#include "CComPtr.hpp"
#include "cor.h"
#include "corhlpr.h"
#include "corprof.h"
#include "profiler_pal.h"

#include "globals.hpp"

#include <iostream>

////////////////////////////////////////////////////////////////////////////////
// Stubs
////////////////////////////////////////////////////////////////////////////////

PROFILER_STUB EnterStub(FunctionIDOrClientID function_id, COR_PRF_ELT_INFO elt_info)
{
    global_profiler->EnterMethod(function_id, elt_info);
}

PROFILER_STUB ExitStub(FunctionID function_id, COR_PRF_ELT_INFO elt_info)
{
    global_profiler->LeaveMethod(function_id, elt_info);
}

PROFILER_STUB TailcallStub(FunctionID function_id, COR_PRF_ELT_INFO elt_info)
{
}