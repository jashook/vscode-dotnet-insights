////////////////////////////////////////////////////////////////////////////////
// Module: CompressedEventBlobHeader.cs
//
// Notes:
// Decodes one event blob's "Header Compression" format header (NetTraceFormat_v5.md,
// EventPipeEventSource.cs's ReadFromFormatV4). Shared by MetadataBlock and
// EventBlock since a metadata-describing event is just an ordinary event blob
// whose payload happens to describe another event's schema.
//
// Several fields are delta/carry-forward encoded: when their flag bit is
// absent, the value from the previous event blob in the same block applies
// (or, for TimeStamp, the previously read value is not replaced but summed
// with the newly read delta). CompressedEventBlobDecoderState carries that
// running state across calls within one block.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

[Flags]
public enum CompressedHeaderFlags : byte
{
    MetadataId = 0x1,
    CaptureThreadAndSequence = 0x2,
    ThreadId = 0x4,
    StackId = 0x8,
    ActivityId = 0x10,
    RelatedActivityId = 0x20,
    Sorted = 0x40,
    DataLength = 0x80
}

public class CompressedEventBlobDecoderState
{
    public int MetadataId;
    public int SequenceNumber;
    public long CaptureThreadId;
    public int ProcessorNumber;
    public long ThreadId;
    public int StackId;
    public long TimeStamp;
    public Guid ActivityId;
    public Guid RelatedActivityId;
    public int PayloadSize;
}

// A readonly struct, not a class: EventBlock.FromStream constructs one per
// event (14.8M times for a real 5-minute capture) as a short-lived local
// whose fields get copied out immediately - never stored long-term, never
// passed to another method (if a future caller needs to pass this as a
// parameter, note it's ~30 bytes, over the struct-passing convention's
// 16-byte threshold, and should take it by `in`). No call site relies on
// reference semantics.
public readonly struct CompressedEventBlobHeader
{
    public readonly int MetadataId;
    public readonly long ThreadId;
    public readonly int StackId;
    public readonly long TimeStamp;
    public readonly bool IsSorted;
    public readonly int PayloadSize;

    private CompressedEventBlobHeader(int metadataId, long threadId, int stackId, long timeStamp, bool isSorted, int payloadSize)
    {
        this.MetadataId = metadataId;
        this.ThreadId = threadId;
        this.StackId = stackId;
        this.TimeStamp = timeStamp;
        this.IsSorted = isSorted;
        this.PayloadSize = payloadSize;
    }

    public static CompressedEventBlobHeader Read(IStreamReader reader, CompressedEventBlobDecoderState state)
    {
        byte flags = reader.ReadByte();

        if (((CompressedHeaderFlags)flags & CompressedHeaderFlags.MetadataId) != 0)
        {
            state.MetadataId = (int)VarIntReader.ReadVarUInt32(reader);
        }

        if (((CompressedHeaderFlags)flags & CompressedHeaderFlags.CaptureThreadAndSequence) != 0)
        {
            state.SequenceNumber += (int)VarIntReader.ReadVarUInt32(reader) + 1;
            state.CaptureThreadId = (long)VarIntReader.ReadVarUInt64(reader);
            state.ProcessorNumber = (int)VarIntReader.ReadVarUInt32(reader);
        }

        if (((CompressedHeaderFlags)flags & CompressedHeaderFlags.ThreadId) != 0)
        {
            state.ThreadId = (long)VarIntReader.ReadVarUInt64(reader);
        }

        if (((CompressedHeaderFlags)flags & CompressedHeaderFlags.StackId) != 0)
        {
            state.StackId = (int)VarIntReader.ReadVarUInt32(reader);
        }

        ulong timeStampDelta = VarIntReader.ReadVarUInt64(reader);
        state.TimeStamp += (long)timeStampDelta;

        if (((CompressedHeaderFlags)flags & CompressedHeaderFlags.ActivityId) != 0)
        {
            state.ActivityId = ReadGuid(reader);
        }

        if (((CompressedHeaderFlags)flags & CompressedHeaderFlags.RelatedActivityId) != 0)
        {
            state.RelatedActivityId = ReadGuid(reader);
        }

        bool isSorted = ((CompressedHeaderFlags)flags & CompressedHeaderFlags.Sorted) != 0;

        if (((CompressedHeaderFlags)flags & CompressedHeaderFlags.DataLength) != 0)
        {
            state.PayloadSize = (int)VarIntReader.ReadVarUInt32(reader);
        }

        return new CompressedEventBlobHeader(state.MetadataId, state.ThreadId, state.StackId, state.TimeStamp, isSorted, state.PayloadSize);
    }

    private static Guid ReadGuid(IStreamReader reader)
    {
        byte[] guidBytes = new byte[16];
        reader.Read(guidBytes, 0, 16);
        return new Guid(guidBytes);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
