////////////////////////////////////////////////////////////////////////////////
// Module: McpProtocolTests.cs
//
// Notes:
// Drives McpServer over an in-memory transport with a synthetic CaptureAnalysis
// - no real process, no real capture. The protocol behaviours tested here are
// the ones whose failure modes are silent: a server that replies to a
// notification, or negotiates a version it cannot speak, or turns a missing
// file into a transport fault, does not look broken from the outside. It just
// stops working with some clients and not others.
//
// The analysis factory is injected, so "opening" a capture here means handing
// back a prepared object. That keeps these tests about the protocol and the
// tool surface rather than about the decoder, which has its own coverage.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using DotnetInsights.NetTrace.Analysis;
using DotnetInsights.NetTrace.Mcp;
using DotnetInsights.NetTrace.Threading;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class McpProtocolTests
{
    private static CaptureAnalysis SampleAnalysis(string path)
    {
        CaptureAnalysis analysis = new CaptureAnalysis();
        analysis.SourcePath = path;
        analysis.ProcessName = "sample-service";
        analysis.FormatVersion = 5;
        analysis.HasCaptureDuration = true;
        analysis.CaptureDurationMSec = 60_000;
        analysis.TotalEventCount = 1_234_567;
        analysis.NumberOfProcessors = 8;

        analysis.Contents.HasGcEvents = true;
        analysis.Contents.HasCpuSamples = true;
        analysis.Contents.SampleTypeSource = "runtime";

        analysis.Gc.Count = 3;
        analysis.Gc.TotalPauseMSec = 300;
        analysis.Gc.CountByGeneration[0] = 2;
        analysis.Gc.CountByGeneration[2] = 1;
        analysis.Gc.MaxPauseMSec = 250;
        analysis.Gc.MaxPauseGcId = 7;

        for (int gcIndex = 0; gcIndex < 3; ++gcIndex)
        {
            GcRecord record = new GcRecord();
            record.Id = gcIndex + 5;
            record.Generation = gcIndex == 2 ? 2 : 0;
            record.Reason = "AllocSmall";
            record.PauseDurationMSec = 50 * (gcIndex + 1);
            analysis.Gc.Gcs.Add(record);
        }

        analysis.Cpu.TotalSampleCount = 1000;

        HotMethodRecord parked = new HotMethodRecord();
        parked.Name = "System.Threading.LowLevelLifoSemaphore.WaitForSignal";
        parked.SelfSamples = 700;
        parked.SelfPercent = 70;
        parked.IsIdleWait = true;
        analysis.Cpu.HotMethods.Add(parked);

        HotMethodRecord running = new HotMethodRecord();
        running.Name = "Contoso.Handler.Run";
        running.SelfSamples = 200;
        running.SelfPercent = 20;
        analysis.Cpu.HotMethods.Add(running);

        ThreadRecord thread = new ThreadRecord();
        thread.ThreadId = 42;
        thread.RoleId = (int)ThreadActivityRole.BlockedPoolWorker;
        thread.Role = ThreadActivityProfiler.NameForRole(ThreadActivityRole.BlockedPoolWorker);
        thread.SampleCount = 500;
        analysis.Threading.Threads.Add(thread);
        analysis.Threading.ThreadCount = 1;
        analysis.Threading.BlockedPoolWorkerCount = 1;

        return analysis;
    }

    private static McpServer NewServer(Func<string, CaptureAnalysis> factory = null)
    {
        // No cache directory: these tests must not touch the user's real cache
        // or depend on one existing.
        return new McpServer(factory ?? SampleAnalysis, null, TextWriter.Null);
    }

    private static JsonElement Call(McpServer server, string method, string parametersJson = null, int id = 1)
    {
        string request = parametersJson == null
            ? $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\"}}"
            : $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{parametersJson}}}";

        string response = server.HandleLine(request);

        Assert.NotNull(response);

        return JsonDocument.Parse(response).RootElement.Clone();
    }

    private static JsonElement CallTool(McpServer server, string toolName, string argumentsJson = "{}")
    {
        JsonElement response = Call(server, "tools/call", $"{{\"name\":\"{toolName}\",\"arguments\":{argumentsJson}}}");

        return response.GetProperty("result");
    }

    private static JsonElement ToolPayload(JsonElement toolResult)
    {
        string text = toolResult.GetProperty("content")[0].GetProperty("text").GetString();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    ////////////////////////////////////////////////////////////////////////////
    // Handshake
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void Initialize_EchoesASupportedProtocolVersionAndDeclaresTools()
    {
        JsonElement response = Call(NewServer(), "initialize", "{\"protocolVersion\":\"2024-11-05\"}");
        JsonElement result = response.GetProperty("result");

        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Equal("nettraceParser", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    // An unknown version must not be echoed back - the client has to be told
    // what this server can actually speak so it can decide whether to proceed.
    [Fact]
    public void Initialize_FallsBackToItsOwnVersionForAnUnknownOne()
    {
        JsonElement result = Call(NewServer(), "initialize", "{\"protocolVersion\":\"1999-01-01\"}").GetProperty("result");

        Assert.Equal(McpProtocol.PreferredProtocolVersion, result.GetProperty("protocolVersion").GetString());
    }

    // Replying to a notification is a protocol violation that strict clients
    // drop the connection over, and it is invisible in casual testing.
    [Fact]
    public void Notification_IsNeverAnswered()
    {
        Assert.Null(NewServer().HandleLine("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
    }

    [Fact]
    public void MalformedLine_IsAParseErrorWithANullId()
    {
        JsonElement response = JsonDocument.Parse(NewServer().HandleLine("{not json")).RootElement;

        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
        Assert.Equal(McpErrorCode.ParseError, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void UnknownMethod_IsMethodNotFound()
    {
        JsonElement response = Call(NewServer(), "does/not/exist");

        Assert.Equal(McpErrorCode.MethodNotFound, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    // JSON-RPC allows a string or a number id and requires the reply to echo
    // it exactly; normalizing to one type makes replies unmatchable for a
    // client that used the other.
    [Fact]
    public void ResponseEchoesTheRequestIdWithItsOriginalType()
    {
        string response = NewServer().HandleLine("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"ping\"}");
        JsonElement id = JsonDocument.Parse(response).RootElement.GetProperty("id");

        Assert.Equal(JsonValueKind.String, id.ValueKind);
        Assert.Equal("abc", id.GetString());
    }

    ////////////////////////////////////////////////////////////////////////////
    // tools/list
    ////////////////////////////////////////////////////////////////////////////

    [Fact]
    public void ToolsList_EveryToolHasANameDescriptionAndObjectSchema()
    {
        JsonElement tools = Call(NewServer(), "tools/list").GetProperty("result").GetProperty("tools");

        Assert.True(tools.GetArrayLength() > 0);

        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonElement tool in tools.EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString();

            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(names.Add(name), "duplicate tool name: " + name);
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()));
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
        }

        Assert.Contains("open_capture", names);
        Assert.Contains("get_insights", names);
    }

    ////////////////////////////////////////////////////////////////////////////
    // tools/call
    ////////////////////////////////////////////////////////////////////////////

    // A capture-level failure is a successful call carrying isError, not a
    // transport fault - so the model can read it and correct itself.
    [Fact]
    public void QueryingWithNoCaptureOpen_IsAToolErrorNotAProtocolError()
    {
        JsonElement response = Call(NewServer(), "tools/call", "{\"name\":\"get_gc_summary\",\"arguments\":{}}");

        Assert.False(response.TryGetProperty("error", out _));

        JsonElement result = response.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("open_capture", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void OpenCapture_WithoutAPath_IsAToolError()
    {
        JsonElement result = CallTool(NewServer(), "open_capture");

        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public void OpenCapture_ForAMissingFile_NamesThePathAndDoesNotThrow()
    {
        JsonElement result = CallTool(NewServer(), "open_capture", "{\"path\":\"/nope/absent.nettrace\"}");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("absent.nettrace", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    // A decoder that throws must come back as a readable tool error, not as a
    // dead connection.
    [Fact]
    public void OpenCapture_WhenParsingThrows_ReportsItAsAToolError()
    {
        string existingFile = Path.GetTempFileName();

        try
        {
            McpServer server = NewServer(path => throw new InvalidOperationException("decoder exploded"));

            JsonElement result = CallTool(server, "open_capture", $"{{\"path\":{JsonSerializer.Serialize(existingFile)}}}");

            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Contains("decoder exploded", result.GetProperty("content")[0].GetProperty("text").GetString());
        }
        finally
        {
            File.Delete(existingFile);
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Answers, against an opened capture
    ////////////////////////////////////////////////////////////////////////////

    private static McpServer ServerWithOpenCapture(out string capturePath)
    {
        capturePath = Path.GetTempFileName();
        string localPath = capturePath;

        McpServer server = NewServer(path => SampleAnalysis(localPath));

        JsonElement result = CallTool(server, "open_capture", $"{{\"path\":{JsonSerializer.Serialize(localPath)}}}");
        Assert.False(result.GetProperty("isError").GetBoolean());

        return server;
    }

    [Fact]
    public void GetCaptureSummary_ReportsWhatWasAndWasNotRecorded()
    {
        McpServer server = ServerWithOpenCapture(out string capturePath);

        try
        {
            JsonElement payload = ToolPayload(CallTool(server, "get_capture_summary"));
            JsonElement recorded = payload.GetProperty("recorded");

            Assert.Equal("sample-service", payload.GetProperty("processName").GetString());
            Assert.True(recorded.GetProperty("gcEvents").GetBoolean());
            // The distinction the whole model exists to preserve.
            Assert.False(recorded.GetProperty("allocationTicks").GetBoolean());
        }
        finally
        {
            File.Delete(capturePath);
        }
    }

    [Fact]
    public void QueryGcs_FiltersByGenerationAndOrdersByPauseDescending()
    {
        McpServer server = ServerWithOpenCapture(out string capturePath);

        try
        {
            JsonElement all = ToolPayload(CallTool(server, "query_gcs", "{\"limit\":10}"));
            Assert.Equal(3, all.GetProperty("matchingCount").GetInt32());

            double previousPause = double.MaxValue;

            foreach (JsonElement collection in all.GetProperty("collections").EnumerateArray())
            {
                double pause = collection.GetProperty("pauseDurationMSec").GetDouble();
                Assert.True(pause <= previousPause, "collections are not ordered by pause descending");
                previousPause = pause;
            }

            JsonElement gen2 = ToolPayload(CallTool(server, "query_gcs", "{\"generation\":2}"));
            Assert.Equal(1, gen2.GetProperty("matchingCount").GetInt32());
        }
        finally
        {
            File.Delete(capturePath);
        }
    }

    // The parked-thread trap: blocking primitives are excluded by default, but
    // their share is always reported because it changes how the remaining
    // percentages should be read.
    [Fact]
    public void GetHotMethods_ExcludesBlockingPrimitivesButStatesTheirShare()
    {
        McpServer server = ServerWithOpenCapture(out string capturePath);

        try
        {
            JsonElement byDefault = ToolPayload(CallTool(server, "get_hot_methods"));

            Assert.Equal(1, byDefault.GetProperty("methods").GetArrayLength());
            Assert.Equal("Contoso.Handler.Run", byDefault.GetProperty("methods")[0].GetProperty("name").GetString());
            Assert.Equal(70.0, byDefault.GetProperty("percentOfSamplesInBlockingPrimitives").GetDouble());

            JsonElement including = ToolPayload(CallTool(server, "get_hot_methods", "{\"includeIdleWait\":true}"));
            Assert.Equal(2, including.GetProperty("methods").GetArrayLength());
        }
        finally
        {
            File.Delete(capturePath);
        }
    }

    [Fact]
    public void GetThreads_FiltersByRole()
    {
        McpServer server = ServerWithOpenCapture(out string capturePath);

        try
        {
            JsonElement blocked = ToolPayload(CallTool(server, "get_threads", "{\"role\":\"blocked pool worker\"}"));
            Assert.Equal(1, blocked.GetProperty("returnedCount").GetInt32());

            JsonElement active = ToolPayload(CallTool(server, "get_threads", "{\"role\":\"Active\"}"));
            Assert.Equal(0, active.GetProperty("returnedCount").GetInt32());
        }
        finally
        {
            File.Delete(capturePath);
        }
    }

    [Fact]
    public void GetInsights_ReturnsFindingsAndTheRulesThatDidNotFire()
    {
        McpServer server = ServerWithOpenCapture(out string capturePath);

        try
        {
            JsonElement payload = ToolPayload(CallTool(server, "get_insights"));

            Assert.True(payload.TryGetProperty("insights", out _));

            // The audit list must be there by default: without it an agent
            // cannot tell a clean area from an unmeasured one.
            Assert.True(payload.TryGetProperty("rulesEvaluated", out JsonElement rules));
            Assert.True(rules.GetArrayLength() > 0);

            foreach (JsonElement rule in rules.EnumerateArray())
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.GetProperty("id").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(rule.GetProperty("firesWhen").GetString()));
            }
        }
        finally
        {
            File.Delete(capturePath);
        }
    }

    [Fact]
    public void GetInsights_RejectsAnUnknownSeverityRatherThanSilentlyReturningEverything()
    {
        McpServer server = ServerWithOpenCapture(out string capturePath);

        try
        {
            JsonElement result = CallTool(server, "get_insights", "{\"severity\":\"catastrophic\"}");

            Assert.True(result.GetProperty("isError").GetBoolean());
        }
        finally
        {
            File.Delete(capturePath);
        }
    }

    // Models routinely send numbers and booleans as strings. Refusing those is
    // a protocol-lawyer's answer to a clearly expressed request.
    [Fact]
    public void ToolArguments_TolerateNumbersAndBooleansSentAsStrings()
    {
        McpServer server = ServerWithOpenCapture(out string capturePath);

        try
        {
            JsonElement payload = ToolPayload(CallTool(server, "get_hot_methods", "{\"limit\":\"1\",\"includeIdleWait\":\"true\"}"));

            Assert.Equal(1, payload.GetProperty("methods").GetArrayLength());
        }
        finally
        {
            File.Delete(capturePath);
        }
    }

    [Fact]
    public void ReopeningTheSameCapture_DoesNotReparseIt()
    {
        string capturePath = Path.GetTempFileName();

        try
        {
            int parseCount = 0;
            McpServer server = NewServer(path =>
            {
                ++parseCount;
                return SampleAnalysis(path);
            });

            string arguments = $"{{\"path\":{JsonSerializer.Serialize(capturePath)}}}";

            CallTool(server, "open_capture", arguments);
            JsonElement second = ToolPayload(CallTool(server, "open_capture", arguments));

            Assert.Equal(1, parseCount);
            Assert.Equal("alreadyOpen", second.GetProperty("status").GetString());
        }
        finally
        {
            File.Delete(capturePath);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
