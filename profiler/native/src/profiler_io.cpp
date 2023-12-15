// ////////////////////////////////////////////////////////////////////////////////
// // Module: profiler_io.cpp
// //
// ////////////////////////////////////////////////////////////////////////////////

// #include "profiler_io.hpp"

// ////////////////////////////////////////////////////////////////////////////////
// ////////////////////////////////////////////////////////////////////////////////

// ev31::profiler_io::profiler_io(bool use_console)
// {
//     this->ip = "127.0.0.1";
//     this->port = "2143";

//     this->connection = new ev31::tcp_connection(this->ip, this->port);
//     this->connection->initialize_winsock();

//     this->should_open_connection = true;
//     this->use_console = use_console;
// }

// ev31::profiler_io::~profiler_io()
// {
//     delete this->connection;
// }

// ////////////////////////////////////////////////////////////////////////////////
// ////////////////////////////////////////////////////////////////////////////////


// void ev31::profiler_io::io_via_console(bool use_console)
// {
//     this->use_console = use_console;
// }

// ////////////////////////////////////////////////////////////////////////////////
// // Private methods
// ////////////////////////////////////////////////////////////////////////////////

// void ev31::profiler_io::_attempt_open_connection()
// {
//     if (this->should_open_connection && this->connection->closed())
//     {
//         try
//         {
//             this->connection->connect();
//         }
//         catch (std::exception& e)
//         {
//             this->should_open_connection = false;
//         }

//         if (this->connection->closed())
//         {
//             this->should_open_connection = false;
//         }
//         else
//         {
//             std::cout << "Connection opened" << std::endl;
//         }
//     }
// }

// void ev31::profiler_io::_close_connection()
// {
//     this->connection->close();
//     this->should_open_connection = false;
// }

// const ev31::http_request<std::string, std::string::const_iterator> ev31::profiler_io::_get_http_request(const std::vector<wchar_t>& payload)
// {
//     std::wstring wstring_type(payload.begin(), payload.end());

//     std::wstring_convert<std::codecvt_utf8<wchar_t>, wchar_t> converter;

//     //use converter (.to_bytes: wstr->str, .from_bytes: str->wstr)
//     std::string json_payload = converter.to_bytes(wstring_type);
//     ev31::http_request<std::string, std::string::const_iterator> request(ev31::http::version::Version1_1, ev31::http::request_type::Post, this->ip, "/profiler", ev31::http::content_type::Json, json_payload);

//     return request;
// }

// bool ev31::profiler_io::_is_open_connection() const
// {
//     return !this->connection->closed();
// }