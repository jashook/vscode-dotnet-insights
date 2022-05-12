////////////////////////////////////////////////////////////////////////////////
// Module: profiler_io.hpp
//
////////////////////////////////////////////////////////////////////////////////

#ifndef __PROFILER_IO_HPP__
#define __PROFILER_IO_HPP__

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#include <cassert>
#include <codecvt>
#include <exception>
#include <locale>
#include <vector>

#include "http_request.hpp"
#include "http_response.hpp"
#include "tcp_connection.hpp"

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace ev31 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

class profiler_io
{
    // Ctor / Dtor
    public:
        profiler_io(bool use_console=false);
        ~profiler_io();

    public: // Template Functions
        template<typename __Type> void communicate(const std::string& id_name,
                                                   const std::size_t id, 
                                                   const __Type& method_name, 
                                                   double time, 
                                                   const std::string& event_name)
        {
            auto&& json_payload = this->_get_json_payload<__Type>(id_name, id, method_name, time, event_name);

            if (!this->use_console)
            {
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
            else
            {
                for (unsigned index = 0; index < json_payload.size(); ++index)
                {
                    std::cout << (char)json_payload[index];
                }
                std::cout << std::endl;
            }
        }

    // Member Methods
    public:
        void io_via_console(bool use_console=true);

    // Private member methods
    private:
        void _attempt_open_connection();
        void _close_connection();
        bool _is_open_connection() const;

    private: // Template Methods
        template<typename __Type> const std::vector<wchar_t> _get_json_payload(const std::string& id_name,
                                                                               const std::size_t id, 
                                                                               const __Type& method_name,
                                                                               double time,
                                                                               const std::string& event_name)
        {
            std::vector<wchar_t> payload;

            std::stringstream ss;
            ss << "{ \"eventName\": " << event_name << ',';
            
            ss << "\"" << id_name << "\": " << (std::size_t)id << ',';

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

    // Private member methods
    private:
        const std::vector<wchar_t> _get_json_payload(const std::size_t, const std::wstring&, double, const std::string&);
        const ev31::http_request<std::string, std::string::const_iterator> _get_http_request(const std::vector<wchar_t>&);

        bool should_open_connection;
        bool use_console;

        std::string ip;
        std::string port;
        ev31::tcp_connection* connection;
};

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace (ev31)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#endif // __PROFILER_IO_HPP__

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////