////////////////////////////////////////////////////////////////////////////////
// Module: TraceEventFormats.cs
//
// Notes:
// Parses the TRACING_DATA feature section, which is how a perf.data carries
// the ftrace `format` descriptions for every tracepoint it recorded. Without
// it a tracepoint sample's PERF_SAMPLE_RAW payload is an opaque blob: the
// payload is a packed C struct whose field offsets come from the running
// kernel's own layout and are NOT stable across kernel versions, so they
// cannot be hardcoded. `sched_switch`'s `prev_state` in particular has both
// moved and changed width across releases.
//
// The section is the "trace.dat" format, and its shape was confirmed against a
// real capture rather than recalled:
//
//     17 08 44 "tracing"      magic
//     "0.6\0"                 version
//     <u8 endian> <u8 long size> <u32 page size>
//     "header_page\0"  <u64 size> <bytes>
//     "header_event\0" <u64 size> <bytes>
//     <u32 count> { <u64 size> <format text> }        ftrace events
//     <u32 count> { <name\0> <u32 count> { <u64 size> <format text> } }  systems
//     <u32 size> <kallsyms text>
//     <u32 size> <printk text>
//     <u64 size> <saved cmdlines>                     version >= 0.6 only
//
// The kallsyms block is the reason this file earns its keep twice over: a
// perf.data otherwise contains NO kernel symbol names at all - perf resolves
// them by reading /proc/kallsyms on the recording machine at report time,
// which is useless when the capture is read somewhere else. Any capture with a
// tracepoint in it carries the recording kernel's own symbol table inline.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.Perf.Tracepoints {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

using DotnetInsights.Perf.PerfData;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public struct TraceEventField
{
    public string Name;
    public int Offset;
    public int Size;
    public bool IsSigned;
}

public sealed class TraceEventFormat
{
    public int Id;
    public string SystemName = string.Empty;
    public string Name = string.Empty;

    public readonly List<TraceEventField> Fields = new List<TraceEventField>();

    public string QualifiedName => this.SystemName.Length > 0 ? this.SystemName + ":" + this.Name : this.Name;

    public bool TryGetField(string fieldName, out TraceEventField field)
    {
        for (int fieldIndex = 0; fieldIndex < this.Fields.Count; ++fieldIndex)
        {
            if (string.Equals(this.Fields[fieldIndex].Name, fieldName, StringComparison.Ordinal))
            {
                field = this.Fields[fieldIndex];
                return true;
            }
        }

        field = default(TraceEventField);
        return false;
    }

