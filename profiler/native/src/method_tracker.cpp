////////////////////////////////////////////////////////////////////////////////
// Module: method_tracker.cpp
//
////////////////////////////////////////////////////////////////////////////////

#include "method_tracker.hpp"

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

ev31::method_tracker::method_tracker()
{

}

ev31::method_tracker::~method_tracker()
{

}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

const ev31::method_info* ev31::method_tracker::get_method_info_for_name(const std::wstring& method_name)
{
    ev31::method_info* method_found = nullptr;
    if (this->method_map.find(method_name) != this->method_map.end())
    {
        method_found = (this->method_map.find(method_name))->second;
    }
    else
    {
        // We will create a new method_info structure and pack it
        method_found = new ev31::method_info(method_name);
        this->method_map.insert({ method_name, method_found });
    }

    return method_found;
}

void ev31::method_tracker::start_method_timing(const std::size_t profiler_method_id,
                                               const std::wstring& method_name)
{
    // Get the method map
    std::unordered_map<std::size_t, std::chrono::steady_clock::time_point>* method_map = nullptr;
    
    if (this->method_timing_map.find(method_name) != this->method_timing_map.end())
    {
        method_map = (this->method_timing_map.find(method_name))->second;
    }
    else
    {
        // First time we have found, create it
        method_map = new std::unordered_map<std::size_t, std::chrono::steady_clock::time_point>();

        this->method_timing_map.insert({ method_name, method_map });
    }

    // Use the method map to add the instance of this method
    if (method_map->find(profiler_method_id) != method_map->end())
    {
        return;
    }

    method_map->insert({ profiler_method_id, std::chrono::high_resolution_clock::now() });
}

std::size_t ev31::method_tracker::stop_method_timing(const std::size_t profiler_method_id,
                                                     const std::wstring& method_name)
{
    // Get the method map
    std::unordered_map<std::size_t, std::chrono::steady_clock::time_point>* method_map = nullptr;
    
    if (this->method_timing_map.find(method_name) != this->method_timing_map.end())
    {
        method_map = (this->method_timing_map.find(method_name))->second;
    }
    else
    {
        // First time we have found, create it
        method_map = new std::unordered_map<std::size_t, std::chrono::steady_clock::time_point>();

        this->method_timing_map.insert({ method_name, method_map });
    }

    // Use the method map to the instance of this method
    std::size_t return_value = 0;

    // Assert that this is not an instance of the profiler id method already.
    assert(method_map->find(profiler_method_id) != method_map->end());
    if (method_map->find(profiler_method_id) != method_map->end())
    {
        auto start_time_point = (method_map->find(profiler_method_id))->second;
        auto end_time_point = std::chrono::high_resolution_clock::now();

        std::chrono::nanoseconds diff_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(end_time_point - start_time_point);
        return_value = diff_ns.count();
    }

    return return_value;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////