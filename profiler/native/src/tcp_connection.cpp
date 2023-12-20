// ////////////////////////////////////////////////////////////////////////////////
// // Module: tcp_connection.cpp
// ////////////////////////////////////////////////////////////////////////////////

// #include "tcp_connection.hpp"

// ////////////////////////////////////////////////////////////////////////////////
// ////////////////////////////////////////////////////////////////////////////////

// ev31::tcp_connection::tcp_connection(const std::string& outbound_ip, const std::string& port)
// {
//     this->socket = INVALID_SOCKET;

//     this->connection_open = false;
//     this->connection_closed = true;

//     this->moved = false;
//     this->received_end_of_request = false;

//     this->client_ip = outbound_ip;
//     this->port = port;
// }

// ev31::tcp_connection::tcp_connection(::SOCKET client_socket, const std::string& ip)
// {
//     this->socket = client_socket;
//     this->connection_open = true;
//     this->connection_closed = false;

//     this->moved = false;
//     this->received_end_of_request = false;

//     this->client_ip = ip;
//     this->port = "";
// }

// ev31::tcp_connection::tcp_connection(ev31::tcp_connection&& rhs)
// {
//     this->socket = rhs.socket;
//     this->client_ip = rhs.client_ip;
//     this->port = rhs.port;

//     this->connection_open = rhs.connection_open;
//     this->connection_closed = rhs.connection_closed;

//     this->received_end_of_request = rhs.received_end_of_request;

//     this->moved = false;
//     rhs.moved = true;
// }

// ev31::tcp_connection::~tcp_connection()
// {
//     // Do not shutdown and close the socket if the object has moved
//     // the socket is still valid and we do not want to send the
//     // shutdown signal to the client.
//     if (this->moved)
//     {
//         return;
//     }

//     if (!this->closed())
//     {
//         assert(this->connection_open);
//         this->shutdown();
//     }

//     if (this->socket != INVALID_SOCKET)
//     {
//         ::closesocket(this->socket);
//     }

//     assert(this->connection_closed);
// }

// bool ev31::tcp_connection::closed(bool sys_call_required)
// {
//     bool closed = this->connection_closed;

//     if (!closed && sys_call_required)
//     {
//         ::fd_set set;
//         FD_ZERO(&set);
//         FD_SET(this->socket, &set);
//         ::timeval time_value;

//         time_value.tv_sec = 1;
//         time_value.tv_usec = 0;

//         int h_result = ::select((int)this->socket + 1, &set, 0, 0, &time_value);

//         if (h_result == SOCKET_ERROR)
//         {
//             closed = false;
//         }
//         else if (h_result != 1)
//         {
//             closed = true;
//         }
//     }

//     return closed;
// }

// void ev31::tcp_connection::connect()
// {
//     ::addrinfo hints;
//     std::memset(&hints, 0, sizeof(::addrinfo));

//     hints.ai_family = AF_UNSPEC;
//     hints.ai_socktype = SOCK_STREAM;
//     hints.ai_protocol = IPPROTO_TCP;

//     ::addrinfo* result_info = nullptr;

//     // Resolve address
//     int h_result = ::getaddrinfo(this->client_ip.c_str(), this->port.c_str(), &hints, &result_info);
//     if (h_result != 0)
//     {
//         std::string error_message("Unable to initialize addrinfo. Error code: ");
//         error_message += std::to_string(::GetLastError());

//         std::cout << error_message << std::endl;
//         throw std::runtime_error(error_message);
//     }

//     for(::addrinfo* info = result_info; info != nullptr; info = info->ai_next) {
//         this->socket = ::socket(info->ai_family, info->ai_socktype, info->ai_protocol);
//         if (this->socket == INVALID_SOCKET) {
//             std::string error_message("Unable to initialize addrinfo. Error code: ");
//             error_message += std::to_string(::WSAGetLastError());

//             std::cout << error_message << std::endl;
//             throw std::runtime_error(error_message);
//         }

//         this->toggle_connection_open();

//         // Connect to server.
//         h_result = ::connect(this->socket, info->ai_addr, (int)info->ai_addrlen);
//         if (h_result == SOCKET_ERROR) {
//             ::closesocket(this->socket);
//             this->socket = INVALID_SOCKET;

//             this->toggle_connection_open();
//             continue;
//         }

//         break;
//     }
// }

// const std::string& ev31::tcp_connection::connecting_ip()
// {
//     return this->client_ip;
// }

// bool ev31::tcp_connection::end_of_request()
// {
//     return this->received_end_of_request;
// }

// int ev31::tcp_connection::send(const std::string& response)
// {
//     this->received_end_of_request = false;
//     int bytes_sent = ::send(this->socket, response.c_str(), (int)response.size(), 0);

//     if (bytes_sent == SOCKET_ERROR) {
//         std::string error_message("Shutdown failed. Error code: ");
//         error_message += std::to_string(::WSAGetLastError());

//         std::cout << error_message << std::endl;
//         throw std::runtime_error(error_message);
//     }
    
//     return bytes_sent;
// }

// int ev31::tcp_connection::send(const unsigned char* buffer, int size)
// {
//     this->received_end_of_request = false;

//     char* buffer_signed = (char*)buffer;
//     int bytes_sent = ::send(this->socket, buffer_signed, size, 0);

//     if (bytes_sent == SOCKET_ERROR) {
//         std::string error_message("Shutdown failed. Error code: ");
//         error_message += std::to_string(::WSAGetLastError());

//         std::cout << error_message << std::endl;
//         throw std::runtime_error(error_message);
//     }
    
//     return bytes_sent;
// }

// ////////////////////////////////////////////////////////////////////////////////
// ////////////////////////////////////////////////////////////////////////////////

// void ev31::tcp_connection::close()
// {
//     if (!this->closed(true))
//     {
//         this->shutdown();
//     }

//     if (this->connection_open)
//     {
//         this->toggle_connection_open();
//     }
// }

// void ev31::tcp_connection::initialize_winsock()
// {
//     std::memset(&this->wsa_data, 0, sizeof(::WSADATA));
//     std::size_t h_result = ::WSAStartup(MAKEWORD(2, 2), &this->wsa_data);

//     if (h_result)
//     {
//         std::string error_message("Unable to initialize winsock. Error code: ");
//         error_message += std::to_string(::GetLastError());

//         std::cout << error_message << std::endl;
//         throw std::runtime_error(error_message);
//     }
// }

// void ev31::tcp_connection::toggle_connection_open()
// {
//     this->connection_open = !this->connection_open;
//     this->connection_closed = !this->connection_closed;
// }

// void ev31::tcp_connection::shutdown()
// {
//     // Shutdown this connection.
//     std::size_t h_result = ::shutdown(this->socket, SD_SEND);

//     if (h_result == SOCKET_ERROR)
//     {
//         std::string error_message("Shutdown failed. Error code: ");
//         error_message += std::to_string(::WSAGetLastError());

//         std::cout << error_message << std::endl;
//         throw std::runtime_error(error_message);
//     }
    
//     this->toggle_connection_open();
// }

// ////////////////////////////////////////////////////////////////////////////////
// ////////////////////////////////////////////////////////////////////////////////