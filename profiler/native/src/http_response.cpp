////////////////////////////////////////////////////////////////////////////////
// Module: http_response.cpp
////////////////////////////////////////////////////////////////////////////////

#include "http_response.hpp"

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

ev31::http_response::http_response(const ev31::http_request& request,
                                   ev31::http_version version, 
                                   ev31::http_response_type response_type, 
                                   const std::string& content,
                                   ev31::http_content_type content_type)
{
    this->response_type = response_type;
    this->version = version;

    this->stamp_version();
    this->stamp_status_code();
    this->stamp_date();
    this->stamp_server();
    this->stamp_last_modified();
    this->stamp_content_length(content);
    this->stamp_content_type(content_type);
    this->stamp_connection();

    this->add_line_to_header("");

    if (content.size() > 0 && request.get_request_type() != ev31::Head)
    {
        this->add_line_to_header(content);
    }

    this->response = this->response_header_builder.str();
}

std::string ev31::http_response::to_string()
{
    return this->response;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

void ev31::http_response::add_to_header(const std::string& content, std::string end)
{
    this->response_header_builder << content << end;
}

void ev31::http_response::add_line_to_header(const std::string& content)
{
    this->add_to_header(content, "\r\n");
}

void ev31::http_response::stamp_connection()
{
    this->add_line_to_header("Connection: Closed");
}

void ev31::http_response::stamp_content_length(const std::string& content)
{
    std::size_t size = content.size();
    std::string content_string = "Content-Length: " + std::to_string(size);

    this->add_line_to_header(content_string);
}

void ev31::http_response::stamp_content_type(ev31::http_content_type content_type)
{
    std::string content;

    if (content_type == ev31::http_content_type::html || content_type == ev31::http_content_type::text)
    {
        content = "text/html";
    }
    else
    {
        content = "application/json";
    }

    this->add_line_to_header(content);
}

void ev31::http_response::stamp_date()
{
    auto now = std::chrono::system_clock::now();
    auto t_c = std::chrono::system_clock::to_time_t(now);

    char buffer[120];
    std::strftime(buffer, 120, "%a, %d %b %Y %H:%M:%S PST", std::localtime(&t_c));

    std::string content(buffer);
    this->add_line_to_header(content);
}

void ev31::http_response::stamp_last_modified()
{
    this->add_line_to_header("Last-Modified: Tue, 2 Feb 2021 15:24:56 PST");
}

void ev31::http_response::stamp_server()
{
    this->add_line_to_header("Server: ev31/1.0.0 (Win32)");
}

void ev31::http_response::stamp_status_code()
{
    if (this->response_type == ev31::http_response_type::Ok)
    {
        this->add_line_to_header("200 OK");
    }
    else if (this->response_type == ev31::http_response_type::BadRequest)
    {
        this->add_line_to_header("201 Created");
    }
    else if (this->response_type == ev31::http_response_type::MovedPermanently)
    {
        this->add_line_to_header("301 Moved Permanently");
    }
    else if (this->response_type == ev31::http_response_type::BadRequest)
    {
        this->add_line_to_header("400 Bad Request");
    }
    else if (this->response_type == ev31::http_response_type::Unauthorized)
    {
        this->add_line_to_header("401 Unauthorized");
    }
    else if (this->response_type == ev31::http_response_type::Forbidden)
    {
        this->add_line_to_header("403 Forbidden");
    }
    else if (this->response_type == ev31::http_response_type::NotFound)
    {
        this->add_line_to_header("404 Not Found");
    }
    else if (this->response_type == ev31::http_response_type::LengthRequired)
    {
        this->add_line_to_header("411 Length Required");
    }
    else if (this->response_type == ev31::http_response_type::TooManyRequests)
    {
        this->add_line_to_header("429 Too Many Requests");
    }
    else if (this->response_type == ev31::http_response_type::InternalServerError)
    {
        this->add_line_to_header("500 Internal Server Error");
    }
    else if (this->response_type == ev31::http_response_type::ServiceUnavailable)
    {
        this->add_line_to_header("503 Server Unavailable");
    }
    else if (this->response_type == ev31::http_response_type::HttpVersionNotSupported)
    {
        this->add_line_to_header("505 HTTP Version Not Supported");
    }
    else
    {
        throw std::runtime_error("NYI: " + this->response_type);
    }
}

void ev31::http_response::stamp_version()
{
    if (this->version == ev31::http_version::Version1)
    {
        this->add_to_header("HTTP/1.0");
    }
    else if (this->version == ev31::http_version::Version1_1)
    {
        this->add_to_header("HTTP/1.1");
    }
    else if (this->version == ev31::http_version::Version1_2)
    {
        this->add_to_header("HTTP/1.2");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////