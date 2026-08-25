////////////////////////////////////////////////////////////////////////////////
// Module: PerfScriptReader.cs
//
// Notes:
// Reads `perf script` TEXT output - the `perf.stacks` file - as a first-class
// input alongside the binary perf.data.
//
// This is not a convenience. In the workflow this tool is actually used in, the
// capture is taken on a production host and the artifact that LEAVES that host
// is the text, not the perf.data:
//
//     perf record -g -e cpu-clock -F 99 -p <pid> -- sleep <n>
//     perf script --header -i perf.data > perf.stacks
//
// and there is a good reason for it. A perf.data is opaque - it embeds module
// paths, build ids, and under `--call-graph dwarf` literal copies of process
// stack memory - so it cannot be eyeballed before it is shared. The text can.
// A reader that only accepts the binary is a reader that cannot open the file
// people are willing to hand over.
//
// Two consequences of the text form, both of which simplify things:
//
//   - Frames arrive ALREADY SYMBOLIZED, by perf, on the host that has the
//     binaries. No build ids, no symbol servers, no address translation. That
//     is strictly better symbolization than this tool can do from a Mac.
//   - Frames have no usable addresses. `perf script` prints the runtime
//     instruction pointer, which means nothing without the mappings, and the
//     mappings are not in the text. So frames are keyed by NAME, and each
//     distinct name is given a synthetic address - the same mechanism inline
//     frames already use - so everything downstream works unchanged.
//
// perf does NOT demangle Rust v0, so names arrive mangled for Rust and
// demangled for C++ (perf demangles Itanium itself). Running the demangler over
// every name fixes the first and is a no-op for the second.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.PerfScript {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Everything the `--header` block tells us about the capture. All optional: a
// file produced without --header still parses, it just has less to say.
public sealed class PerfScriptHeader
{
    public string HostName = string.Empty;
    public string OsRelease = string.Empty;
    public string PerfVersion = string.Empty;
    public string Architecture = string.Empty;
    public string CommandLine = string.Empty;
    public string CapturedOn = string.Empty;
    public int CpuCount;
    public readonly List<string> EventNames = new List<string>();
}

// Receives each decoded sample. An interface, not a list, because a long
// capture's text runs to hundreds of MB and the caller decides what to retain.
public interface IPerfScriptSink
{
    // frames are LEAF FIRST, matching both perf's own output order and the
    // order every stack in this codebase carries.
    void OnScriptSample(string comm, long threadId, double timeSeconds, string eventName, IReadOnlyList<string> frames);
}

public readonly struct PerfScriptReadResult
{
    public readonly bool Success;
    public readonly string ErrorMessage;

    private PerfScriptReadResult(bool success, string errorMessage)
    {
        this.Success = success;
        this.ErrorMessage = errorMessage;
    }

    public static PerfScriptReadResult Ok()
    {
        return new PerfScriptReadResult(true, null);
    }

    public static PerfScriptReadResult Fail(string errorMessage)
    {
        return new PerfScriptReadResult(false, errorMessage);
    }
}

public sealed class PerfScriptReader
{
    public readonly PerfScriptHeader Header = new PerfScriptHeader();

    public long SampleCount { get; private set; }

    public long FrameCount { get; private set; }

    public long UnparsedLineCount { get; private set; }

