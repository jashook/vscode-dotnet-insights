////////////////////////////////////////////////////////////////////////////////
// Module: McpProtocol.cs
//
// Notes:
// The Model Context Protocol's stdio transport: newline-delimited JSON-RPC
// 2.0, one message per line, on stdin/stdout.
//
// HAND-ROLLED, NO NuGet PACKAGE. The official ModelContextProtocol package
// pulls Microsoft.Extensions.Hosting and its dependency graph behind it, and
// this binary ships as a self-contained per-OS release asset the VS Code
// extension downloads - that graph is real download weight for a JSON-RPC
// loop with five methods. System.Text.Json is already used everywhere here.
// This is the same trade the project's one allowed dependency is reasoned
// about in nettraceParser.csproj: take the package when the thing behind it
// is a private versioned contract (ClrMD over the DAC), hand-roll it when it
// is a documented format.
//
// TRANSPORT FRAMING IS NEWLINE-DELIMITED, NOT Content-Length. That is the
// difference between MCP's stdio transport and LSP's, and getting it backwards
// produces a server that connects and then hangs forever with no error on
// either side. Messages must therefore contain no raw newlines, which
// Utf8JsonWriter's default (non-indented) output guarantees.
//
// STDOUT IS THE PROTOCOL. Nothing else in the process may write to it while a
// server is running - every diagnostic goes to stderr. Program.cs never calls
// ProgressReporter.Enable() on this path for exactly that reason.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Mcp {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// JSON-RPC 2.0's own reserved codes, plus the one application code this
// server uses. A tool that fails because of the CAPTURE (a missing file, an
// unparseable trace) is NOT a protocol error - it comes back as a successful
// tools/call result carrying isError, which is what the MCP specification
// asks for and what lets a model read the failure and try something else
// rather than seeing the transport fault.
public static class McpErrorCode
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class McpRequest
{
    // Null for a NOTIFICATION, which must never be replied to. Getting this
    // wrong is not cosmetic: replying to notifications/initialized makes
    // strict clients drop the connection.
    public JsonElement? Id;
    public string Method = "";
    public JsonElement Parameters;
    public bool HasParameters;

    public bool IsNotification => this.Id == null;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class McpProtocol
{
    // Protocol revisions this server knows how to speak. `initialize` echoes
    // back the client's own version when it is one of these and otherwise
    // answers with the newest we support, which is what the specification
    // prescribes - the client then decides whether it can proceed.
    public static readonly string[] SupportedProtocolVersions = new string[]
    {
        "2025-06-18",
        "2025-03-26",
        "2024-11-05"
    };

    public const string PreferredProtocolVersion = "2025-06-18";

    public const string ServerName = "nettraceParser";

    public static string NegotiateProtocolVersion(string requestedVersion)
    {
        if (requestedVersion != null)
        {
            for (int versionIndex = 0; versionIndex < SupportedProtocolVersions.Length; ++versionIndex)
            {
                if (string.Equals(SupportedProtocolVersions[versionIndex], requestedVersion, StringComparison.Ordinal))
                {
                    return requestedVersion;
                }
            }
        }

        return PreferredProtocolVersion;
    }

    // Returns false for a line that is not a usable JSON-RPC message at all.
    // The caller answers a malformed line with a ParseError carrying a null
    // id, which is what the specification requires when the id could not be
    // recovered.
    public static bool TryParseRequest(string line, out McpRequest request, out string parseFailure)
    {
        request = null;
        parseFailure = null;

        try
        {
            using (JsonDocument document = JsonDocument.Parse(line))
            {
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    parseFailure = "message is not a JSON object";
                    return false;
                }

                if (!root.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String)
                {
                    parseFailure = "message has no method";
                    return false;
                }

                McpRequest parsed = new McpRequest();
                parsed.Method = methodElement.GetString() ?? "";

                if (root.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind != JsonValueKind.Null)
                {
                    // Cloned because the JsonDocument backing it is disposed
                    // when this using block exits - a JsonElement into a
                    // disposed document throws on any later access, which
                    // would surface as a mystery failure when the reply is
                    // written rather than here.
                    parsed.Id = idElement.Clone();
                }

                if (root.TryGetProperty("params", out JsonElement paramsElement))
                {
                    parsed.Parameters = paramsElement.Clone();
                    parsed.HasParameters = true;
                }

                request = parsed;
                return true;
            }
        }
        catch (JsonException jsonException)
        {
            parseFailure = jsonException.Message;
            return false;
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Argument access
    ////////////////////////////////////////////////////////////////////////////

    public static string TryGetString(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!parent.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    // Tolerates a number arriving as a JSON string. Models routinely emit
    // {"limit": "10"} for an integer-typed argument, and refusing that is a
    // protocol-lawyer's answer to a question the caller asked perfectly
    // clearly.
    public static int TryGetInt(JsonElement parent, string propertyName, int fallback)
    {
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public static double TryGetDouble(JsonElement parent, string propertyName, double fallback)
    {
        if (parent.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out double parsed))
        {
            return parsed;
        }

        return fallback;
    }

    ////////////////////////////////////////////////////////////////////////////
    // Message construction
    ////////////////////////////////////////////////////////////////////////////

    public static string BuildResult(JsonElement? id, Action<Utf8JsonWriter> writeResult)
    {
        using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                WriteId(writer, id);

                writer.WritePropertyName("result");
                writeResult(writer);

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    public static string BuildError(JsonElement? id, int code, string message)
    {
        using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                WriteId(writer, id);

                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", code);
                writer.WriteString("message", message ?? "");
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static void WriteId(Utf8JsonWriter writer, JsonElement? id)
    {
        writer.WritePropertyName("id");

        if (id == null)
        {
            writer.WriteNullValue();
            return;
        }

        // Written back with the CLIENT's own type. JSON-RPC allows a string or
        // a number and requires the reply to echo it exactly; normalizing it
        // to one of the two makes replies unmatchable for a client that used
        // the other.
        id.Value.WriteTo(writer);
    }

    // A tools/call result: content blocks plus the isError flag. Text is the
    // only block type this server produces - everything it returns is JSON a
    // model reads.
    public static void WriteToolResult(Utf8JsonWriter writer, string text, bool isError)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text ?? "");
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WriteBoolean("isError", isError);

        writer.WriteEndObject();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Mcp)
