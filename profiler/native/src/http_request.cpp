////////////////////////////////////////////////////////////////////////////////
// Module: http_request.cpp
////////////////////////////////////////////////////////////////////////////////

#include "http_request.hpp"

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

ev31::http_request::http_request()
{
    
}

ev31::http_request::http_request(const std::string& request_unparsed)
{
    std::stringstream ss(request_unparsed);

    std::string request_type;
    ss >> request_type;

    std::string path;
    ss >> path;

    std::string version;
    ss >> version;

    std::string host;
    std::getline(ss, host);

    ss >> host;
    ss >> host;

    if (*(host.end() - 1) == '\r')
    {
        host.erase(host.end() - 1);
    }

    http_request_type parsed_request_type;
    if (request_type == "GET") parsed_request_type = ev31::Get;
    else if (request_type == "HEAD") parsed_request_type = ev31::Head;
    else if (request_type == "POST") parsed_request_type = ev31::Post;
    else if (request_type == "PUT") parsed_request_type = ev31::Put;
    else if (request_type == "DELETE") parsed_request_type = ev31::Delete;
    else if (request_type == "CONNECT") parsed_request_type = ev31::Connect;
    else if (request_type == "OPTIONS") parsed_request_type = ev31::Options;
    else if (request_type == "TRACE") parsed_request_type = ev31::Trace;
    else if (request_type == "PATCH") parsed_request_type = ev31::Patch;

    http_version parsed_version;

    if (version == "HTTP/1.0") parsed_version = ev31::Version1;
    else if (version == "HTTP/1.1") parsed_version = ev31::Version1_1;
    else if (version == "HTTP/1.2") parsed_version = ev31::Version1_1;

    this->request_type = parsed_request_type;
    this->path = path;
    this->version = parsed_version;
    this->host = host;

    this->request = request_unparsed;
}

ev31::http_request::http_request(ev31::http_version version, ev31::http_request_type request_type, const std::string& host, const std::string& path)
{
    this->request_type = request_type;
    this->version = version;
    this->host = host;
    this->path = path;

    this->stamp_request_type();
    this->stamp_path();
    this->stamp_version();
    this->stamp_host();
    this->stamp_user_agent();
    this->stamp_accept();

    this->add_line_to_header("");

    this->request = this->request_header_builder.str();
}

ev31::http_request::http_request(http_request& rhs)
{
    this->request_type = rhs.request_type;
    this->version = rhs.version;
    this->host = rhs.host;
    this->path = rhs.path;

    this->request = rhs.request;
}

ev31::http_request ev31::http_request::operator= (const ev31::http_request & rhs)
{
    http_request temp(rhs.get_version(), rhs.get_request_type(), rhs.get_host(), rhs.get_path());
    return temp;
}

ev31::http_request_type ev31::http_request::get_request_type() const
{
    return this->request_type;
}

const std::string& ev31::http_request::get_host() const
{
    return this->host;
}

const std::string& ev31::http_request::get_path() const
{
    return this->path;
}

ev31::http_version ev31::http_request::get_version() const 
{
    return this->version;
}

std::string ev31::http_request::to_string() const
{
    return this->request;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

void ev31::http_request::add_to_header(const std::string& content, std::string end)
{
    this->request_header_builder << content << end;
}

void ev31::http_request::add_line_to_header(const std::string& content)
{
    this->add_to_header(content, "\r\n");
}

void ev31::http_request::stamp_accept()
{
    this->add_line_to_header("Accept: */*");
}

void ev31::http_request::stamp_host()
{
    this->add_line_to_header(this->host);
}

void ev31::http_request::stamp_user_agent()
{
    this->add_line_to_header("ev31/1.0.0 (Win32)");
}

void ev31::http_request::stamp_request_type()
{
    std::string request_type;

    if (this->request_type == ev31::Get)
    {
        request_type = "GET";
    }
    else if (this->request_type == ev31::Head)
    {
        request_type = "HEAD";
    }
    else if (this->request_type == ev31::Post)
    {
        request_type = "POST";
    }
    else if (this->request_type == ev31::Put)
    {
        request_type = "PUT";
    }
    else if (this->request_type == ev31::Delete)
    {
        request_type = "DELETE";
    }
    else if (this->request_type == ev31::Connect)
    {
        request_type = "CONNECT";
    }
    else if (this->request_type == ev31::Options)
    {
        request_type = "OPTIONS";
    }
    else if (this->request_type == ev31::Trace)
    {
        request_type = "TRACE";
    }
    else if (this->request_type == ev31::Patch)
    {
        request_type = "PATCH";
    }

    this->add_to_header(request_type);
}

void ev31::http_request::stamp_path()
{
    this->add_to_header(this->path);
}

void ev31::http_request::stamp_version()
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