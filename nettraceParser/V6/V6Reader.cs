////////////////////////////////////////////////////////////////////////////////
// Module: V6Reader.cs
//
// Notes:
// Reads a NetTrace v6 stream - what `dotnet-trace collect-linux` writes - into
// exactly the same in-memory model the v5 FastSerialization path produces: a
// NettraceHeader, a MetadataId -> EventMetadata dictionary, one
// List<EventRecord>, and a StackTable. Nothing downstream of NettraceFile
// knows which container a capture came out of, which is the whole point: the
// GC/exception/contention/allocation projectors already dispatch on
// record.EventId and already treat CLR payloads as opaque manifest-shaped
// bytes, so they work on a v6 capture unchanged.
//
// The v6 container is much simpler than v5's and this reader is correspondingly
// direct - a flat loop over blocks, each introduced by a 4-byte header
// (uint24 BlockSize, uint8 BlockKind). There is no FastSerialization type
// registry, no per-object version negotiation, no footer byte, and - notably -
// no alignment padding, so a block's declared size is exactly how far to the
// next block header. Unrecognized block kinds are skipped by size, which is
// what makes new block kinds a non-breaking minor-version change.
//
// MEMORY: blocks are read one at a time into their own buffer rather than the
// file being read whole (a byte[] cannot exceed int.MaxValue, and this project
// has already hit that ceiling once on a 3.2GB capture - see NettraceFile.cs).
// EventRecord holds a reference to its own block's buffer for the payload
// slice, exactly as the v5 EventBlock path does, so event block buffers stay
// alive for the process's life and every other block kind's buffer is
// collectable as soon as the block is decoded. Buffers come from
// GC.AllocateUninitializedArray because the next statement overwrites every
// byte from the stream - the same measured win as the v5 path's.
//
// The three id spaces that a v6 event header references - StackId,
// ThreadIndex and LabelListIndex - are ALL recyclable across sequence points.
// Every one of them is therefore resolved EAGERLY here, at the moment the
// event is parsed, never stored as an id for a later pass to look up. This
// project has a scar from getting that wrong for v5 StackIds (see
// EventRecord.cs's comment on StackIndex - a lazy whole-file lookup made
// every event's stack a coin flip, confirmed against TraceEvent), and v6
// gives two more chances to make the same mistake.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.V6 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class V6Reader
{
    // Shared by every EventRecord whose provider has no field list to decode -
    // which is every CLR event and every high-volume Universal.Events event.
    // Never mutated (EventRecord.Fields is write-once), so one instance is
    // safe. The v5 path keeps the same singleton for the same measured
    // reason: allocating an always-empty dictionary per event was one of the
    // largest contributors to parse time on a 14.8M-event capture.
    private static readonly Dictionary<string, object> EmptyFields = new Dictionary<string, object>();

    private static readonly List<FieldDefinition> EmptyFieldDefinitions = new List<FieldDefinition>();

    // Read granularity for the small fixed-size reads (stream header, block
    // headers). Block payloads are read straight into their own buffer.
    private const int FileStreamBufferBytes = 4 * 1024 * 1024;

    private readonly V6Utf8StringPool stringPool = new V6Utf8StringPool();
    private readonly V6ThreadTable threadTable = new V6ThreadTable();
    private readonly V6LabelListTable labelTable = new V6LabelListTable();

    private readonly Dictionary<int, EventMetadata> metadataById;
    private readonly List<EventRecord> events;
    private readonly Dictionary<int, int> stackIndexById;
    private readonly StackTable stackTable;

    private NettraceHeader header = new NettraceHeader();
    private int pointerSize = 8;

    // Reused across every StackBlock so the common case - a stack whose
    // content already exists in the StackTable - allocates nothing. Grown on
    // demand; see StackTable.GetOrAdd, which takes a span for this reason.
    private long[] frameScratch = new long[256];

    public int MetadataBlockCount { get; private set; }

    public int EventBlockCount { get; private set; }

    public int SkippedBlockCount { get; private set; }

    public int StackBlockCount { get; private set; }

    public int SequencePointCount { get; private set; }

    // Blocks of a KNOWN kind that failed to decode - distinct from
    // SkippedBlockCount, which counts blocks of an unrecognized kind (routine,
    // and how the format adds block kinds compatibly). Non-zero means this
    // capture is damaged or this reader has a bug; either way the rest of the
    // file was still read.
    public int MalformedBlockCount { get; private set; }

    public string FirstMalformedBlockError { get; private set; }

    // The event id THIS capture assigned to Universal.Events/cpu, or -1 if it
    // has none. Universal event ids are per-capture (UniversalProviders.md
    // promises stable names, not stable ids), so this is discovered while
    // reading metadata rather than assumed - it exists only so a caller
    // holding an EventOverview, which is keyed by (provider, event id), can
    // ask it for this capture's exact CPU sample count to presize with.
    public int UniversalCpuEventId { get; private set; } = -1;

    public V6ThreadTable Threads => this.threadTable;

    public V6LabelListTable Labels => this.labelTable;

    public NettraceHeader Header => this.header;

    public V6Reader(Dictionary<int, EventMetadata> metadataById, List<EventRecord> events, Dictionary<int, int> stackIndexById, StackTable stackTable)
    {
        this.metadataById = metadataById;
        this.events = events;
        this.stackIndexById = stackIndexById;
        this.stackTable = stackTable;

        // Seeded so every projector's `record.ProviderName != Literal` test
        // resolves by reference rather than by content - see
        // V6Utf8StringPool.Seed for the measurement behind this.
        this.stringPool.Seed(V6Format.ClrProviderName);
        this.stringPool.Seed(V6Format.UniversalEventsProviderName);
        this.stringPool.Seed(V6Format.UniversalSystemProviderName);
        this.stringPool.Seed(V6Format.CpuSampleEventName);
        this.stringPool.Seed(V6Format.ContextSwitchEventName);
    }

    public void Read(string filePath, long fileLength, Action<double> onProgress)
    {
        using (FileStream inputStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileStreamBufferBytes))
        {
            byte[] streamHeader = new byte[V6Format.StreamHeaderBytes];
            ReadExactly(inputStream, streamHeader, streamHeader.Length);

            V6SpanReader headerReader = new V6SpanReader(streamHeader);
            headerReader.Skip(V6Format.MagicBytes);

            uint reserved = headerReader.ReadUInt32();
            int majorVersion = headerReader.ReadInt32();
            int minorVersion = headerReader.ReadInt32();

            if (reserved != 0)
            {
                throw new InvalidDataException($"'{filePath}' is not a v6 nettrace stream (Reserved is {reserved}, expected 0).");
            }

            if (majorVersion != V6Format.MajorVersion)
            {
                // A HIGHER major version is a breaking change the format's own
                // rules say a reader must not attempt to continue past.
                throw new InvalidDataException(
                    $"'{filePath}' is nettrace v{majorVersion}.{minorVersion}; this build understands v{V6Format.MajorVersion}. " +
                    "A newer major version is a breaking format change - update nettraceParser.");
            }

            byte[] blockHeader = new byte[V6Format.BlockHeaderBytes];
            long consumedBytes = V6Format.StreamHeaderBytes;

            while (true)
            {
                if (!TryReadExactly(inputStream, blockHeader, blockHeader.Length))
                {
                    // A capture cut short (the collector was killed) has no
                    // EndOfStream block. Everything decoded so far is still
                    // valid, so stop rather than fail.
                    break;
                }

                consumedBytes += blockHeader.Length;

                uint packedHeader = (uint)(blockHeader[0] | (blockHeader[1] << 8) | (blockHeader[2] << 16) | (blockHeader[3] << 24));
                int blockSize = (int)(packedHeader & V6Format.BlockSizeMask);
                V6BlockKind blockKind = (V6BlockKind)(packedHeader >> 24);

                if (blockKind == V6BlockKind.EndOfStream)
                {
                    break;
                }

                byte[] blockBytes = GC.AllocateUninitializedArray<byte>(blockSize);

                if (!TryReadExactly(inputStream, blockBytes, blockSize))
                {
                    break;
                }

                consumedBytes += blockSize;

                // One malformed block must not cost the whole capture. Blocks
                // are independent and self-delimiting - the next block header
                // is found from THIS block's declared size, not from how far
                // decoding got - so a block that fails to decode can be
                // counted and stepped over while everything else in the file
                // still reads. The alternative is losing a 764MB parse to one
                // bad row, which is the wrong trade for a reader whose input
                // can be a capture someone cannot easily retake.
                //
                // Counted separately from SkippedBlockCount: "a block kind
                // this build does not know" is routine and expected (it is how
                // minor versions add blocks), while "a block this build should
                // have understood and could not" is a real signal, and folding
                // them together would hide it.
                try
                {
                    this.DecodeBlock(blockKind, blockBytes, blockSize);
                }
                catch (Exception decodeError) when (decodeError is InvalidOperationException || decodeError is ArgumentException || decodeError is IndexOutOfRangeException)
                {
                    ++this.MalformedBlockCount;

                    if (this.FirstMalformedBlockError == null)
                    {
                        this.FirstMalformedBlockError = $"{blockKind} block at byte {consumedBytes - blockSize}: {decodeError.Message}";
                    }
                }

                onProgress?.Invoke(consumedBytes / (double)fileLength);
            }
        }

        this.header.PointerSize = this.pointerSize;
    }

    private void DecodeBlock(V6BlockKind blockKind, byte[] blockBytes, int blockSize)
    {
        V6SpanReader reader = new V6SpanReader(new ReadOnlySpan<byte>(blockBytes, 0, blockSize));

        switch (blockKind)
        {
            case V6BlockKind.Trace:
            {
                this.ReadTraceBlock(ref reader);
                break;
            }

            case V6BlockKind.Metadata:
            {
                ++this.MetadataBlockCount;
                this.ReadMetadataBlock(ref reader, blockSize);
                break;
            }

            case V6BlockKind.Event:
            {
                ++this.EventBlockCount;
                this.ReadEventBlock(ref reader, blockBytes, blockSize);
                break;
            }

            case V6BlockKind.Stack:
            {
                ++this.StackBlockCount;
                this.ReadStackBlock(ref reader, blockSize);
                break;
            }

            case V6BlockKind.Thread:
            {
                this.ReadThreadBlock(ref reader, blockSize);
                break;
            }

            case V6BlockKind.RemoveThread:
            {
                this.ReadRemoveThreadBlock(ref reader, blockSize);
                break;
            }

            case V6BlockKind.LabelList:
            {
                this.labelTable.ReadBlock(ref reader, blockSize, this.stringPool);
                break;
            }

            case V6BlockKind.SequencePoint:
            {
                ++this.SequencePointCount;
                this.ReadSequencePointBlock(ref reader, blockSize);
                break;
            }

            default:
            {
                // Unrecognized kinds are skipped by size on purpose - that is
                // what lets a minor version add block kinds compatibly.
                ++this.SkippedBlockCount;
                break;
            }
        }
    }

    private void ReadTraceBlock(ref V6SpanReader reader)
    {
        this.header.Year = reader.ReadInt16();
        this.header.Month = reader.ReadInt16();
        this.header.DayOfWeek = reader.ReadInt16();
        this.header.Day = reader.ReadInt16();
        this.header.Hour = reader.ReadInt16();
        this.header.Minute = reader.ReadInt16();
        this.header.Second = reader.ReadInt16();
        this.header.Millisecond = reader.ReadInt16();

        this.header.SyncTimeQPC = reader.ReadInt64();
        this.header.QPCFrequency = reader.ReadInt64();
        this.header.PointerSize = reader.ReadInt32();
        this.pointerSize = this.header.PointerSize;

        int keyValueCount = reader.ReadInt32();

        // v6 removed NumberOfProcessors, ProcessId and ExpectedCPUSamplingRate
        // as dedicated Trace fields and re-expressed them as optional key-value
        // pairs (NetTraceFormat.md, "New TraceBlock Metadata"). Absent keys
        // leave the header's field at 0, which is what a v5 capture that never
        // set them would also produce.
        for (int pairIndex = 0; pairIndex < keyValueCount; ++pairIndex)
        {
            string key = this.stringPool.GetOrAdd(reader.ReadStringBytes());
            string value = this.stringPool.GetOrAdd(reader.ReadStringBytes());

            if (key == V6Format.HardwareThreadCountKey)
            {
                int hardwareThreadCount;

                if (int.TryParse(value, out hardwareThreadCount))
                {
                    this.header.NumberOfProcessors = hardwareThreadCount;
                }
            }
            else if (key == V6Format.ProcessIdKey)
            {
                int processId;

                if (int.TryParse(value, out processId))
                {
                    this.header.ProcessId = processId;
                }
            }
            else if (key == V6Format.ExpectedCpuSamplingRateKey)
            {
                int samplingRate;

                if (int.TryParse(value, out samplingRate))
                {
                    this.header.ExpectedCPUSamplingRate = samplingRate;
                }
            }
        }
    }

    private void ReadMetadataBlock(ref V6SpanReader reader, int blockSize)
    {
        // NOTE the asymmetry with the event block below: a MetadataBlock's
        // HeaderSize EXCLUDES its own field, an EventBlock's INCLUDES it. Both
        // are as documented, and getting it backwards desynchronizes the whole
        // block by two bytes - which does not fail, it silently decodes
        // garbage that looks like plausible events.
        int headerSize = reader.ReadUInt16();
        reader.Skip(headerSize);

        while (reader.Position < blockSize)
        {
            int rowSize = reader.ReadUInt16();
            int rowEnd = reader.Position + rowSize;

            if (rowEnd > blockSize)
            {
                break;
            }

            EventMetadata metadata = new EventMetadata();
            metadata.MetadataId = (int)reader.ReadVarUInt32();
            metadata.ProviderName = this.stringPool.GetOrAdd(reader.ReadStringBytes());
            metadata.EventId = (int)reader.ReadVarUInt32();
            metadata.EventName = this.stringPool.GetOrAdd(reader.ReadStringBytes());
            metadata.Fields = this.ReadFieldDescriptions(ref reader);

            // Version stays 0 - "the capture did not say" - unless the
            // OptionalMetadata below names one. V6ClrEventVersions is what
            // turns that into a usable version for CLR events.
            metadata.Version = 0;
            metadata.Level = 0;
            metadata.Keywords = 0;

            this.ReadOptionalMetadata(ref reader, metadata, rowEnd);

            this.metadataById[metadata.MetadataId] = metadata;

            if (this.UniversalCpuEventId < 0 &&
                metadata.ProviderName == V6Format.UniversalEventsProviderName &&
                metadata.EventName == V6Format.CpuSampleEventName)
            {
                this.UniversalCpuEventId = metadata.EventId;
            }

            reader.Position = rowEnd;
        }
    }

    private List<FieldDefinition> ReadFieldDescriptions(ref V6SpanReader reader)
    {
        int fieldCount = reader.ReadUInt16();

        if (fieldCount == 0)
        {
            return EmptyFieldDefinitions;
        }

        List<FieldDefinition> fields = new List<FieldDefinition>(fieldCount);

        for (int fieldIndex = 0; fieldIndex < fieldCount; ++fieldIndex)
        {
            int fieldSize = reader.ReadUInt16();
            int fieldEnd = reader.Position + fieldSize;

            FieldDefinition field = new FieldDefinition();
            field.Name = this.stringPool.GetOrAdd(reader.ReadStringBytes());
            field.TypeCode = this.ReadFieldType(ref reader, field);

            fields.Add(field);

            reader.Position = fieldEnd;
        }

        return fields;
    }

    // Type is a discriminated union: the element type follows for the array-ish
    // codes, an element count follows for FixedLengthArray, and a whole nested
    // FieldDescriptions block follows for Object.
    private FieldTypeCode ReadFieldType(ref V6SpanReader reader, FieldDefinition field)
    {
        FieldTypeCode typeCode = (FieldTypeCode)reader.ReadUInt8();

        if (typeCode == FieldTypeCode.Array || typeCode == FieldTypeCode.FixedLengthArray ||
            typeCode == FieldTypeCode.RelLoc || typeCode == FieldTypeCode.DataLoc)
        {
            FieldDefinition elementField = new FieldDefinition();
            elementField.Name = string.Empty;
            elementField.TypeCode = this.ReadFieldType(ref reader, elementField);

            if (typeCode == FieldTypeCode.FixedLengthArray)
            {
                reader.ReadUInt16();
            }

            // FieldDefinition has no element-type slot (it was shaped for v5,
            // which had no such types), so the element rides in NestedFields.
            // Only V6FieldValueReader reads it back and it only needs the
            // element's own type code.
            field.NestedFields = new List<FieldDefinition>(1);
            field.NestedFields.Add(elementField);
        }
        else if (typeCode == FieldTypeCode.Object)
        {
            field.NestedFields = this.ReadFieldDescriptions(ref reader);
        }

        return typeCode;
    }

    private void ReadOptionalMetadata(ref V6SpanReader reader, EventMetadata metadata, int rowEnd)
    {
        if (reader.Position + 2 > rowEnd)
        {
            return;
        }

        int optionalSize = reader.ReadUInt16();
        int optionalEnd = reader.Position + optionalSize;

        if (optionalEnd > rowEnd)
        {
            return;
        }

        while (reader.Position < optionalEnd)
        {
            int kind = reader.ReadUInt8();

            switch (kind)
            {
                case V6OptionalMetadataKind.OpCode:
                {
                    reader.ReadUInt8();
                    break;
                }

                case V6OptionalMetadataKind.Keyword:
                {
                    metadata.Keywords = (long)reader.ReadUInt64();
                    break;
                }

                case V6OptionalMetadataKind.MessageTemplate:
                case V6OptionalMetadataKind.Description:
                {
                    reader.ReadStringBytes();
                    break;
                }

                case V6OptionalMetadataKind.KeyValue:
                {
                    reader.ReadStringBytes();
                    reader.ReadStringBytes();
                    break;
                }

                case V6OptionalMetadataKind.ProviderGuid:
                {
                    reader.Skip(16);
                    break;
                }

                case V6OptionalMetadataKind.Level:
                {
                    metadata.Level = reader.ReadUInt8();
                    break;
                }

                case V6OptionalMetadataKind.Version:
                {
                    metadata.Version = reader.ReadUInt8();
                    break;
                }

                default:
                {
                    // Unknown kinds carry no length, so nothing after this
                    // point in the optional section can be located. The row's
                    // own Size still bounds it, so only this row's remaining
                    // optional properties are lost.
                    return;
                }
            }
        }
    }

    private void ReadStackBlock(ref V6SpanReader reader, int blockSize)
    {
        int firstId = (int)reader.ReadUInt32();
        int stackCount = (int)reader.ReadUInt32();

        for (int stackOffset = 0; stackOffset < stackCount; ++stackOffset)
        {
            if (reader.Position + 4 > blockSize)
            {
                break;
            }

            int stackByteCount = (int)reader.ReadUInt32();

            if (reader.Position + stackByteCount > blockSize)
            {
                break;
            }

            ReadOnlySpan<byte> stackBytes = reader.ReadBytes(stackByteCount);
            int frameCount = stackByteCount / this.pointerSize;

            if (this.frameScratch.Length < frameCount)
            {
                this.frameScratch = new long[Math.Max(frameCount, this.frameScratch.Length * 2)];
            }

            DecodeInstructionPointers(stackBytes, this.pointerSize, frameCount, this.frameScratch);

            this.stackIndexById[firstId + stackOffset] = this.stackTable.GetOrAdd(new ReadOnlySpan<long>(this.frameScratch, 0, frameCount));
        }
    }

    private static void DecodeInstructionPointers(ReadOnlySpan<byte> stackBytes, int pointerSize, int frameCount, long[] instructionPointers)
    {
        for (int frameIndex = 0; frameIndex < frameCount; ++frameIndex)
        {
            int offset = frameIndex * pointerSize;

            if (pointerSize == 4)
            {
                instructionPointers[frameIndex] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(stackBytes.Slice(offset));
            }
            else
            {
                instructionPointers[frameIndex] = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(stackBytes.Slice(offset));
            }
        }
    }

    private void ReadThreadBlock(ref V6SpanReader reader, int blockSize)
    {
        while (reader.Position < blockSize)
        {
            int rowSize = reader.ReadUInt16();
            int rowEnd = reader.Position + rowSize;

            if (rowEnd > blockSize)
            {
                break;
            }

            ulong threadIndex = reader.ReadVarUInt64();

            string name = null;
            long osThreadId = 0;
            int osProcessId = 0;

            while (reader.Position < rowEnd)
            {
                int kind = reader.ReadUInt8();

                if (kind == V6ThreadInfoKind.Name)
                {
                    name = this.stringPool.GetOrAdd(reader.ReadStringBytes());
                }
                else if (kind == V6ThreadInfoKind.OSProcessId)
                {
                    osProcessId = (int)reader.ReadVarUInt64();
                }
                else if (kind == V6ThreadInfoKind.OSThreadId)
                {
                    osThreadId = (long)reader.ReadVarUInt64();
                }
                else if (kind == V6ThreadInfoKind.KeyValue)
                {
                    reader.ReadStringBytes();
                    reader.ReadStringBytes();
                }
                else
                {
                    // Unknown info kind: no length to skip by, so this row is
                    // done. The row's own Size still bounds it.
                    break;
                }
            }

            this.threadTable.Define(threadIndex, osThreadId, osProcessId, name);

            reader.Position = rowEnd;
        }
    }

    private void ReadRemoveThreadBlock(ref V6SpanReader reader, int blockSize)
    {
        while (reader.Position < blockSize)
        {
            ulong threadIndex = reader.ReadVarUInt64();
            reader.ReadVarUInt32();

            this.threadTable.Remove(threadIndex);
        }
    }

    private void ReadSequencePointBlock(ref V6SpanReader reader, int blockSize)
    {
        reader.ReadUInt64();
        uint flags = reader.ReadUInt32();
        uint threadCount = reader.ReadUInt32();

        for (uint threadOffset = 0; threadOffset < threadCount; ++threadOffset)
        {
            if (reader.AtEnd)
            {
                break;
            }

            reader.ReadVarUInt64();
            reader.ReadVarUInt32();
        }

        // Flags & 1 flushes the thread cache, Flags & 2 the metadata cache
        // (v6 added the latter so a writer can tell a memory-constrained
        // reader when it is safe to forget metadata ids).
        if ((flags & 1) != 0)
        {
            this.threadTable.FlushIndices();
        }

        if ((flags & 2) != 0)
        {
            this.metadataById.Clear();
        }

        // Label list indices never survive a sequence point. Stack ids are
        // deliberately NOT flushed here, matching the v5 path: every stack
        // reference is resolved eagerly at event-parse time, so a recycled id
        // resolves against the most recent definition, which is the correct
        // answer - whereas flushing would turn a writer that references a
        // stack slightly early into silently missing stacks.
        this.labelTable.Flush();
    }

    private void ReadEventBlock(ref V6SpanReader reader, byte[] blockBytes, int blockSize)
    {
        // Unlike the metadata block's, this HeaderSize INCLUDES itself.
        int headerSize = reader.ReadUInt16();
        int headerFlags = reader.ReadUInt16();

        // Min/Max timestamps are purely descriptive - they let a reader locate
        // blocks of interest without decoding them. They must NOT seed the
        // delta decoder: doing exactly that was a real long-standing bug on
        // the v5 path that doubled every event's timestamp (see
        // Blocks/CompressedEventBlobHeader.cs).
        reader.ReadUInt64();
        reader.ReadUInt64();

        reader.Position = headerSize;

        bool headerCompressed = (headerFlags & 1) != 0;

        V6EventHeaderState state = new V6EventHeaderState();

        while (reader.Position < blockSize)
        {
            int eventStart = reader.Position;

            if (blockSize - eventStart < 2)
            {
                break;
            }

            bool decoded;

            if (headerCompressed)
            {
                decoded = TryReadCompressedHeader(ref reader, ref state);
            }
            else
            {
                decoded = TryReadUncompressedHeader(ref reader, ref state);
            }

            if (!decoded)
            {
                break;
            }

            int payloadStart = reader.Position;
            long payloadEnd = (long)payloadStart + state.PayloadSize;

            // An event lives entirely inside its own block. A header or
            // payload that would cross the block end is block padding, not an
            // event - the v5 path learned this the hard way, where a 2-byte
            // zero tail decoded into a complete, plausible duplicate of the
            // previous event.
            if (payloadEnd > blockSize)
            {
                break;
            }

            this.AddEvent(ref state, blockBytes, payloadStart);

            reader.Position = (int)payloadEnd;
        }
    }

    private void AddEvent(ref V6EventHeaderState state, byte[] blockBytes, int payloadStart)
    {
        EventMetadata metadata;

        if (!this.metadataById.TryGetValue((int)state.MetadataId, out metadata))
        {
            return;
        }

        // Resolved NOW against the tables as they stand at this point in the
        // in-order parse. See this file's header for why none of these may be
        // deferred.
        int stackIndex;

        if (state.StackId == 0 || !this.stackIndexById.TryGetValue((int)state.StackId, out stackIndex))
        {
            stackIndex = StackTable.EmptyStackIndex;
        }

        long threadId = 0;
        V6ThreadTable.ThreadEntry threadEntry;

        if (this.threadTable.TryResolve(state.ThreadIndex, out threadEntry))
        {
            threadId = threadEntry.ThreadId;
        }

        int labelVersion = -1;

        if (state.LabelListId != 0)
        {
            V6LabelOverrides overrides;

            if (this.labelTable.TryGetOverrides(state.LabelListId, out overrides))
            {
                labelVersion = overrides.Version;
            }
        }

        int version = metadata.Version;

        if (metadata.ProviderName == V6Format.ClrProviderName)
        {
            version = V6ClrEventVersions.Resolve(metadata.EventId, (int)state.PayloadSize, metadata.Version, labelVersion);
        }
        else if (labelVersion >= 0)
        {
            version = labelVersion;
        }

        Dictionary<string, object> fields = EmptyFields;

        // Only Universal.System is field-decoded here. Its events are the
        // low-volume state events (process create/exit, module mappings,
        // symbols) that exist purely to be read as named fields, and there are
        // ~11K of them on the reference capture. Everything else is either
        // manifest-shaped (the CLR provider, decoded from raw bytes by the
        // projectors using hardcoded offsets) or high-volume - Universal.Events
        // 'cpu' alone is 1.09M events on that same capture, and giving each
        // one a Dictionary would repeat a cost the v5 path already measured
        // and removed.
        if (metadata.Fields.Count > 0 && metadata.ProviderName == V6Format.UniversalSystemProviderName)
        {
            fields = V6FieldValueReader.ReadFields(
                new ReadOnlySpan<byte>(blockBytes, payloadStart, (int)state.PayloadSize),
                metadata.Fields,
                this.stringPool);
        }

        EventRecord record = new EventRecord(
            metadata.ProviderName,
            metadata.EventName,
            metadata.EventId,
            version,
            (long)state.TimeStamp,
            threadId,
            stackIndex,
            fields,
            blockBytes,
            payloadStart,
            (int)state.PayloadSize);

        this.events.Add(record);
    }

    private static bool TryReadCompressedHeader(ref V6SpanReader reader, ref V6EventHeaderState state)
    {
        byte flags = reader.ReadUInt8();

        if ((flags & 1) != 0)
        {
            state.MetadataId = reader.ReadVarUInt32();
        }

        if ((flags & 2) != 0)
        {
            state.SequenceNumber += reader.ReadVarUInt32();
            state.CaptureThreadIndex = reader.ReadVarUInt64();
            state.ProcessorNumber = reader.ReadVarUInt32();
        }

        ++state.SequenceNumber;

        if ((flags & 4) != 0)
        {
            state.ThreadIndex = reader.ReadVarUInt64();
        }

        if ((flags & 8) != 0)
        {
            state.StackId = reader.ReadVarUInt32();
        }

        state.TimeStamp += reader.ReadVarUInt64();

        if ((flags & 16) != 0)
        {
            state.LabelListId = reader.ReadVarUInt32();
        }

        state.IsSorted = (flags & 64) == 64;

        if ((flags & 128) != 0)
        {
            state.PayloadSize = reader.ReadVarUInt32();
        }

        return true;
    }

    private static bool TryReadUncompressedHeader(ref V6SpanReader reader, ref V6EventHeaderState state)
    {
        if (reader.Remaining < 48)
        {
            return false;
        }

        reader.ReadUInt32();

        uint metadataIdAndSortedFlag = reader.ReadUInt32();
        state.MetadataId = metadataIdAndSortedFlag & 0x7FFFFFFF;
        state.IsSorted = (metadataIdAndSortedFlag & 0x80000000) != 0;

        state.SequenceNumber = reader.ReadUInt32();
        state.ThreadIndex = reader.ReadUInt64();
        state.CaptureThreadIndex = reader.ReadUInt64();
        state.ProcessorNumber = reader.ReadUInt32();
        state.StackId = reader.ReadUInt32();
        state.TimeStamp = reader.ReadUInt64();
        state.LabelListId = reader.ReadUInt32();
        state.PayloadSize = reader.ReadUInt32();

        return true;
    }

    private static void ReadExactly(FileStream stream, byte[] buffer, int count)
    {
        if (!TryReadExactly(stream, buffer, count))
        {
            throw new EndOfStreamException("Unexpected end of nettrace stream.");
        }
    }

    private static bool TryReadExactly(FileStream stream, byte[] buffer, int count)
    {
        int totalRead = 0;

        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);

            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }
}

// Carries the previous event's header fields across one block, which is what
// the compressed encoding's delta/omit scheme is defined against. Zeroed per
// block on purpose: "when starting a new event block assume that the previous
// event contained every field with a zeroed value."
public struct V6EventHeaderState
{
    public uint MetadataId;
    public uint SequenceNumber;
    public ulong CaptureThreadIndex;
    public uint ProcessorNumber;
    public ulong ThreadIndex;
    public uint StackId;
    public ulong TimeStamp;
    public uint LabelListId;
    public uint PayloadSize;
    public bool IsSorted;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.V6)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
