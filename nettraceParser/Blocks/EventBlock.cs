////////////////////////////////////////////////////////////////////////////////
// Module: EventBlock.cs
//
// Notes:
// Decodes an EventBlock: a sequence of compressed-header event blobs, each
// resolved against the shared MetadataId -> EventMetadata dictionary
// (populated by MetadataBlock) and decoded into a generic EventRecord via
// FieldValueReader. Nothing here is specific to any one provider or event -
// that interpretation belongs to consumers like Gc/GcEventProjector.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventBlock : IFastSerializable, IFastSerializableVersion
{
    // NetTraceFormat_v5.md documents EventBlock as Version 2 / MinumumReaderVersion 2.
    public int Version => 2;
    public int MinimumVersionCanRead => 2;
    public int MinimumReaderVersion => 2;

    // Shared across every EventRecord that has no manifest field list to
    // decode - which is every event from the CLR runtime provider (GC,
    // AllocationTick, ...), the only kind this parser actually consumes,
    // since manifest-based providers declare no fields at all (see
    // FromStream's own comment). Never mutated after construction -
    // record.Fields is write-once (see FromStream, the only assigner) - so
    // one shared empty instance is safe. Profiling a real 1GB/14.8M-event
    // capture (nearly all CLR-provider events) showed this allocating a
    // fresh, always-empty Dictionary once per event as one of the largest
    // contributors to parse time.
    private static readonly Dictionary<string, object> EmptyFields = new Dictionary<string, object>();

    // The smallest a real compressed event blob can be: a flags byte plus the
    // timestamp-delta varint, which every blob carries unconditionally (every
    // other field is flag-gated - see CompressedEventBlobHeader.Read). A tail
    // shorter than this cannot be an event under any flag combination, so it
    // is block padding.
    private const int MinimumEventBlobHeaderBytes = 2;

    private readonly Dictionary<int, EventMetadata> metadataById;
    private readonly List<EventRecord> events;
    // Same Dictionary<int, long[]> instance StackBlock.cs writes into,
    // shared across the whole file and updated in place as blocks are read
    // in stream order - looking it up HERE, at event-parse time, rather
    // than deferring to a later pass, is what makes EventRecord.Stack immune
    // to a later StackBlock reusing the same numeric id (see EventRecord.cs's
    // own comment on Stack for why that reuse is real and this matters).
    private readonly Dictionary<int, long[]> stacksById;

    public int EventCount { get; set; }
    public int SkippedEventCount { get; set; }

    public EventBlock(Dictionary<int, EventMetadata> metadataById, List<EventRecord> events, Dictionary<int, long[]> stacksById)
    {
        this.metadataById = metadataById;
        this.events = events;
        this.stacksById = stacksById;
    }

    public void FromStream(Deserializer deserializer)
    {
        int blockSize;
        deserializer.Read(out blockSize);

        NettraceBlockAlignment.SkipPaddingToFourByteAlignment(deserializer);

        long blockContentStart = (long)deserializer.Current;
        long blockContentEnd = blockContentStart + blockSize;

        // This block's bytes, copied out of the file exactly once, and the
        // array every EventRecord below then references with a BLOCK-relative
        // offset.
        //
        // It used to be the whole file's single byte[], with each record
        // holding an absolute offset into it. That capped the parser at 2GB
        // twice over - a byte[] cannot exceed int.MaxValue elements, and the
        // offset was itself an int, so a 3GB capture failed outright in
        // File.ReadAllBytes ("The file is too long") and would have silently
        // wrapped to negative offsets had the read succeeded. Per-block arrays
        // remove both limits: a block is bounded (~100KB), so its own offsets
        // always fit an int no matter how large the capture is.
        //
        // The cost is one bulk copy per BLOCK rather than zero - not per
        // event, which is what the old whole-file design was really protecting
        // (it avoided a per-event copy out of an intermediate buffer, and that
        // property is preserved here: events are still parsed in place out of
        // this array).
        byte[] blockBytes = new byte[blockSize];
        deserializer.Reader.Read(blockBytes, 0, blockSize);

        MemoryStreamReader blockReader = new MemoryStreamReader(blockBytes, 0, blockSize, SerializationSettings.Default);

        short headerSize = blockReader.ReadInt16();
        // Read but intentionally unused beyond advancing the stream: per
        // NetTraceFormat_v5.md, Min/MaxTimestamp are purely descriptive
        // (letting a reader locate blocks of interest without decoding every
        // event inside them) - they do NOT seed the per-block delta decoder.
        // See CompressedEventBlobDecoderState's own doc comment for why this
        // matters and what broke when this code used to (wrongly) seed with
        // MinTimestamp.
        short headerFlags = blockReader.ReadInt16();
        long minTimeStamp = blockReader.ReadInt64();
        long maxTimeStamp = blockReader.ReadInt64();

        // The block header lives INSIDE the block content, so its size is
        // already a block-relative offset.
        blockReader.Goto((StreamLabel)headerSize);

        // Zero-initialized (not seeded from MinTimestamp) - see
        // CompressedEventBlobDecoderState's doc comment.
        CompressedEventBlobDecoderState decoderState = new CompressedEventBlobDecoderState();

        while ((long)blockReader.Current < blockSize)
        {
            // A block's tail can be zero padding rather than another event -
            // measured on a real capture: an 8-byte zero tail on a 101,836-byte
            // block, the last real event ending at 101,828.
            //
            // Padding cannot simply be parsed and discarded, because a zero
            // flags byte does not mean "empty event", it means "reuse EVERY
            // field from the previous event" (see CompressedEventBlobHeader.
            // Read). Decoding 2 bytes of zeros therefore yields a complete,
            // plausible-looking duplicate of the last real event - same
            // metadata id, thread, stack and payload size - which the old
            // whole-file reader appended to the trace as a real event, its
            // payload silently read out of the NEXT block's bytes because one
            // shared array made that a legal offset.
            //
            // The rule that rejects it is the format's own: an event lives
            // entirely inside its own block. Anything whose header or payload
            // would cross blockSize is not an event, so the block is done.
            int eventStart = (int)blockReader.Current;

            if (blockSize - eventStart < MinimumEventBlobHeaderBytes)
            {
                break;
            }

            CompressedEventBlobHeader eventHeader = CompressedEventBlobHeader.Read(blockReader, decoderState);

            int payloadStart = (int)blockReader.Current;
            long payloadEnd = (long)payloadStart + eventHeader.PayloadSize;

            if (payloadEnd > blockSize)
            {
                break;
            }

            EventMetadata metadata;
            if (this.metadataById.TryGetValue(eventHeader.MetadataId, out metadata))
            {
                // Always capture the raw payload - manifest-based providers (the CLR
                // runtime provider, for GC/JIT/... events) declare no field list at
                // all, so there is nothing for FieldValueReader to walk. Consumers
                // for those providers (Gc/GcEventProjector) decode the payload
                // themselves using hardcoded offsets from the CLR ETW manifest.
                // No allocation/copy needed here - blockReader operates directly
                // on blockBytes, so payloadStart is already an offset into it
                // and the record can just reference that array + slice.
                Dictionary<string, object> fields;
                if (metadata.Fields.Count > 0)
                {
                    try
                    {
                        blockReader.Goto((StreamLabel)payloadStart);
                        fields = FieldValueReader.ReadFields(blockReader, metadata.Fields);
                    }
                    catch (Exception)
                    {
                        // A field type we don't decode yet (e.g. an Array-typed field).
                        // The payload slice was already captured above regardless, and
                        // payloadEnd below re-syncs the stream either way.
                        fields = EmptyFields;
                    }
                }
                else
                {
                    fields = EmptyFields;
                }

                // Resolved NOW, against whatever this.stacksById holds at
                // this exact point in the (in-order) parse - see
                // EventRecord.cs's own comment on Stack for why a deferred,
                // look-up-by-id-later approach is wrong (StackId values get
                // reused later in the file).
                long[] stack;
                if (eventHeader.StackId == 0 || !this.stacksById.TryGetValue(eventHeader.StackId, out stack))
                {
                    stack = System.Array.Empty<long>();
                }

                EventRecord record = new EventRecord(
                    metadata.ProviderName,
                    metadata.EventName,
                    metadata.EventId,
                    metadata.Version,
                    eventHeader.TimeStamp,
                    eventHeader.ThreadId,
                    stack,
                    fields,
                    blockBytes,
                    payloadStart,
                    eventHeader.PayloadSize);

                this.events.Add(record);
                ++this.EventCount;
            }
            else
            {
                ++this.SkippedEventCount;
            }

            blockReader.Goto((StreamLabel)(int)payloadEnd);
        }

        // The bulk Read above already left the outer reader at the block's end;
        // this keeps that explicit rather than implied, and re-syncs if a
        // future change makes the read partial.
        deserializer.Reader.Goto((StreamLabel)blockContentEnd);
    }

    public void ToStream(Serializer serializer)
    {
        throw new System.NotImplementedException("nettraceParser is read-only.");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
