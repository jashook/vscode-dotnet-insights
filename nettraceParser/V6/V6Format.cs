////////////////////////////////////////////////////////////////////////////////
// Module: V6Format.cs
//
// Notes:
// Constants and version detection for NetTrace v6 (perfview's
// src/TraceEvent/EventPipe/NetTraceFormat.md), the format `dotnet-trace
// collect-linux` writes.
//
// v6 is a BREAKING change from v5 and the difference is structural, not
// incremental: v5 wraps everything in FastSerialization (per-object type
// names, versions, headers, a footer byte and stream-offset-driven alignment
// padding), and v6 deletes that layer outright in favour of a flat sequence
// of blocks each introduced by a 4-byte header. That is why a v6 file fails
// inside FastSerialization's own Deserializer.Initialize with "Not a
// understood file format" rather than anywhere in this project's code - the
// 'Nettrace' magic matches, and the very next thing v5 expects (the
// "!FastSerialization.1" signature string) simply is not there.
//
// HOW TO TELL THEM APART: the format's own answer (NetTraceFormat.md, "First
// Bytes: NetTrace Magic") is that v6 follows the magic with a uint32 Reserved
// field that is always 0, and "You can distinguish earlier versions either by
// the absence of the 'Nettrace' magic or because the Reserved field is not
// zero." In a v5 file those same 4 bytes are the length prefix of the
// "!FastSerialization.1" signature string - the constant 20 - so the test is
// decisive rather than heuristic, in both directions.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.V6 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.IO;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public enum V6BlockKind
{
    EndOfStream = 0,
    Trace = 1,
    Event = 2,
    Metadata = 3,
    SequencePoint = 4,
    Stack = 5,
    Thread = 6,
    RemoveThread = 7,
    LabelList = 8
}

// Kind discriminants for the OptionalThreadInfo entries inside a ThreadBlock.
public static class V6ThreadInfoKind
{
    public const int Name = 1;
    public const int OSProcessId = 2;
    public const int OSThreadId = 3;
    public const int KeyValue = 4;
}

// Kind discriminants for entries inside a LabelListBlock. The high bit of the
// Kind byte marks the last label of a list, so callers must mask with 0x7F
// before comparing - see V6LabelListTable.
public static class V6LabelKind
{
    public const int ActivityId = 1;
    public const int RelatedActivityId = 2;
    public const int TraceId = 3;
    public const int SpanId = 4;
    public const int StringKeyValue = 5;
    public const int IntKeyValue = 6;
    public const int OpCode = 7;
    public const int Keywords = 8;
    public const int Level = 9;
    public const int Version = 10;

    public const int LastLabelInListFlag = 0x80;
    public const int KindMask = 0x7F;
}

// Kind discriminants for a metadata row's OptionalMetadata section.
public static class V6OptionalMetadataKind
{
    public const int OpCode = 1;
    // 2 is deliberately absent: it was V5's V2Params and is not reused.
    public const int Keyword = 3;
    public const int MessageTemplate = 4;
    public const int Description = 5;
    public const int KeyValue = 6;
    public const int ProviderGuid = 7;
    public const int Level = 8;
    public const int Version = 9;
}

public static class V6Format
{
    // 'Nettrace' (8) + Reserved (4) + MajorVersion (4) + MinorVersion (4).
    public const int StreamHeaderBytes = 20;

    public const int MagicBytes = 8;

    public const int MajorVersion = 6;

    // The BlockHeader is a single little-endian uint32: BlockSize = X &
    // 0xFFFFFF, BlockKind = X >> 24.
    public const int BlockHeaderBytes = 4;

    public const uint BlockSizeMask = 0xFFFFFF;

    // Trace-block key-value keys that carry what used to be dedicated Trace
    // object fields in v5 (NetTraceFormat.md, "New TraceBlock Metadata").
    public const string HardwareThreadCountKey = "HardwareThreadCount";
    public const string ProcessIdKey = "ProcessId";
    public const string ExpectedCpuSamplingRateKey = "ExpectedCPUSamplingRate";

    public const string UniversalEventsProviderName = "Universal.Events";
    public const string UniversalSystemProviderName = "Universal.System";
    public const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";

    // Universal.Events identifies its events by NAME, not id:
    // UniversalProviders.md is explicit that "There are no stable event IDs,
    // but there will be a set of stable names." Matching on the id a
    // particular capture happened to assign would work on one file and
    // silently find nothing on the next.
    public const string CpuSampleEventName = "cpu";
    public const string ContextSwitchEventName = "cswitch";

    // Returns the major version of the stream, or 5 for any file that carries
    // the 'Nettrace' magic but does not use the v6 stream header. Deliberately
    // reports "5" rather than "not v6" so callers read as a version switch;
    // v1-v4 are not distinguished here because this project never supported
    // them and the v5 FastSerialization path reports its own error for them.
    //
    // Throws nothing for a non-Nettrace file - the caller (NettraceFile.Read)
    // has already validated the magic and produces its own error message.
    public static int ReadMajorVersion(string filePath)
    {
        Span<byte> header = stackalloc byte[StreamHeaderBytes];

        using (FileStream stream = File.OpenRead(filePath))
        {
            int totalRead = 0;

            while (totalRead < header.Length)
            {
                int read = stream.Read(header.Slice(totalRead));

                if (read == 0)
                {
                    // Too short to be a v6 stream header at all. A v5 file can
                    // legitimately be this short only if it is truncated, and
                    // the v5 path will say so.
                    return 5;
                }

                totalRead += read;
            }
        }

        uint reserved = (uint)(header[8] | (header[9] << 8) | (header[10] << 16) | (header[11] << 24));

        if (reserved != 0)
        {
            return 5;
        }

        return header[12] | (header[13] << 8) | (header[14] << 16) | (header[15] << 24);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.V6)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
