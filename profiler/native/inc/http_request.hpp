////////////////////////////////////////////////////////////////////////////////
// Module: http_request.hpp
////////////////////////////////////////////////////////////////////////////////

#ifndef __HTTP_REQUEST_HPP__
#define __HTTP_REQUEST_HPP__

////////////////////////////////////////////////////////////////////////////////
// Includes
////////////////////////////////////////////////////////////////////////////////

#include <cassert>

#include <string>
#include <sstream>

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace ev31 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

enum http_version
{
    Version1,
    Version1_1,
    Version1_2
};

enum http_request_type
{
    Get,
    Head,
    Post,
    Put,
    Delete,
    Connect,
    Options,
    Trace,
    Patch
};

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

class http_request
{
    private:
        std::stringstream request_header_builder;
        std::string host;
        std::string path;
        std::string request;
        http_request_type request_type;
        http_version version;

    public:
        http_request();
        http_request(const std::string&);
        http_request(http_version version, http_request_type request_type, const std::string& host, const std::string& path);
        http_request(http_request& rhs);

        ev31::http_request ev31::http_request::operator= (const ev31::http_request & rhs);

        http_request_type get_request_type() const;
        http_version get_version() const;
        const std::string& get_host() const;
        const std::string& get_path() const;
        std::string to_string() const;

    private:

        void add_to_header(const std::string& content, std::string end = " ");
        void add_line_to_header(const std::string& content);
        void stamp_accept();
        void stamp_host();
        void stamp_request_type();
        void stamp_path();
        void stamp_user_agent();
        void stamp_version();
};


////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end namespace(ev31)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#endif // __HTTP_REQUEST_HPP__

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////