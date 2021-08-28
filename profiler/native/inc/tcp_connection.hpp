////////////////////////////////////////////////////////////////////////////////
// Module: tcp_connection.hpp
////////////////////////////////////////////////////////////////////////////////

#ifndef __TCP_CONNECTION_HPP__
#define __TCP_CONNECTION_HPP__

////////////////////////////////////////////////////////////////////////////////
// Includes
////////////////////////////////////////////////////////////////////////////////

#include <array>
#include <functional>
#include <iostream>
#include <string>

#include <cassert>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace ev31 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

class tcp_connection
{
    private:
        std::string client_ip;
        std::string port;
        bool connection_open;
        bool connection_closed;
        bool received_end_of_request;
        bool moved;
        ::SOCKET socket;
        ::WSAData wsa_data;

    public:
        tcp_connection(const std::string& outbound_ip, const std::string& port);
        tcp_connection(::SOCKET client_socket, const std::string& ip);
        tcp_connection(tcp_connection&& rhs);
        ~tcp_connection();

        void close();
        bool closed(bool required_sys_call=false);
        void connect();
        const std::string& connecting_ip();
        bool end_of_request();
        void initialize_winsock();
        void reset_receive_status();
        int send(const std::string& response);
        int send(const unsigned char* buffer, int size);

    private:
        void initialize_addrinfo();
        void toggle_connection_open();
        void shutdown();

    public:
        template<std::size_t __Size> std::size_t receive(std::array<unsigned char, __Size>& buffer)
        {
            if (this->closed(true))
            {
                this->toggle_connection_open();
                return 0;
            }

            this->received_end_of_request = false;

            assert(this->socket != INVALID_SOCKET);

            char* buffer_signed = (char*)buffer.data();
            int bytes_received = ::recv(this->socket, buffer_signed, __Size, 0);

            if (bytes_received < 0)
            {
                std::string error_message("Receive failed. Error code: ");
                error_message += std::to_string(::WSAGetLastError());

                std::cout << error_message << std::endl;
                throw std::runtime_error(error_message);
            }
            else if (bytes_received != __Size)
            {
                this->received_end_of_request = true;
            }

            return bytes_received;
        }
};


////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end namespace(ev31)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#endif // __TCP_CONNECTION_HPP__

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
