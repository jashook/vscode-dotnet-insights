////////////////////////////////////////////////////////////////////////////////
// Module: profiler_io.cpp
//
////////////////////////////////////////////////////////////////////////////////

#include "profiler_io.hpp"

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

ev31::profiler_io::profiler_io()
{
    this->ip = "127.0.0.1";
    this->port = "2143";

    this->connection = new ev31::tcp_connection(this->ip, this->port);
    this->connection->initialize_winsock();

    this->should_open_connection = true;
}

ev31::profiler_io::~profiler_io()
{
    delete this->connection;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

void ev31::profiler_io::communicate(const std::size_t function_id, const std::wstring& method_name, double time)
{
    auto&& json_payload = this->_get_json_payload(function_id, method_name, time);
    auto&& http_request = this->_get_http_request(json_payload);

    this->_attempt_open_connection();
    if (this->_is_open_connection())
    {
        const std::string& request_string = http_request.to_string();
        int bytes_sent = this->connection->send(request_string);
        
        if (bytes_sent != http_request.size())
        {
            // We should close the connection and ignore failures.
            this->_close_connection();
        }

        // Unused
        std::array<unsigned char, 1024> bytes_received;
        int receive_count = this->connection->receive(bytes_received);

        if (receive_count == 0)
        {
            // This is most likely an issue with us overloading the http server.
            std::cout << "Receive count zero" << std::endl;
        }

        std::string response_string(bytes_received.begin(), bytes_received.begin() + receive_count);
        std::cout << response_string << std::endl;
    }
    else
    {
        int i = 0;
    }
}

////////////////////////////////////////////////////////////////////////////////
// Private methods
////////////////////////////////////////////////////////////////////////////////

void ev31::profiler_io::_attempt_open_connection()
{
    if (this->should_open_connection && this->connection->closed())
    {
        try
        {
            this->connection->connect();
        }
        catch (std::exception& e)
        {
            this->should_open_connection = false;
        }

        if (this->connection->closed())
        {
            this->should_open_connection = false;
        }
        else
        {
            std::cout << "Connection opened" << std::endl;
        }
    }
}

void ev31::profiler_io::_close_connection()
{
    this->connection->close();
    this->should_open_connection = false;
}

const std::vector<wchar_t> ev31::profiler_io::_get_json_payload(const std::size_t function_id, const std::wstring& method_name, double time)
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
    return payload;
}

const ev31::http_request<std::string, std::string::const_iterator> ev31::profiler_io::_get_http_request(const std::vector<wchar_t>& payload)
{
    std::wstring wstring_type(payload.begin(), payload.end());

    std::wstring_convert<std::codecvt_utf8<wchar_t>, wchar_t> converter;

    //use converter (.to_bytes: wstr->str, .from_bytes: str->wstr)
    std::string json_payload = converter.to_bytes(wstring_type);
    ev31::http_request<std::string, std::string::const_iterator> request(ev31::http::version::Version1_1, ev31::http::request_type::Post, this->ip, "/profiler", ev31::http::content_type::Json, json_payload);

    return request;
}

bool ev31::profiler_io::_is_open_connection() const
{
    return !this->connection->closed();
}