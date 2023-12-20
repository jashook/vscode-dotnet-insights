////////////////////////////////////////////////////////////////////////////////
// Module: assembly_tracker.cpp
//
////////////////////////////////////////////////////////////////////////////////

#include "assembly_tracker.hpp"

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

ev31::assembly_tracker::assembly_tracker()
{

}

ev31::assembly_tracker::~assembly_tracker()
{

}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

void ev31::assembly_tracker::start_assembly_timing(const std::size_t profiler_method_id,
                                                   const std::wstring& method_name)
{
    // Get the method map
    std::unordered_map<std::size_t, std::chrono::steady_clock::time_point>* assembly_map = nullptr;
    
    if (this->assembly_timing_map.find(method_name) != this->assembly_timing_map.end())
    {
        assembly_map = (this->assembly_timing_map.find(method_name))->second;
    }
    else
    {
        // First time we have found, create it
        assembly_map = new std::unordered_map<std::size_t, std::chrono::steady_clock::time_point>();

        this->assembly_timing_map.insert({ method_name, assembly_map });
    }

    // Use the method map to add the instance of this method
    if (assembly_map->find(profiler_method_id) != assembly_map->end())
    {
        return;
    }

    assembly_map->insert({ profiler_method_id, std::chrono::high_resolution_clock::now() });
}

std::size_t ev31::assembly_tracker::stop_assembly_timing(const std::size_t profiler_method_id,
                                                         const std::wstring& method_name)
{
    // Get the method map
    std::unordered_map<std::size_t, std::chrono::steady_clock::time_point>* assembly_map = nullptr;
    
    if (this->assembly_timing_map.find(method_name) != this->assembly_timing_map.end())
    {
        assembly_map = (this->assembly_timing_map.find(method_name))->second;
    }
    else
    {
        // First time we have found, create it
        assembly_map = new std::unordered_map<std::size_t, std::chrono::steady_clock::time_point>();

        this->assembly_timing_map.insert({ method_name, assembly_map });
    }

    // Use the method map to the instance of this method
    std::size_t return_value = 0;

    // Assert that this is not an instance of the profiler id method already.
    assert(assembly_map->find(profiler_method_id) != assembly_map->end());
    if (assembly_map->find(profiler_method_id) != assembly_map->end())
    {
        auto start_time_point = (assembly_map->find(profiler_method_id))->second;
        auto end_time_point = std::chrono::high_resolution_clock::now();

        std::chrono::nanoseconds diff_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(end_time_point - start_time_point);
        return_value = diff_ns.count();
    }

    return return_value;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////