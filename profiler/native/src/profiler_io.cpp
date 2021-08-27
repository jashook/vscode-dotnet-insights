////////////////////////////////////////////////////////////////////////////////
// Module: profiler_io.cpp
//
////////////////////////////////////////////////////////////////////////////////

#include "profiler_io.hpp"

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

ev31::profiler_io::profiler_io()
{

}

ev31::profiler_io::~profiler_io()
{

}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

void ev31::profiler_io::communicate(const std::size_t function_id, const std::wstring& method_name, double time)
{
    std::vector<wchar_t> json_payload = this->_get_json_payload(function_id, method_name);
}

////////////////////////////////////////////////////////////////////////////////
// Private methods
////////////////////////////////////////////////////////////////////////////////

const std::vector<wchar_t> _get_json_payload(const std::size_t function_id, const std::wstring& method_name, double time)
{
    std::vector<wchar_t> payload;

    std::stringstream ss;
    ss << "{ \"id\": " << (std::size_t)function_id << ',';

    std::copy(std::istream_iterator<char>(ss), std::istream_iterator<char>(), std::back_insert_iterator<std::vector<wchar_t>>(payload));

    ss.clear();
    ss << "methodName: ";

    std::copy(std::istream_iterator<char>(ss), std::istream_iterator<char>(), std::back_insert_iterator<std::vector<wchar_t>>(payload));
    std::copy(method_name.begin(), method_name.end(), std::back_insert_iterator<std::vector<wchar_t>>(payload));
    
    ss.clear();
    ss << ", time: " << time << '}';

    std::copy(std::istream_iterator<char>(ss), std::istream_iterator<char>(), std::back_insert_iterator<std::vector<wchar_t>>(payload));

    // json payload done.
}