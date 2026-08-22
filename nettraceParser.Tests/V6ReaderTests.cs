////////////////////////////////////////////////////////////////////////////////
// Module: V6ReaderTests.cs
//
// Notes:
// Covers the NetTrace v6 container reader (nettraceParser/V6/) - the format
// `dotnet-trace collect-linux` writes - by building synthetic v6 streams byte
// by byte and reading them back. Synthetic rather than fixture-based on
// purpose: a real collect-linux capture is hundreds of MB and cannot be
// checked in, and the things most likely to break here are structural (a
// field width, a header-size convention, a delta-decode rule) and are exactly
// what a hand-built stream can pin precisely. The real capture is covered by
// the opt-in fixture test at the bottom of this file.
//
// Two of the tests below are regressions for bugs that actually happened
// while this reader was written, both of which produced plausible-looking
// wrong output rather than an error:
//
//   - EventBlock's HeaderSize INCLUDES its own field while MetadataBlock's
//     EXCLUDES it. Getting that backwards desynchronizes the event stream by
//     two bytes, and because the compressed event header is self-describing,
//     the reader happily decodes garbage into complete-looking events - the
//     first version of this reader produced 11,077,123 events with impossible
//     timestamps instead of the correct 11,274,185.
//
//   - varint is ZIGZAG encoded, not sign-extended.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using DotnetInsights.NetTrace;
using DotnetInsights.NetTrace.V6;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Builds a v6 stream. Mirrors NetTraceFormat.md field for field so a test can
// state what it is writing rather than encoding it inline.
internal sealed class V6StreamBuilder
{
    private readonly List<byte> bytes = new List<byte>();

    public V6StreamBuilder()
    {
        this.bytes.AddRange(Encoding.UTF8.GetBytes("Nettrace"));
        this.WriteUInt32(0);   // Reserved - what distinguishes v6 from v5
        this.WriteUInt32(6);   // MajorVersion
        this.WriteUInt32(0);   // MinorVersion
    }

    public byte[] ToArray()
    {
        return this.bytes.ToArray();
    }