    // Reads one field out of a raw tracepoint payload. Width comes from the
    // format, not from the caller: reading a `long prev_state` as an int is
    // correct on one kernel and silently wrong on another.
    public bool TryReadInt64(ReadOnlySpan<byte> raw, string fieldName, out long value)
    {
        value = 0;

        TraceEventField field;
        if (!this.TryGetField(fieldName, out field))
        {
            return false;
        }

        if (field.Offset < 0 || field.Size <= 0 || field.Offset + field.Size > raw.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> slice = raw.Slice(field.Offset, field.Size);

        switch (field.Size)
        {
            case 1:
                value = field.IsSigned ? (sbyte)slice[0] : slice[0];
                return true;

            case 2:
                value = field.IsSigned
                    ? BinaryPrimitives.ReadInt16LittleEndian(slice)
                    : BinaryPrimitives.ReadUInt16LittleEndian(slice);
                return true;

            case 4:
                value = field.IsSigned
                    ? BinaryPrimitives.ReadInt32LittleEndian(slice)
                    : BinaryPrimitives.ReadUInt32LittleEndian(slice);
                return true;

            case 8:
                value = BinaryPrimitives.ReadInt64LittleEndian(slice);
                return true;

            default:
                return false;
        }
    }

    // A fixed-width char array field, e.g. sched_switch's `prev_comm[16]`.
    public bool TryReadString(ReadOnlySpan<byte> raw, string fieldName, out string value)
    {
        value = null;

        TraceEventField field;
        if (!this.TryGetField(fieldName, out field))
        {
            return false;
        }

        if (field.Offset < 0 || field.Size <= 0 || field.Offset + field.Size > raw.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> slice = raw.Slice(field.Offset, field.Size);
        int terminator = slice.IndexOf((byte)0);
        if (terminator >= 0)
        {
            slice = slice.Slice(0, terminator);
        }

        value = Encoding.UTF8.GetString(slice);
        return true;
    }
}

public sealed class TraceEventFormats
{
    private static readonly byte[] Magic = new byte[] { 0x17, 0x08, 0x44 };

    private readonly Dictionary<int, TraceEventFormat> formatsById = new Dictionary<int, TraceEventFormat>();

    public string Version = string.Empty;

    // The recording kernel's own /proc/kallsyms, verbatim. See this file's
    // header for why it matters.
    public string KallsymsText = string.Empty;

    public int Count => this.formatsById.Count;

    public bool TryGetById(int eventId, out TraceEventFormat format)
    {
        return this.formatsById.TryGetValue(eventId, out format);
    }

    public TraceEventFormat FindByName(string qualifiedName)
    {
        foreach (KeyValuePair<int, TraceEventFormat> entry in this.formatsById)
        {
            if (string.Equals(entry.Value.QualifiedName, qualifiedName, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    public IEnumerable<TraceEventFormat> All => this.formatsById.Values;

    public static TraceEventFormats TryParse(ReadOnlySpan<byte> section, out string error)
    {
        error = null;

        if (section.Length < 16)
        {
            error = "tracing data section is too small";
            return null;
        }

        for (int magicIndex = 0; magicIndex < Magic.Length; ++magicIndex)
        {
            if (section[magicIndex] != Magic[magicIndex])
            {
                error = "tracing data section has the wrong magic";
                return null;
            }
        }

        PerfSpanReader reader = new PerfSpanReader(section);
        reader.Skip(3);

        string tracingTag = reader.ReadFixedString(7);
        if (!string.Equals(tracingTag, "tracing", StringComparison.Ordinal))
        {
            error = "tracing data section is not a trace.dat stream";
            return null;
        }

        TraceEventFormats formats = new TraceEventFormats();
        formats.Version = ReadNulTerminated(ref reader);

        // endian, long size, page size
        reader.Skip(1);
        reader.Skip(1);
        reader.Skip(4);

        // header_page and header_event describe the ring buffer's own framing,
        // which nothing here reads - the payloads arrive already unpacked as
        // PERF_SAMPLE_RAW.
        if (!SkipNamedBlock(ref reader, "header_page") || !SkipNamedBlock(ref reader, "header_event"))
        {
            error = "tracing data header blocks are malformed";
            return null;
        }

        // The ftrace events (function tracer and friends). Parsed the same way
        // as the rest, since a capture could name one.
        uint ftraceEventCount = reader.ReadUInt32();
        for (uint eventIndex = 0; eventIndex < ftraceEventCount; ++eventIndex)
        {
            long formatLength = (long)reader.ReadUInt64();
            if (formatLength < 0 || formatLength > reader.Remaining)
            {
                error = "tracing data ftrace event block is truncated";
                return null;
            }

            string formatText = Encoding.UTF8.GetString(reader.ReadBytes((int)formatLength));
            formats.AddFormat("ftrace", formatText);
        }

        uint systemCount = reader.ReadUInt32();
        for (uint systemIndex = 0; systemIndex < systemCount; ++systemIndex)
        {
            string systemName = ReadNulTerminated(ref reader);
            uint eventCount = reader.ReadUInt32();

            for (uint eventIndex = 0; eventIndex < eventCount; ++eventIndex)
            {
                long formatLength = (long)reader.ReadUInt64();
                if (formatLength < 0 || formatLength > reader.Remaining)
                {
                    error = "tracing data event block is truncated";
                    return null;
                }

                string formatText = Encoding.UTF8.GetString(reader.ReadBytes((int)formatLength));
                formats.AddFormat(systemName, formatText);
            }
        }

        // kallsyms. A u32 length, unlike the u64 lengths above.
        uint kallsymsLength = reader.ReadUInt32();
        if (kallsymsLength > 0 && kallsymsLength <= (uint)reader.Remaining)
        {
            formats.KallsymsText = Encoding.UTF8.GetString(reader.ReadBytes((int)kallsymsLength));
        }

        return formats;
    }

    private void AddFormat(string systemName, string formatText)
    {
        TraceEventFormat format = ParseFormatText(systemName, formatText);
        if (format != null && format.Id != 0)
        {
            this.formatsById[format.Id] = format;
        }
    }

    // The `format` file's text, as the kernel exposes it under
    // /sys/kernel/tracing/events/<system>/<event>/format.
    private static TraceEventFormat ParseFormatText(string systemName, string formatText)
    {
        TraceEventFormat format = new TraceEventFormat();
        format.SystemName = systemName;

        string[] lines = formatText.Split('\n');

        for (int lineIndex = 0; lineIndex < lines.Length; ++lineIndex)
        {
            string line = lines[lineIndex].Trim();

            if (line.StartsWith("name:", StringComparison.Ordinal))
            {
                format.Name = line.Substring(5).Trim();
                continue;
            }

            if (line.StartsWith("ID:", StringComparison.Ordinal))
            {
                int id;
                if (int.TryParse(line.Substring(3).Trim(), out id))
                {
                    format.Id = id;
                }

                continue;
            }

            if (!line.StartsWith("field:", StringComparison.Ordinal))
            {
                continue;
            }

            // field:<type> <name>;\toffset:N;\tsize:N;\tsigned:N;
            TraceEventField field = new TraceEventField();
            field.Offset = -1;

            string[] parts = line.Split(';');
            for (int partIndex = 0; partIndex < parts.Length; ++partIndex)
            {
                string part = parts[partIndex].Trim();

                if (part.StartsWith("field:", StringComparison.Ordinal))
                {
                    // The declaration is "<type...> <name>", possibly with an
                    // array suffix: `char prev_comm[16]`. The name is the last
                    // whitespace-separated token with any array suffix removed.
                    string declaration = part.Substring(6).Trim();
                    int lastSpace = declaration.LastIndexOf(' ');
                    string name = lastSpace >= 0 ? declaration.Substring(lastSpace + 1) : declaration;

                    int bracket = name.IndexOf('[');
                    if (bracket >= 0)
                    {
                        name = name.Substring(0, bracket);
                    }

                    field.Name = name.TrimStart('*');
                }
                else if (part.StartsWith("offset:", StringComparison.Ordinal))
                {
                    int offset;
                    if (int.TryParse(part.Substring(7).Trim(), out offset))
                    {
                        field.Offset = offset;
                    }
                }
                else if (part.StartsWith("size:", StringComparison.Ordinal))
                {
                    int size;
                    if (int.TryParse(part.Substring(5).Trim(), out size))
                    {
                        field.Size = size;
                    }
                }
                else if (part.StartsWith("signed:", StringComparison.Ordinal))
                {
                    field.IsSigned = part.Substring(7).Trim() == "1";
                }
            }

            if (!string.IsNullOrEmpty(field.Name) && field.Offset >= 0 && field.Size > 0)
            {
                format.Fields.Add(field);
            }
        }

        return format;
    }

    private static bool SkipNamedBlock(ref PerfSpanReader reader, string expectedName)
    {
        string name = ReadNulTerminated(ref reader);
        if (!string.Equals(name, expectedName, StringComparison.Ordinal))
        {
            return false;
        }

        long blockLength = (long)reader.ReadUInt64();
        if (blockLength < 0 || blockLength > reader.Remaining)
        {
            return false;
        }

        reader.Skip((int)blockLength);
        return true;
    }

    private static string ReadNulTerminated(ref PerfSpanReader reader)
    {
        StringBuilder builder = new StringBuilder();

        while (!reader.AtEnd)
        {
            byte current = reader.ReadUInt8();
            if (current == 0)
            {
                break;
            }

            builder.Append((char)current);
        }

        return builder.ToString();
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.Perf.Tracepoints)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
