////////////////////////////////////////////////////////////////////////////////
// Module: http_request.hpp
////////////////////////////////////////////////////////////////////////////////

#ifndef __HTTP_REQUEST_HPP__
#define __HTTP_REQUEST_HPP__

////////////////////////////////////////////////////////////////////////////////
// Includes
////////////////////////////////////////////////////////////////////////////////

#include <cassert>

#include <algorithm>
#include <iterator>
#include <string>
#include <sstream>
#include <vector>

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace ev31 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace http {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

enum content_type
{
    Json,
    FormEncoded
};

enum version
{
    Version1,
    Version1_1,
    Version1_2
};

enum request_type
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

} // namespace (http)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

template<typename __Type, typename __Iterator> class http_request
{
    private:
        std::stringstream request_header_builder;
        std::string host;
        std::string path;
        std::string request;
        http::request_type request_type;
        http::version version;

    public:
        http_request() { }

        http_request(const std::string& request_unparsed)
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

            http::request_type parsed_request_type;
            if (request_type == "GET") parsed_request_type = ev31::http::Get;
            else if (request_type == "HEAD") parsed_request_type = ev31::http::Head;
            else if (request_type == "POST") parsed_request_type = ev31::http::Post;
            else if (request_type == "PUT") parsed_request_type = ev31::http::Put;
            else if (request_type == "DELETE") parsed_request_type = ev31::http::Delete;
            else if (request_type == "CONNECT") parsed_request_type = ev31::http::Connect;
            else if (request_type == "OPTIONS") parsed_request_type = ev31::http::Options;
            else if (request_type == "TRACE") parsed_request_type = ev31::http::Trace;
            else if (request_type == "PATCH") parsed_request_type = ev31::http::Patch;

            http::version parsed_version;

            if (version == "HTTP/1.0") parsed_version = ev31::http::Version1;
            else if (version == "HTTP/1.1") parsed_version = ev31::http::Version1_1;
            else if (version == "HTTP/1.2") parsed_version = ev31::http::Version1_1;

            this->request_type = parsed_request_type;
            this->path = path;
            this->version = parsed_version;
            this->host = host;

            this->request = request_unparsed;
        }

        http_request(http::version version, http::request_type request_type, const std::string& host, const std::string& path)
        {
            this->request_type = request_type;
            this->version = version;
            this->host = host;
            this->path = path;

            this->stamp_request_type();
            this->stamp_path();
            this->stamp_version();
            this->stamp_host();
            this->stamp_accept();
            this->stamp_user_agent();

            this->add_line_to_header("");

            this->request = this->request_header_builder.str();
        }

        http_request(http::version version, http::request_type request_type, const std::string& host, const std::string& path, http::content_type content_type, const __Type& body)
        {
            this->request_type = request_type;
            this->version = version;
            this->host = host;
            this->path = path;

            this->stamp_request_type();
            this->stamp_path();
            this->stamp_version();
            this->stamp_host();
            this->stamp_accept();
            this->stamp_user_agent();
            this->stamp_content_length(body.size());
            this->stamp_content_type(content_type);
            this->add_line_to_header("");

            this->request = this->request_header_builder.str();

            this->add_body(body);
        }

        http_request(http_request& rhs)
        {
            this->request_type = rhs.request_type;
            this->version = rhs.version;
            this->host = rhs.host;
            this->path = rhs.path;

            this->request = rhs.request;
        }

        ev31::http_request<__Type, __Iterator> operator= (const ev31::http_request<__Type, __Iterator> & rhs)
        {
            http_request temp(rhs.get_version(), rhs.get_request_type(), rhs.get_host(), rhs.get_path());
            return temp;
        }

    // Member variables
    public:

        http::request_type get_request_type() const { return this->request_type; }
        http::version get_version() const { return this->host; }
        const std::string& get_host() const { return this->path; }
        const std::string& get_path() const { return this->version; }
        const std::string& to_string() const { return this->request; }

        std::size_t size() const
        {
            return this->request.size();
        }

    private:

        void add_body(const __Type& body)
        {
            std::copy(body.begin(), body.end(), std::back_insert_iterator<std::string>(this->request));
            this->request += "\r\n";
        }

        void add_to_header(const std::string& content, std::string end = " ")
        {
            this->request_header_builder << content << end;
        }
        
        void add_line_to_header(const std::string& content)
        {
            this->add_to_header(content, "\r\n");
        }

        void stamp_accept()
        {
            this->add_line_to_header("Accept: */*");
        }

        void stamp_content_length(std::size_t length)
        {
            std::stringstream content_length;
            
            content_length << "Content-Length: ";
            content_length << length;

            this->add_line_to_header(content_length.str());
        }

        void stamp_content_type(http::content_type type)
        {
            assert(type == http::content_type::Json);
            this->add_line_to_header("Content-Type: application/json");
        }
        
        void stamp_host()
        {
            std::stringstream ss;

            ss << "Host: " << this->host;
            this->add_line_to_header(ss.str());
        }

        void stamp_request_type()
        {
            std::string request_type;

            if (this->request_type == ev31::http::Get)
            {
                request_type = "GET";
            }
            else if (this->request_type == ev31::http::Head)
            {
                request_type = "HEAD";
            }
            else if (this->request_type == ev31::http::Post)
            {
                request_type = "POST";
            }
            else if (this->request_type == ev31::http::Put)
            {
                request_type = "PUT";
            }
            else if (this->request_type == ev31::http::Delete)
            {
                request_type = "DELETE";
            }
            else if (this->request_type == ev31::http::Connect)
            {
                request_type = "CONNECT";
            }
            else if (this->request_type == ev31::http::Options)
            {
                request_type = "OPTIONS";
            }
            else if (this->request_type == ev31::http::Trace)
            {
                request_type = "TRACE";
            }
            else if (this->request_type == ev31::http::Patch)
            {
                request_type = "PATCH";
            }

            this->add_to_header(request_type);
        }

        void stamp_path()
        {
            this->add_to_header(this->path);
        }

        void stamp_user_agent()
        {
            this->add_line_to_header("User-Agent: ev31/1.0.0");
        }

        void stamp_version()
        {
            if (this->version == ev31::http::version::Version1)
            {
                this->add_line_to_header("HTTP/1.0");
            }
            else if (this->version == ev31::http::version::Version1_1)
            {
                this->add_line_to_header("HTTP/1.1");
            }
            else if (this->version == ev31::http::version::Version1_2)
            {
                this->add_line_to_header("HTTP/1.2");
            }
        }
};


////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end namespace(ev31)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

#endif // __HTTP_REQUEST_HPP__

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