    public string WriteToTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"v6test-{Guid.NewGuid():N}.nettrace");
        File.WriteAllBytes(path, this.ToArray());
        return path;
    }

    public V6StreamBuilder AddBlock(V6BlockKind kind, byte[] payload)
    {
        uint packed = (uint)(payload.Length & 0xFFFFFF) | ((uint)kind << 24);
        this.WriteUInt32(packed);
        this.bytes.AddRange(payload);
        return this;
    }

    public V6StreamBuilder AddEndOfStream()
    {
        return this.AddBlock(V6BlockKind.EndOfStream, Array.Empty<byte>());
    }

    private void WriteUInt32(uint value)
    {
        this.bytes.Add((byte)(value & 0xFF));
        this.bytes.Add((byte)((value >> 8) & 0xFF));
        this.bytes.Add((byte)((value >> 16) & 0xFF));
        this.bytes.Add((byte)((value >> 24) & 0xFF));
    }

    public static byte[] TraceBlock(long syncTimeQpc, long qpcFrequency, int pointerSize, params (string Key, string Value)[] keyValuePairs)
    {
        PayloadWriter writer = new PayloadWriter();
        writer.Int16(2026).Int16(8).Int16(4).Int16(21).Int16(16).Int16(41).Int16(25).Int16(844);
        writer.Int64(syncTimeQpc).Int64(qpcFrequency).Int32(pointerSize).Int32(keyValuePairs.Length);

        foreach ((string Key, string Value) pair in keyValuePairs)
        {
            writer.String(pair.Key).String(pair.Value);
        }

        return writer.ToArray();
    }

    // headerSize here EXCLUDES its own uint16, per NetTraceFormat.md.
    public static byte[] MetadataBlock(params byte[][] rows)
    {
        PayloadWriter writer = new PayloadWriter();
        writer.UInt16(0);

        foreach (byte[] row in rows)
        {
            writer.UInt16((ushort)row.Length).Raw(row);
        }

        return writer.ToArray();
    }

    public static byte[] MetadataRow(int metadataId, string providerName, int eventId, string eventName, int? version = null)
    {
        PayloadWriter writer = new PayloadWriter();
        writer.VarUInt((ulong)metadataId).String(providerName).VarUInt((ulong)eventId).String(eventName);
        writer.UInt16(0);   // FieldDescriptions count

        PayloadWriter optional = new PayloadWriter();

        if (version.HasValue)
        {
            optional.Byte(V6OptionalMetadataKind.Version).Byte((byte)version.Value);
        }

        byte[] optionalBytes = optional.ToArray();
        writer.UInt16((ushort)optionalBytes.Length).Raw(optionalBytes);

        return writer.ToArray();
    }

    public static byte[] ThreadBlock(params (ulong Index, long ThreadId, int ProcessId, string Name)[] threads)
    {
        PayloadWriter writer = new PayloadWriter();

        foreach ((ulong Index, long ThreadId, int ProcessId, string Name) thread in threads)
        {
            PayloadWriter row = new PayloadWriter();
            row.VarUInt(thread.Index);
            row.Byte(V6ThreadInfoKind.OSProcessId).VarUInt((ulong)thread.ProcessId);
            row.Byte(V6ThreadInfoKind.OSThreadId).VarUInt((ulong)thread.ThreadId);

            if (thread.Name != null)
            {
                row.Byte(V6ThreadInfoKind.Name).String(thread.Name);
            }

            byte[] rowBytes = row.ToArray();
            writer.UInt16((ushort)rowBytes.Length).Raw(rowBytes);
        }

        return writer.ToArray();
    }

    public static byte[] StackBlock(int firstId, params long[][] stacks)
    {
        PayloadWriter writer = new PayloadWriter();
        writer.Int32(firstId).Int32(stacks.Length);

        foreach (long[] stack in stacks)
        {
            writer.Int32(stack.Length * 8);

            foreach (long frame in stack)
            {
                writer.Int64(frame);
            }
        }

        return writer.ToArray();
    }

    // headerSize here INCLUDES its own uint16 - the opposite of MetadataBlock,
    // and the whole point of EventBlockHeaderSizeIncludesItsOwnField below.
    public static byte[] EventBlock(bool compressed, params byte[][] eventRows)
    {
        PayloadWriter writer = new PayloadWriter();
        writer.UInt16(20);                          // HeaderSize, including itself
        writer.UInt16((ushort)(compressed ? 1 : 0));
        writer.Int64(0);                            // MinTimestamp (descriptive only)
        writer.Int64(0);                            // MaxTimestamp (descriptive only)

        foreach (byte[] row in eventRows)
        {
            writer.Raw(row);
        }

        return writer.ToArray();
    }

    // One compressed event row. Every field is flag-gated except the timestamp
    // delta; passing null for a field means "reuse the previous event's".
    public static byte[] CompressedEvent(
        uint? metadataId,
        ulong? threadIndex,
        uint? stackId,
        ulong timeStampDelta,
        uint? payloadSize,
        byte[] payload)
    {
        byte flags = 0;

        if (metadataId.HasValue) { flags |= 1; }
        if (threadIndex.HasValue) { flags |= 4; }
        if (stackId.HasValue) { flags |= 8; }
        if (payloadSize.HasValue) { flags |= 128; }

        PayloadWriter writer = new PayloadWriter();
        writer.Byte(flags);

        if (metadataId.HasValue) { writer.VarUInt(metadataId.Value); }
        if (threadIndex.HasValue) { writer.VarUInt(threadIndex.Value); }
        if (stackId.HasValue) { writer.VarUInt(stackId.Value); }

        writer.VarUInt(timeStampDelta);

        if (payloadSize.HasValue) { writer.VarUInt(payloadSize.Value); }

        if (payload != null) { writer.Raw(payload); }

        return writer.ToArray();
    }
}

internal sealed class PayloadWriter
{
    private readonly List<byte> bytes = new List<byte>();

    public byte[] ToArray() => this.bytes.ToArray();

    public PayloadWriter Raw(byte[] value) { this.bytes.AddRange(value); return this; }

    public PayloadWriter Byte(int value) { this.bytes.Add((byte)value); return this; }

    public PayloadWriter Int16(int value)
    {
        this.bytes.Add((byte)(value & 0xFF));
        this.bytes.Add((byte)((value >> 8) & 0xFF));
        return this;
    }

    public PayloadWriter UInt16(ushort value) => this.Int16(value);

    public PayloadWriter Int32(int value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            this.bytes.Add((byte)((value >> shift) & 0xFF));
        }