    // Recognises the text form. A perf.data starts with "PERFILE2"; anything
    // that starts with a '#' comment or parses as a sample header is text.
    public static bool LooksLikePerfScript(string filePath)
    {
        try
        {
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] probe = new byte[8];
                int read = stream.Read(probe, 0, probe.Length);

                if (read >= 8 &&
                    probe[0] == (byte)'P' && probe[1] == (byte)'E' && probe[2] == (byte)'R' && probe[3] == (byte)'F' &&
                    probe[4] == (byte)'I' && probe[5] == (byte)'L' && probe[6] == (byte)'E' && probe[7] == (byte)'2')
                {
                    return false;
                }

                // A text file's first bytes must all be printable or whitespace.
                for (int probeIndex = 0; probeIndex < read; ++probeIndex)
                {
                    byte value = probe[probeIndex];
                    bool printable = value == (byte)'\n' || value == (byte)'\r' || value == (byte)'\t' || (value >= 0x20 && value < 0x7F);
                    if (!printable)
                    {
                        return false;
                    }
                }

                return read > 0;
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    public PerfScriptReadResult Read(string filePath, IPerfScriptSink sink)
    {
        if (!File.Exists(filePath))
        {
            return PerfScriptReadResult.Fail("No such file: " + filePath);
        }

        List<string> frames = new List<string>();

        string currentComm = null;
        long currentThreadId = 0;
        double currentTimeSeconds = 0;
        string currentEventName = null;
        bool inSample = false;

        // Streamed, not read whole: a capture of any length produces text far
        // larger than the perf.data it came from.
        using (StreamReader reader = new StreamReader(filePath))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0)
                {
                    if (inSample)
                    {
                        this.Emit(sink, currentComm, currentThreadId, currentTimeSeconds, currentEventName, frames);
                        inSample = false;
                    }

                    continue;
                }

                if (line[0] == '#')
                {
                    this.ParseHeaderLine(line);
                    continue;
                }

                // A frame line is indented; a sample header line is not.
                if (line[0] == ' ' || line[0] == '\t')
                {
                    if (inSample)
                    {
                        string frameName = ParseFrameLine(line);
                        if (frameName != null)
                        {
                            frames.Add(frameName);
                        }
                    }

                    continue;
                }

                // A new sample header closes any sample still open - a capture
                // whose last sample has no trailing blank line would otherwise
                // be dropped, and so would every sample in a file that uses no
                // blank separators at all.
                if (inSample)
                {
                    this.Emit(sink, currentComm, currentThreadId, currentTimeSeconds, currentEventName, frames);
                }

                inSample = TryParseSampleHeader(line, out currentComm, out currentThreadId, out currentTimeSeconds, out currentEventName);

                if (!inSample)
                {
                    ++this.UnparsedLineCount;
                }

                frames.Clear();
            }
        }

        if (inSample)
        {
            this.Emit(sink, currentComm, currentThreadId, currentTimeSeconds, currentEventName, frames);
        }

        if (this.SampleCount == 0)
        {
            return PerfScriptReadResult.Fail(
                "No samples found. This does not look like `perf script` output - it should contain lines like " +
                "\"comm  1234  12.345678:  1000000 cpu-clock:\" followed by indented stack frames.");
        }

        return PerfScriptReadResult.Ok();
    }

    private void Emit(IPerfScriptSink sink, string comm, long threadId, double timeSeconds, string eventName, List<string> frames)
    {
        ++this.SampleCount;
        this.FrameCount += frames.Count;
        sink.OnScriptSample(comm, threadId, timeSeconds, eventName, frames);
        frames.Clear();
    }

    private void ParseHeaderLine(string line)
    {
        // "# hostname : cf51d08ffe10"
        int separator = line.IndexOf(" : ", StringComparison.Ordinal);
        if (separator < 0)
        {
            return;
        }

        string key = line.Substring(1, separator - 1).Trim();
        string value = line.Substring(separator + 3).Trim();

        switch (key)
        {
            case "hostname": this.Header.HostName = value; break;
            case "os release": this.Header.OsRelease = value; break;
            case "perf version": this.Header.PerfVersion = value; break;
            case "arch": this.Header.Architecture = value; break;
            case "cmdline": this.Header.CommandLine = value; break;
            case "captured on": this.Header.CapturedOn = value; break;

            case "nrcpus online":
            {
                int cpuCount;
                if (int.TryParse(value, out cpuCount))
                {
                    this.Header.CpuCount = cpuCount;
                }

                break;
            }

            case "event":
            {
                // "name = cpu-clock, , id = { ... }, ..."
                const string NamePrefix = "name = ";
                if (value.StartsWith(NamePrefix, StringComparison.Ordinal))
                {
                    int comma = value.IndexOf(',', NamePrefix.Length);
                    string eventName = comma > 0
                        ? value.Substring(NamePrefix.Length, comma - NamePrefix.Length)
                        : value.Substring(NamePrefix.Length);

                    this.Header.EventNames.Add(eventName.Trim());
                }

                break;
            }

            default:
                break;
        }
    }

    ////////////////////////////////////////////////////////////////////////////
    // "dynamic      21    14.672658:    2004008 cpu-clock: "
    // "swapper     0/0   [002]  1234.567890:     10101 cycles: "
    //
    // Parsed from the RIGHT. The leading comm is a fixed-width field that can
    // itself contain spaces (a thread named "my worker" is legal), so anything
    // that splits from the left and counts tokens misreads those lines - and
    // misreads them into a plausible tid and timestamp rather than failing.
    ////////////////////////////////////////////////////////////////////////////
    private static bool TryParseSampleHeader(string line, out string comm, out long threadId, out double timeSeconds, out string eventName)
    {
        comm = null;
        threadId = 0;
        timeSeconds = 0;
        eventName = null;

        string trimmed = line.TrimEnd();

        // The line ends with "<event>:" - possibly with a trailing tracepoint
        // payload after it, which is ignored here.
        if (!trimmed.EndsWith(":", StringComparison.Ordinal))
        {
            int payloadStart = trimmed.IndexOf(": ", StringComparison.Ordinal);
            if (payloadStart < 0)
            {
                return false;
            }
        }

        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        // Walk right to left: find the token ending in ':' that is preceded by
        // an all-digit period, and preceded by a timestamp ending in ':'.
        for (int eventIndex = parts.Length - 1; eventIndex >= 3; --eventIndex)
        {
            if (!parts[eventIndex].EndsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            string periodToken = parts[eventIndex - 1];
            string timeToken = parts[eventIndex - 2];

            if (!IsAllDigits(periodToken) || !timeToken.EndsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            double parsedTime;
            if (!double.TryParse(timeToken.Substring(0, timeToken.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out parsedTime))
            {
                continue;
            }

            eventName = parts[eventIndex].Substring(0, parts[eventIndex].Length - 1);
            timeSeconds = parsedTime;

            // Everything before the timestamp is: comm, then tid or pid/tid,
            // and optionally a [cpu] field.
            int cursor = eventIndex - 3;

            if (cursor >= 0 && parts[cursor].Length > 1 && parts[cursor][0] == '[')
            {
                --cursor;
            }

            if (cursor < 0)
            {
                return false;
            }

            string threadToken = parts[cursor];
            int slash = threadToken.IndexOf('/');
            if (slash >= 0)
            {
                // "pid/tid" - the tid is what identifies a thread.
                threadToken = threadToken.Substring(slash + 1);
            }

            long parsedThreadId;
            if (!long.TryParse(threadToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedThreadId))
            {
                continue;
            }

            threadId = parsedThreadId;
            comm = cursor > 0 ? string.Join(" ", parts, 0, cursor) : string.Empty;
            return true;
        }

        return false;
    }

    private static bool IsAllDigits(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        for (int index = 0; index < value.Length; ++index)
        {
            if (value[index] < '0' || value[index] > '9')
            {
                return false;
            }
        }

        return true;
    }

    ////////////////////////////////////////////////////////////////////////////
    // "\t    aaaac9efb184 symbol+0x88 (/work/rust_workload)"
    // "\t    ffffa519582c [unknown] (/usr/lib/.../libc.so.6)"
    //
    // The +0xNN offset is DROPPED. Keeping it splits one hot function into a
    // row per sampled instruction, which is the same mistake the .NET side's
    // name-keyed frame ids exist to prevent.
    ////////////////////////////////////////////////////////////////////////////
    private static string ParseFrameLine(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // The module, in trailing parentheses.
        string module = null;
        int moduleStart = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
        if (moduleStart >= 0 && trimmed.EndsWith(")", StringComparison.Ordinal))
        {
            module = trimmed.Substring(moduleStart + 2, trimmed.Length - moduleStart - 3);
            trimmed = trimmed.Substring(0, moduleStart);
        }

        // The leading instruction pointer.
        string address = null;
        int firstSpace = trimmed.IndexOf(' ');
        if (firstSpace > 0)
        {
            address = trimmed.Substring(0, firstSpace);
            trimmed = trimmed.Substring(firstSpace + 1).Trim();
        }

        if (trimmed.Length == 0)
        {
            return null;
        }

        // Drop the +0xNN.
        int plus = trimmed.LastIndexOf("+0x", StringComparison.Ordinal);
        if (plus > 0)
        {
            trimmed = trimmed.Substring(0, plus);
        }

        if (trimmed == "[unknown]")
        {
            // Named "module+0xADDRESS", which is the shape every other part of
            // this project uses to mark an unresolved frame - the CPU category
            // classifier tests for it explicitly.
            string moduleName = module != null ? Path.GetFileName(module) : "[unknown]";
            if (moduleName.Length == 0)
            {
                moduleName = "[unknown]";
            }

            return address != null ? moduleName + "+0x" + address : moduleName;
        }

        return trimmed;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.PerfScript)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
