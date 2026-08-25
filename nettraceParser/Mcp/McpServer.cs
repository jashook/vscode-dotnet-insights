////////////////////////////////////////////////////////////////////////////////
// Module: McpServer.cs
//
// Notes:
// The `--mcp` server: reads JSON-RPC lines from stdin, dispatches them to the
// tools in McpTools.cs, writes replies to stdout. See McpProtocol.cs for the
// transport and why it is hand-rolled.
//
// STATE IS THE POINT. A capture costs seconds to parse and hundreds of MB to
// hold; an agent asks a dozen questions about it. So `open_capture` parses
// once, keeps the resulting CaptureAnalysis (tens of KB - see
// Analysis/CaptureAnalysis.cs) and every later tool reads that. The full
// NettraceFile is dropped as soon as the analysis is built, so what the server
// retains between calls is small no matter how large the capture was.
//
// PARSING IS SYNCHRONOUS AND THAT IS DELIBERATE. A tool call that takes six
// seconds is fine; a server that answers other questions about a
// half-constructed analysis is not. The single-threaded loop makes it
// impossible for a second tool call to observe a capture mid-parse, which is
// worth more here than the concurrency would be.
//
// A CAPTURE-LEVEL FAILURE IS NOT A PROTOCOL ERROR. A missing file or an
// unparseable trace comes back as a successful tools/call carrying isError and
// a message naming the path, per the MCP specification - so the model reads
// the failure and can correct itself, rather than seeing a transport fault it
// can do nothing with. Only a malformed message or an unknown method produces
// a JSON-RPC error.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Mcp {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using DotnetInsights.NetTrace.Analysis;
using DotnetInsights.NetTrace.Insights;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class McpServer
{
    private readonly Func<string, CaptureAnalysis> analyzeCapture;
    private readonly string cacheDirectory;
    private readonly TextWriter diagnostics;

    // Insertion-ordered so "the most recently opened capture" is well defined
    // without a timestamp. Keyed by full path.
    private readonly Dictionary<string, CaptureAnalysis> openCaptures = new Dictionary<string, CaptureAnalysis>(StringComparer.Ordinal);
    private readonly List<string> openOrder = new List<string>();

    // Insight reports are memoized per capture. Running 26 rules is quick, but
    // the report is deterministic for a given analysis, so recomputing it on
    // every get_insights call would be pure waste.
    private readonly Dictionary<string, InsightReport> reportsByCapture = new Dictionary<string, InsightReport>(StringComparer.Ordinal);

    private readonly List<McpToolDefinition> tools = McpTools.AllTools();

    public McpServer(Func<string, CaptureAnalysis> analyzeCapture, string cacheDirectory, TextWriter diagnostics)
    {
        this.analyzeCapture = analyzeCapture;
        this.cacheDirectory = cacheDirectory;
        this.diagnostics = diagnostics;
    }

    // Runs until stdin closes, which is how an MCP client signals shutdown.
    public void Run(TextReader input, TextWriter output)
    {
        string line;

        while ((line = input.ReadLine()) != null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            string response = this.HandleLine(line);

            if (response == null)
            {
                // A notification. Answering one is a protocol violation that
                // strict clients drop the connection over.
                continue;
            }

            output.WriteLine(response);
            output.Flush();
        }
    }

    public string HandleLine(string line)
    {
        if (!McpProtocol.TryParseRequest(line, out McpRequest request, out string parseFailure))
        {
            // Null id: the specification's answer when the id could not be
            // recovered from the message.
            return McpProtocol.BuildError(null, McpErrorCode.ParseError, parseFailure ?? "could not parse message");
        }

        if (request.IsNotification)
        {
            return null;
        }

        try
        {
            switch (request.Method)
            {
                case "initialize":
                    return this.HandleInitialize(request);

                case "ping":
                    return McpProtocol.BuildResult(request.Id, writer => { writer.WriteStartObject(); writer.WriteEndObject(); });

                case "tools/list":
                    return this.HandleToolsList(request);

                case "tools/call":
                    return this.HandleToolsCall(request);

                default:
                    return McpProtocol.BuildError(request.Id, McpErrorCode.MethodNotFound, "unknown method: " + request.Method);
            }
        }
        catch (Exception dispatchException)
        {
            // A bug here must not take the connection down - the client would
            // see a dead server rather than a failed call.
            this.diagnostics?.WriteLine("mcp: " + request.Method + " failed: " + dispatchException);
            return McpProtocol.BuildError(request.Id, McpErrorCode.InternalError, dispatchException.Message);
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Handshake
    ////////////////////////////////////////////////////////////////////////////

    private string HandleInitialize(McpRequest request)
    {
        string requestedVersion = request.HasParameters
            ? McpProtocol.TryGetString(request.Parameters, "protocolVersion")
            : null;

        string negotiated = McpProtocol.NegotiateProtocolVersion(requestedVersion);

        return McpProtocol.BuildResult(request.Id, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", negotiated);

            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            // Tools only. This server exposes no resources and no prompts, and
            // declaring capabilities it does not implement makes a client offer
            // the user features that then fail.
            writer.WritePropertyName("tools");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WritePropertyName("serverInfo");
            writer.WriteStartObject();
            writer.WriteString("name", McpProtocol.ServerName);
            writer.WriteString("version", NettraceParserVersion.Value);
            writer.WriteEndObject();

            writer.WriteString("instructions",
                "Tools for inspecting a .NET .nettrace capture. Call open_capture with a file path first; every other tool then "
                + "defaults to that capture. Start with get_insights for a ranked list of what is wrong, and get_capture_summary to "
                + "see which classes of event the capture actually recorded - an empty result from a capture that never recorded "
                + "that event type does not mean the process was healthy.");

            writer.WriteEndObject();
        });
    }

    private string HandleToolsList(McpRequest request)
    {
        return McpProtocol.BuildResult(request.Id, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartArray();

            for (int toolIndex = 0; toolIndex < this.tools.Count; ++toolIndex)
            {
                McpToolDefinition tool = this.tools[toolIndex];

                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("inputSchema");
                tool.WriteInputSchema(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    ////////////////////////////////////////////////////////////////////////////
    // Tool dispatch
    ////////////////////////////////////////////////////////////////////////////

    private string HandleToolsCall(McpRequest request)
    {
        if (!request.HasParameters)
        {
            return McpProtocol.BuildError(request.Id, McpErrorCode.InvalidParams, "tools/call requires params");
        }

        string toolName = McpProtocol.TryGetString(request.Parameters, "name");

        if (toolName == null)
        {
            return McpProtocol.BuildError(request.Id, McpErrorCode.InvalidParams, "tools/call requires a tool name");
        }

        JsonElement arguments = default;

        if (request.Parameters.ValueKind == JsonValueKind.Object
            && request.Parameters.TryGetProperty("arguments", out JsonElement argumentsElement))
        {
            arguments = argumentsElement;
        }

        return McpProtocol.BuildResult(request.Id, writer =>
        {
            string answer;
            bool isError;

            try
            {
                answer = this.InvokeTool(toolName, arguments, out isError);
            }
            catch (Exception toolException)
            {
                this.diagnostics?.WriteLine("mcp: tool " + toolName + " threw: " + toolException);
                answer = "Tool failed: " + toolException.Message;
                isError = true;
            }

            McpProtocol.WriteToolResult(writer, answer, isError);
        });
    }

    private string InvokeTool(string toolName, JsonElement arguments, out bool isError)
    {
        isError = false;

        if (toolName == "open_capture")
        {
            return this.OpenCapture(arguments, out isError);
        }

        if (toolName == "list_open_captures")
        {
            return WriteJson(writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName("captures");
                writer.WriteStartArray();

                for (int captureIndex = this.openOrder.Count - 1; captureIndex >= 0; --captureIndex)
                {
                    McpTools.WriteCaptureSummary(writer, this.openCaptures[this.openOrder[captureIndex]]);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }

        CaptureAnalysis analysis = this.ResolveCapture(arguments, out string resolveFailure);

        if (analysis == null)
        {
            isError = true;
            return resolveFailure;
        }

        int limit = McpTools.ClampLimit(McpProtocol.TryGetInt(arguments, "limit", McpTools.DefaultLimit));

        switch (toolName)
        {
            case "get_capture_summary":
                return WriteJson(writer => McpTools.WriteCaptureSummary(writer, analysis));

            case "get_time_breakdown":
                return WriteJson(writer => McpTools.WriteTimeBreakdown(writer, analysis));

            case "get_gc_summary":
                return WriteJson(writer => McpTools.WriteGcSummary(writer, analysis));

            case "query_gcs":
                return WriteJson(writer => McpTools.WriteGcs(
                    writer,
                    analysis,
                    McpProtocol.TryGetInt(arguments, "generation", -1),
                    McpProtocol.TryGetDouble(arguments, "minPauseMSec", 0),
                    TryGetBool(arguments, "inducedOnly", false),
                    limit));

            case "get_cpu_categories":
                return WriteJson(writer => McpTools.WriteCpuCategories(writer, analysis));

            case "get_hot_methods":
                return WriteJson(writer => McpTools.WriteHotMethods(
                    writer,
                    analysis,
                    limit,
                    TryGetBool(arguments, "includeIdleWait", false)));

            case "get_threads":
                return WriteJson(writer => McpTools.WriteThreads(
                    writer,
                    analysis,
                    McpProtocol.TryGetString(arguments, "role"),
                    TryGetBool(arguments, "excludeBenign", false),
                    limit));

            case "get_allocation_types":
                return WriteJson(writer => McpTools.WriteAllocationTypes(writer, analysis, limit));

            case "get_contention_sites":
                return WriteJson(writer => McpTools.WriteContentionSites(writer, analysis, limit));

            case "get_exception_types":
                return WriteJson(writer => McpTools.WriteExceptionTypes(writer, analysis, limit));

            case "get_insights":
            {
                InsightReport report = this.ReportFor(analysis);

                InsightSeverity minimumSeverity = InsightSeverity.Info;
                string requestedSeverity = McpProtocol.TryGetString(arguments, "severity");

                if (requestedSeverity != null && !InsightExitCode.TryParseSeverity(requestedSeverity, out minimumSeverity))
                {
                    isError = true;
                    return "Unknown severity \"" + requestedSeverity + "\". Expected one of: info, warning, critical.";
                }

                bool includeRules = TryGetBool(arguments, "includeRules", true);

                return WriteJson(writer => McpTools.WriteInsights(writer, report, minimumSeverity, includeRules));
            }

            default:
                isError = true;
                return "Unknown tool: " + toolName;
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Capture lifecycle
    ////////////////////////////////////////////////////////////////////////////

    private string OpenCapture(JsonElement arguments, out bool isError)
    {
        isError = false;

        string requestedPath = McpProtocol.TryGetString(arguments, "path");

        if (string.IsNullOrEmpty(requestedPath))
        {
            isError = true;
            return "open_capture requires a \"path\" argument naming a .nettrace file.";
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception)
        {
            isError = true;
            return "Not a usable file path: " + requestedPath;
        }

        if (this.openCaptures.TryGetValue(fullPath, out CaptureAnalysis alreadyOpen))
        {
            return WriteJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("status", "alreadyOpen");
                writer.WritePropertyName("capture");
                McpTools.WriteCaptureSummary(writer, alreadyOpen);
                writer.WriteEndObject();
            });
        }

        if (!File.Exists(fullPath))
        {
            isError = true;
            return "No such file: " + fullPath;
        }

        string cacheKey = CaptureAnalysisCache.TryComputeKey(fullPath);
        CaptureAnalysis analysis = CaptureAnalysisCache.TryRead(this.cacheDirectory, cacheKey);
        string source = "cache";

        if (analysis == null)
        {
            source = "parsed";

            try
            {
                analysis = this.analyzeCapture(fullPath);
            }
            catch (Exception parseException)
            {
                this.diagnostics?.WriteLine("mcp: failed to parse " + fullPath + ": " + parseException);
                isError = true;
                return "Could not parse " + fullPath + ": " + parseException.Message;
            }

            CaptureAnalysisCache.TryWrite(this.cacheDirectory, cacheKey, analysis);
        }

        // The cached copy carries whatever path it was written from, which for
        // a capture opened by two different relative paths would be the other
        // one. Harmless, but it would be echoed back to the agent as this
        // capture's identity.
        analysis.SourcePath = fullPath;

        this.openCaptures[fullPath] = analysis;
        this.openOrder.Add(fullPath);

        string capturedSource = source;

        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("status", capturedSource == "cache" ? "openedFromCache" : "parsed");
            writer.WritePropertyName("capture");
            McpTools.WriteCaptureSummary(writer, analysis);
            writer.WriteEndObject();
        });
    }

    private CaptureAnalysis ResolveCapture(JsonElement arguments, out string failure)
    {
        failure = null;

        string requestedCapture = McpProtocol.TryGetString(arguments, "capture");

        if (requestedCapture != null)
        {
            string fullPath;

            try
            {
                fullPath = Path.GetFullPath(requestedCapture);
            }
            catch (Exception)
            {
                failure = "Not a usable file path: " + requestedCapture;
                return null;
            }

            if (this.openCaptures.TryGetValue(fullPath, out CaptureAnalysis named))
            {
                return named;
            }

            failure = "Capture is not open: " + fullPath + ". Call open_capture first.";
            return null;
        }

        if (this.openOrder.Count == 0)
        {
            failure = "No capture is open. Call open_capture with the path to a .nettrace file first.";
            return null;
        }

        return this.openCaptures[this.openOrder[this.openOrder.Count - 1]];
    }

    private InsightReport ReportFor(CaptureAnalysis analysis)
    {
        if (this.reportsByCapture.TryGetValue(analysis.SourcePath, out InsightReport existing))
        {
            return existing;
        }

        InsightReport report = InsightEngine.Run(analysis);
        this.reportsByCapture[analysis.SourcePath] = report;

        return report;
    }

    ////////////////////////////////////////////////////////////////////////////

    private static bool TryGetBool(JsonElement parent, string propertyName, bool fallback)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        // Same tolerance as the numeric accessors: a model emitting "true" as
        // a string has expressed itself perfectly clearly.
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            using (Utf8JsonWriter writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                write(writer);
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Reported in the initialize handshake so a client (and a bug report) can say
// which build answered. Kept next to the cache's own format version, which is
// the other thing that has to move when the analysis shape changes.
public static class NettraceParserVersion
{
    public const string Value = "1.10.0";
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Mcp)