        return this;
    }

    public PayloadWriter Int64(long value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            this.bytes.Add((byte)((value >> shift) & 0xFF));
        }

        return this;
    }

    public PayloadWriter VarUInt(ulong value)
    {
        while (value >= 0x80)
        {
            this.bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        this.bytes.Add((byte)value);
        return this;
    }

    public PayloadWriter String(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        this.VarUInt((ulong)utf8.Length);
        this.bytes.AddRange(utf8);
        return this;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class V6ReaderTests
{
    private const string ClrProvider = "Microsoft-Windows-DotNETRuntime";

    private static NettraceFile ReadStream(V6StreamBuilder builder)
    {
        string path = builder.WriteToTempFile();

        try
        {
            return NettraceFile.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void V6Reader_DetectsFormatVersionSixFromZeroReservedField()
    {
        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(1000, 1_000_000_000, 8))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Equal(6, file.FormatVersion);
    }

    [Fact]
    public void V6Reader_ReadsTraceBlockHeaderFields()
    {
        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(166548178221731, 1_000_000_000, 8))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Equal(166548178221731, file.Header.SyncTimeQPC);
        Assert.Equal(1_000_000_000, file.Header.QPCFrequency);
        Assert.Equal(8, file.Header.PointerSize);
        Assert.Equal(new DateTime(2026, 8, 21, 16, 41, 25, 844, DateTimeKind.Utc), file.Header.SyncTimeUtc);
    }

    // v6 removed NumberOfProcessors/ProcessId/ExpectedCPUSamplingRate as
    // dedicated Trace fields and re-expressed them as optional key-value pairs.
    [Fact]
    public void V6Reader_MapsTraceKeyValuePairsOntoLegacyHeaderFields()
    {
        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(
                1000,
                1_000_000_000,
                8,
                ("HardwareThreadCount", "64"),
                ("ProcessId", "4242"),
                ("ExpectedCPUSamplingRate", "1000000")))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Equal(64, file.Header.NumberOfProcessors);
        Assert.Equal(4242, file.Header.ProcessId);
        Assert.Equal(1000000, file.Header.ExpectedCPUSamplingRate);
    }

    [Fact]
    public void V6Reader_ResolvesThreadIndexToOsThreadId()
    {
        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(0, 1_000_000_000, 8))
            .AddBlock(V6BlockKind.Metadata, V6StreamBuilder.MetadataBlock(
                V6StreamBuilder.MetadataRow(1, ClrProvider, 1, "Unknown(1)")))
            .AddBlock(V6BlockKind.Thread, V6StreamBuilder.ThreadBlock((5, 24854, 1, "worker")))
            .AddBlock(V6BlockKind.Event, V6StreamBuilder.EventBlock(
                compressed: true,
                V6StreamBuilder.CompressedEvent(1, 5, null, 500, 0, Array.Empty<byte>())))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Single(file.Events);
        Assert.Equal(24854, file.Events[0].ThreadId);
        Assert.True(file.V6Threads.TryGetProcessId(24854, out int processId));
        Assert.Equal(1, processId);
    }

    [Fact]
    public void V6Reader_ResolvesStackIdToStackTableFrames()
    {
        long[] stack = new long[] { 0x7D62130F8072L, unchecked((long)0xFFFFFFFF8C29DFABUL) };

        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(0, 1_000_000_000, 8))
            .AddBlock(V6BlockKind.Metadata, V6StreamBuilder.MetadataBlock(
                V6StreamBuilder.MetadataRow(1, ClrProvider, 1, "Unknown(1)")))
            .AddBlock(V6BlockKind.Thread, V6StreamBuilder.ThreadBlock((1, 100, 1, null)))
            .AddBlock(V6BlockKind.Stack, V6StreamBuilder.StackBlock(7, stack))
            .AddBlock(V6BlockKind.Event, V6StreamBuilder.EventBlock(
                compressed: true,
                V6StreamBuilder.CompressedEvent(1, 1, 7, 500, 0, Array.Empty<byte>())))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Single(file.Events);
        Assert.Equal(stack, file.Stacks.FramesAt(file.Events[0].StackIndex));
    }

    // The timestamp is a DELTA from the previous event in the same block, and
    // a new block restarts from zero - "when starting a new event block assume
    // that the previous event contained every field with a zeroed value". The
    // block's own Min/MaxTimestamp header fields are descriptive and must NOT
    // seed the decoder; seeding them is a bug this project already shipped
    // once on the v5 path, where it doubled every event's timestamp.
    [Fact]
    public void V6Reader_AccumulatesTimestampDeltasWithinABlockAndResetsAcrossBlocks()
    {
        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(0, 1_000_000_000, 8))
            .AddBlock(V6BlockKind.Metadata, V6StreamBuilder.MetadataBlock(
                V6StreamBuilder.MetadataRow(1, ClrProvider, 1, "Unknown(1)")))
            .AddBlock(V6BlockKind.Thread, V6StreamBuilder.ThreadBlock((1, 100, 1, null)))
            .AddBlock(V6BlockKind.Event, V6StreamBuilder.EventBlock(
                compressed: true,
                V6StreamBuilder.CompressedEvent(1, 1, null, 1000, 0, Array.Empty<byte>()),
                V6StreamBuilder.CompressedEvent(null, null, null, 250, null, Array.Empty<byte>()),
                V6StreamBuilder.CompressedEvent(null, null, null, 250, null, Array.Empty<byte>())))
            .AddBlock(V6BlockKind.Event, V6StreamBuilder.EventBlock(
                compressed: true,
                V6StreamBuilder.CompressedEvent(1, 1, null, 4000, 0, Array.Empty<byte>())))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Equal(4, file.Events.Count);
        Assert.Equal(1000, file.Events[0].TimeStampRelativeQPC);
        Assert.Equal(1250, file.Events[1].TimeStampRelativeQPC);
        Assert.Equal(1500, file.Events[2].TimeStampRelativeQPC);
        Assert.Equal(4000, file.Events[3].TimeStampRelativeQPC);
    }

    // Regression: EventBlock's HeaderSize includes its own uint16 while
    // MetadataBlock's excludes it. Reading either with the other's convention
    // shifts the stream by two bytes and silently yields plausible garbage
    // instead of failing, so this asserts the events land exactly where the
    // writer put them.
    [Fact]
    public void V6Reader_EventBlockHeaderSizeIncludesItsOwnField()
    {
        byte[] payload = new byte[] { 0xEB, 0x3F, 0x00, 0x00 };

        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(0, 1_000_000_000, 8))
            .AddBlock(V6BlockKind.Metadata, V6StreamBuilder.MetadataBlock(
                V6StreamBuilder.MetadataRow(3, ClrProvider, 1, "Unknown(1)")))
            .AddBlock(V6BlockKind.Thread, V6StreamBuilder.ThreadBlock((1, 100, 1, null)))
            .AddBlock(V6BlockKind.Event, V6StreamBuilder.EventBlock(
                compressed: true,
                V6StreamBuilder.CompressedEvent(3, 1, null, 77, (uint)payload.Length, payload)))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Single(file.Events);
        Assert.Equal(1, file.Events[0].EventId);
        Assert.Equal(77, file.Events[0].TimeStampRelativeQPC);
        Assert.Equal(payload.Length, file.Events[0].PayloadLength);

        for (int byteIndex = 0; byteIndex < payload.Length; ++byteIndex)
        {
            Assert.Equal(payload[byteIndex], file.Events[0].PayloadBuffer[file.Events[0].PayloadOffset + byteIndex]);
        }
    }

    [Fact]
    public void V6Reader_SkipsUnrecognizedBlockKinds()
    {
        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(0, 1_000_000_000, 8))
            .AddBlock((V6BlockKind)99, new byte[] { 1, 2, 3, 4, 5 })
            .AddBlock(V6BlockKind.Metadata, V6StreamBuilder.MetadataBlock(
                V6StreamBuilder.MetadataRow(1, ClrProvider, 1, "Unknown(1)")))
            .AddBlock(V6BlockKind.Thread, V6StreamBuilder.ThreadBlock((1, 100, 1, null)))
            .AddBlock(V6BlockKind.Event, V6StreamBuilder.EventBlock(
                compressed: true,
                V6StreamBuilder.CompressedEvent(1, 1, null, 10, 0, Array.Empty<byte>())))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Equal(1, file.SkippedBlockCount);
        Assert.Single(file.Events);
    }

    // A capture whose collector was killed has no EndOfStream block. Whatever
    // was decoded before the cut is still valid and must be kept.
    [Fact]
    public void V6Reader_StopsCleanlyOnATruncatedStream()
    {
        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(0, 1_000_000_000, 8))
            .AddBlock(V6BlockKind.Metadata, V6StreamBuilder.MetadataBlock(
                V6StreamBuilder.MetadataRow(1, ClrProvider, 1, "Unknown(1)")))
            .AddBlock(V6BlockKind.Thread, V6StreamBuilder.ThreadBlock((1, 100, 1, null)))
            .AddBlock(V6BlockKind.Event, V6StreamBuilder.EventBlock(
                compressed: true,
                V6StreamBuilder.CompressedEvent(1, 1, null, 10, 0, Array.Empty<byte>())));

        byte[] full = builder.ToArray();
        byte[] truncated = new byte[full.Length - 3];
        Array.Copy(full, truncated, truncated.Length);

        string path = Path.Combine(Path.GetTempPath(), $"v6trunc-{Guid.NewGuid():N}.nettrace");
        File.WriteAllBytes(path, truncated);

        try
        {
            NettraceFile file = NettraceFile.Read(path);
            Assert.Equal(6, file.FormatVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void V6Reader_RejectsAMajorVersionItDoesNotUnderstand()
    {
        List<byte> bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes("Nettrace"));
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes(7u));
        bytes.AddRange(BitConverter.GetBytes(0u));

        string path = Path.Combine(Path.GetTempPath(), $"v7-{Guid.NewGuid():N}.nettrace");
        File.WriteAllBytes(path, bytes.ToArray());

        try
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() => NettraceFile.Read(path));
            Assert.Contains("v7", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A metadata row may declare a Version; a CLR row in a real collect-linux
    // capture does not, and V6ClrEventVersions fills the gap. Both paths are
    // asserted here because the metadata one takes priority.
    [Fact]
    public void V6Reader_PrefersAnExplicitMetadataVersionOverTheInferredOne()
    {
        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(0, 1_000_000_000, 8))
            .AddBlock(V6BlockKind.Metadata, V6StreamBuilder.MetadataBlock(
                V6StreamBuilder.MetadataRow(1, ClrProvider, 1, "Unknown(1)", version: 1)))
            .AddBlock(V6BlockKind.Thread, V6StreamBuilder.ThreadBlock((1, 100, 1, null)))
            .AddBlock(V6BlockKind.Event, V6StreamBuilder.EventBlock(
                compressed: true,
                V6StreamBuilder.CompressedEvent(1, 1, null, 10, 26, new byte[26])))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Single(file.Events);
        Assert.Equal(1, file.Events[0].Version);
    }

    [Fact]
    public void V6Reader_InfersClrEventVersionWhenMetadataDeclaresNone()
    {
        // A 26-byte GCStart payload is V2 - see V6ClrEventVersions.
        V6StreamBuilder builder = new V6StreamBuilder()
            .AddBlock(V6BlockKind.Trace, V6StreamBuilder.TraceBlock(0, 1_000_000_000, 8))
            .AddBlock(V6BlockKind.Metadata, V6StreamBuilder.MetadataBlock(
                V6StreamBuilder.MetadataRow(1, ClrProvider, 1, "Unknown(1)")))
            .AddBlock(V6BlockKind.Thread, V6StreamBuilder.ThreadBlock((1, 100, 1, null)))
            .AddBlock(V6BlockKind.Event, V6StreamBuilder.EventBlock(
                compressed: true,
                V6StreamBuilder.CompressedEvent(1, 1, null, 10, 26, new byte[26])))
            .AddEndOfStream();

        NettraceFile file = ReadStream(builder);

        Assert.Single(file.Events);
        Assert.Equal(2, file.Events[0].Version);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class V6SpanReaderTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(300UL)]
    [InlineData(16363UL)]
    [InlineData(ulong.MaxValue)]
    public void ReadVarUInt64_RoundTripsEveryBoundary(ulong value)
    {
        PayloadWriter writer = new PayloadWriter();
        writer.VarUInt(value);

        V6SpanReader reader = new V6SpanReader(writer.ToArray());

        Assert.Equal(value, reader.ReadVarUInt64());
    }

    // varint is ZIGZAG encoded per NetTraceFormat.md
    // ("result = (value >> 1) ^ (-(value & 1))"). Decoding it as a plain
    // varuint and casting gives a wrong answer for every negative value.
    [Theory]
    [InlineData(0L, 0UL)]
    [InlineData(-1L, 1UL)]
    [InlineData(1L, 2UL)]
    [InlineData(-2L, 3UL)]
    [InlineData(2L, 4UL)]
    public void ReadVarInt64_DecodesZigZag(long expected, ulong encoded)
    {
        PayloadWriter writer = new PayloadWriter();
        writer.VarUInt(encoded);

        V6SpanReader reader = new V6SpanReader(writer.ToArray());

        Assert.Equal(expected, reader.ReadVarInt64());
    }

    [Fact]
    public void ReadStringBytes_ReadsVarUIntLengthPrefixedUtf8()
    {
        PayloadWriter writer = new PayloadWriter();
        writer.String("Universal.Events");

        V6SpanReader reader = new V6SpanReader(writer.ToArray());

        Assert.Equal("Universal.Events", Encoding.UTF8.GetString(reader.ReadStringBytes()));
    }

    // A block's declared size comes from the file, so a truncated or corrupt
    // capture has to fail as a catchable error rather than read into whatever
    // bytes follow.
    [Fact]
    public void Reads_ThrowRatherThanRunPastTheEndOfTheBlock()
    {
        // Not Assert.Throws: V6SpanReader is a ref struct and cannot be
        // captured by the lambda that overload needs.
        bool threw = false;

        try
        {
            V6SpanReader reader = new V6SpanReader(new byte[] { 1, 2 });
            reader.ReadInt64();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.True(threw);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
