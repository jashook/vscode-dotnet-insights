////////////////////////////////////////////////////////////////////////////////
// Module: class_factor.cpp
//
////////////////////////////////////////////////////////////////////////////////

#include "class_factory.hpp"

////////////////////////////////////////////////////////////////////////////////
// Ctor / Dtor
////////////////////////////////////////////////////////////////////////////////

ev31::class_factory::class_factory() : ref_count(0) { }
ev31::class_factory::~class_factory() { }

////////////////////////////////////////////////////////////////////////////////
// Member methods
////////////////////////////////////////////////////////////////////////////////

::ULONG STDMETHODCALLTYPE ev31::class_factory::AddRef()
{
    return std::atomic_fetch_add(&this->ref_count, 1) + 1;
}

::HRESULT STDMETHODCALLTYPE ev31::class_factory::CreateInstance(IUnknown *pUnkOuter, REFIID riid, void **ppvObject)
{
    if (pUnkOuter != nullptr)
    {
        *ppvObject = nullptr;
        return CLASS_E_NOAGGREGATION;
    }

    ev31_profiler* profiler = new ev31_profiler();
    if (profiler == nullptr)
    {
        return E_FAIL;
    }

    return profiler->QueryInterface(riid, ppvObject);
}

::HRESULT STDMETHODCALLTYPE ev31::class_factory::LockServer(::BOOL fLock)
{
    return S_OK;
}

::HRESULT STDMETHODCALLTYPE ev31::class_factory::QueryInterface(REFIID riid, void** ppvObject)
{
    if (riid == IID_IUnknown || riid == IID_IClassFactory)
    {
        *ppvObject = this;
        this->AddRef();
        return S_OK;
    }

    *ppvObject = nullptr;
    return E_NOINTERFACE;
}

::ULONG STDMETHODCALLTYPE ev31::class_factory::Release()
{
    int count = std::atomic_fetch_sub(&this->ref_count, 1) - 1;
    if (count <= 0)
    {
        delete this;
    }

    return count;
}