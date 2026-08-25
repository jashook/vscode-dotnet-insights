////////////////////////////////////////////////////////////////////////////////
// Module: PerfScriptReaderTests.cs
//
// Notes:
// The text path exists because `perf script` output - not perf.data - is the
// artifact that leaves a production host. These pin the parsing rules that make
// it readable, using real lines from a real capture.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Tests {

using System;
using System.Collections.Generic;
using System.IO;

using Xunit;

using DotnetInsights.Perf.PerfScript;

public class PerfScriptReaderTests
{
    private sealed class RecordingSink : IPerfScriptSink
    {
        public readonly List<string> Comms = new List<string>();
        public readonly List<long> ThreadIds = new List<long>();
        public readonly List<double> Times = new List<double>();
        public readonly List<string> EventNames = new List<string>();
        public readonly List<List<string>> Stacks = new List<List<string>>();

        public void OnScriptSample(string comm, long threadId, double timeSeconds, string eventName, IReadOnlyList<string> frames)
        {
            this.Comms.Add(comm);
            this.ThreadIds.Add(threadId);
            this.Times.Add(timeSeconds);
            this.EventNames.Add(eventName);
            this.Stacks.Add(new List<string>(frames));
        }
    }

    private static RecordingSink ReadText(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "perfscript-" + Guid.NewGuid().ToString("n") + ".stacks");
        File.WriteAllText(path, text);

        try
        {
            RecordingSink sink = new RecordingSink();
            PerfScriptReader reader = new PerfScriptReader();
            PerfScriptReadResult result = reader.Read(path, sink);
            Assert.True(result.Success, result.ErrorMessage);
            return sink;
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The shape produced by `perf record -p <pid>`: comm, tid, timestamp,
    // period, event - no pid/tid pair and no [cpu] field.
    [Fact]
    public void Read_PerProcessSample_ParsesCommThreadAndFrames()
    {
        RecordingSink sink = ReadText(
            "dynamic      21    14.672658:    2004008 cpu-clock: \n" +
            "\t    aaaac9efb184 hot_leaf_alpha+0x88 (/work/rust_workload)\n" +
            "\t    ffffa519582c [unknown] (/usr/lib/aarch64-linux-gnu/libc.so.6)\n" +
            "\n");

        Assert.Single(sink.Stacks);
        Assert.Equal("dynamic", sink.Comms[0]);
        Assert.Equal(21, sink.ThreadIds[0]);
        Assert.Equal(14.672658, sink.Times[0], 6);
        Assert.Equal("cpu-clock", sink.EventNames[0]);

        // Leaf first, the +0xNN dropped - keeping it splits one hot function
        // into a row per sampled instruction.
        Assert.Equal("hot_leaf_alpha", sink.Stacks[0][0]);

        // An unresolved frame becomes "module+0xADDRESS", the shape the CPU
        // category classifier recognises as unresolved.
        Assert.Equal("libc.so.6+0xffffa519582c", sink.Stacks[0][1]);
    }

    // The shape produced by `perf record -a`: a pid/tid pair and a [cpu] field.
    // The tid is what identifies a thread.
    [Fact]
    public void Read_SystemWideSample_TakesTheThreadIdFromThePidSlashTidPair()
    {
        RecordingSink sink = ReadText(
            "envoy 12345/12399 [002]  9876.543210:     10101 cpu-clock: \n" +
            "\t    7f0000001234 Envoy::Http::ConnectionManagerImpl::onData (/usr/local/bin/envoy)\n" +
            "\n");

        Assert.Single(sink.Stacks);
        Assert.Equal(12399, sink.ThreadIds[0]);
        Assert.Equal("Envoy::Http::ConnectionManagerImpl::onData", sink.Stacks[0][0]);
    }

    // A comm can contain spaces. Parsing from the LEFT and counting tokens
    // misreads those lines into a plausible tid and timestamp rather than
    // failing, which is why the parser works from the right.
    [Fact]
    public void Read_CommContainingSpaces_IsNotMistakenForExtraFields()
    {
        RecordingSink sink = ReadText(
            "my worker thread   77    1.234567:    500000 cpu-clock: \n" +
            "\t    aaaa00001111 some_function+0x4 (/bin/app)\n" +
            "\n");

        Assert.Single(sink.Stacks);
        Assert.Equal(77, sink.ThreadIds[0]);
        Assert.Equal("my worker thread", sink.Comms[0]);
    }

    // The --header block carries the capture's provenance, which is most of
    // what a reader wants to know before trusting a profile.
    [Fact]
    public void Read_HeaderBlock_IsParsed()
    {
        string path = Path.Combine(Path.GetTempPath(), "perfscript-" + Guid.NewGuid().ToString("n") + ".stacks");
        File.WriteAllText(path,
            "# ========\n" +
            "# captured on    : Sun Aug 23 02:01:44 2026\n" +
            "# hostname : prod-host-7\n" +
            "# os release : 5.15.0-91-generic\n" +
            "# arch : x86_64\n" +
            "# perf version : 5.15.148\n" +
            "# nrcpus online : 16\n" +
            "# cmdline : /usr/bin/perf record -g -e cpu-clock -F 99 -p 4242 -- sleep 30 \n" +
            "# event : name = cpu-clock, , id = { 1 }, type = 1 (software)\n" +
            "# ========\n" +
            "#\n" +
            "envoy 4242/4250 [001]  100.000000:     10101 cpu-clock: \n" +
            "\t    7f0000001234 main (/usr/local/bin/envoy)\n" +
            "\n");

        try
        {
            RecordingSink sink = new RecordingSink();
            PerfScriptReader reader = new PerfScriptReader();
            Assert.True(reader.Read(path, sink).Success);

            Assert.Equal("prod-host-7", reader.Header.HostName);
            Assert.Equal("x86_64", reader.Header.Architecture);
            Assert.Equal("5.15.148", reader.Header.PerfVersion);
            Assert.Equal(16, reader.Header.CpuCount);
            Assert.Contains("cpu-clock", reader.Header.EventNames);
            Assert.Contains("-p 4242", reader.Header.CommandLine);
            Assert.Equal(1, reader.SampleCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A file whose final sample has no trailing blank line must not lose that
    // sample - and neither must a file that uses no blank separators at all.
    [Fact]
    public void Read_LastSampleWithoutTrailingBlankLine_IsStillEmitted()
    {
        RecordingSink sink = ReadText(
            "a 1  1.000000:  1 cpu-clock: \n" +
            "\t    aaaa00001111 first (/bin/app)\n" +
            "b 2  2.000000:  1 cpu-clock: \n" +
            "\t    aaaa00002222 second (/bin/app)\n");

        Assert.Equal(2, sink.Stacks.Count);
        Assert.Equal("first", sink.Stacks[0][0]);
        Assert.Equal("second", sink.Stacks[1][0]);
    }

    // A perf.data must NOT be taken for text - the two paths decode completely
    // differently and the binary one would be parsed as garbage.
    [Fact]
    public void LooksLikePerfScript_RejectsABinaryPerfData()
    {
        string path = Path.Combine(Path.GetTempPath(), "perfdata-" + Guid.NewGuid().ToString("n") + ".data");
        File.WriteAllBytes(path, new byte[] { (byte)'P', (byte)'E', (byte)'R', (byte)'F', (byte)'I', (byte)'L', (byte)'E', (byte)'2', 0, 0 });

        try
        {
            Assert.False(PerfScriptReader.LooksLikePerfScript(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

} // end of namespace(DotnetInsights.Perf.Tests)
