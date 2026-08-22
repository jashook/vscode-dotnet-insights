////////////////////////////////////////////////////////////////////////////////
// Module: V6LabelListTable.cs
//
// Notes:
// v6 replaces the v5 event header's fixed ActivityId/RelatedActivityId fields
// with a LabelListIndex into a LabelListBlock (NetTraceFormat.md, "Extended
// support for event labels"). A label list is an open-ended bag of key-value
// pairs, and four of the label kinds - OpCode, Keywords, Level and Version -
// are documented to OVERRIDE whatever the event's metadata row says.
//
// The Version override is the one that has to work. v6 metadata makes almost
// every property optional, and a real `dotnet-trace collect-linux` capture
// exercises that: its CLR metadata rows carry ONLY a ProviderGuid - no
// Version, no Level, no Keywords, no field descriptions. Since this project's
// CLR payload decoders are version-gated (ExceptionThrown needs Version >= 1,
// GCAllocationTick >= 2, and GCEnd/GCSuspendEEBegin pick a different field
// width below version 1), taking metadata at face value there would silently
// drop every exception in the capture and mis-decode the GC events. See
// V6ClrEventVersions for how the remaining gap is closed once this table has
// had its say.
//
// WHY THIS DOES NOT MATERIALIZE STRINGS: on the reference capture the
// LabelList blocks are 305MB of a 764MB file - 10,172,583 label entries
// against 11,274,185 events - and 10,171,358 of them are one repeated
// string pair, `Error` = "Expected actual values", which the writer attaches
// to every event whose field layout it could not describe. None of that is an
// override, so this walks the strings and skips their bytes rather than
// decoding them, and only records an entry when a label list actually carries
// an override. On that capture the resulting table holds 640 entries instead
// of 10.2 million.
//
// The `Error` labels are still COUNTED (by comparing raw UTF-8 bytes, no
// allocation), because "40% of your file is a writer-side error annotation"
// is worth being able to say out loud rather than silently discarding.
//
// Like stacks and thread indices, a label list index is only valid until the
// next SequencePointBlock, so this table is flushed there and every event
// resolves its own index eagerly at parse time.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.V6 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Absent values are -1 rather than 0 because 0 is a legal OpCode, Level and
// Version, so a "was it present" answer cannot be encoded as a default.
public struct V6LabelOverrides
{
    public int Version;
    public int OpCode;
    public int Level;
    public long Keywords;

    public static V6LabelOverrides None
    {
        get
        {
            V6LabelOverrides value = new V6LabelOverrides();
            value.Version = -1;
            value.OpCode = -1;
            value.Level = -1;
            value.Keywords = 0;
            return value;
        }
    }

    public bool HasAny => this.Version >= 0 || this.OpCode >= 0 || this.Level >= 0 || this.Keywords != 0;
}

public sealed class V6LabelListTable
{
    private static readonly byte[] ErrorKeyUtf8 = Encoding.UTF8.GetBytes("Error");

    private readonly Dictionary<uint, V6LabelOverrides> overridesByIndex = new Dictionary<uint, V6LabelOverrides>();

    public long TotalLabelCount { get; private set; }

    public long WriterErrorLabelCount { get; private set; }

    // The first writer-error message seen, decoded once purely so the CLI and
    // the extension can report what the writer actually complained about
    // instead of just a count.
    public string FirstWriterErrorMessage { get; private set; }

    public int OverrideCount => this.overridesByIndex.Count;

    public void Flush()
    {
        this.overridesByIndex.Clear();
    }

    public bool TryGetOverrides(uint labelListIndex, out V6LabelOverrides overrides)
    {
        return this.overridesByIndex.TryGetValue(labelListIndex, out overrides);
    }

    // Decodes one LabelListBlock's payload. The block's own header names the
    // index of its first list; each subsequent list is implicitly the previous
    // index + 1, and a label with the high bit of its Kind byte set is the
    // last label of its list.
    public void ReadBlock(ref V6SpanReader reader, int blockEnd, V6Utf8StringPool stringPool)
    {
        uint firstIndex = reader.ReadUInt32();
        uint listCount = reader.ReadUInt32();

        uint currentIndex = firstIndex;
        V6LabelOverrides current = V6LabelOverrides.None;
        uint listsRead = 0;

        while (reader.Position < blockEnd && listsRead < listCount)
        {
            byte rawKind = reader.ReadUInt8();
            int kind = rawKind & V6LabelKind.KindMask;
            bool isLastInList = (rawKind & V6LabelKind.LastLabelInListFlag) != 0;

            ++this.TotalLabelCount;

            switch (kind)
            {
                case V6LabelKind.ActivityId:
                case V6LabelKind.RelatedActivityId:
                case V6LabelKind.TraceId:
                {
                    reader.Skip(16);
                    break;
                }

                case V6LabelKind.SpanId:
                {
                    reader.Skip(8);
                    break;
                }

                case V6LabelKind.StringKeyValue:
                {
                    ReadOnlySpan<byte> key = reader.ReadStringBytes();
                    ReadOnlySpan<byte> value = reader.ReadStringBytes();

                    if (key.SequenceEqual(ErrorKeyUtf8))
                    {
                        ++this.WriterErrorLabelCount;

                        if (this.FirstWriterErrorMessage == null)
                        {
                            this.FirstWriterErrorMessage = stringPool.GetOrAdd(value);
                        }
                    }

                    break;
                }

                case V6LabelKind.IntKeyValue:
                {
                    reader.ReadStringBytes();
                    reader.ReadVarInt64();
                    break;
                }

                case V6LabelKind.OpCode:
                {
                    current.OpCode = reader.ReadUInt8();
                    break;
                }

                case V6LabelKind.Keywords:
                {
                    current.Keywords = (long)reader.ReadUInt64();
                    break;
                }

                case V6LabelKind.Level:
                {
                    current.Level = reader.ReadUInt8();
                    break;
                }

                case V6LabelKind.Version:
                {
                    current.Version = reader.ReadUInt8();
                    break;
                }

                default:
                {
                    // An unrecognized label kind has no declared size, so the
                    // rest of this block cannot be walked. Blocks are
                    // independent, so abandoning this one loses only its own
                    // labels - every event still resolves, just without
                    // overrides it might have carried.
                    return;
                }
            }

            if (isLastInList)
            {
                if (current.HasAny)
                {
                    this.overridesByIndex[currentIndex] = current;
                }

                current = V6LabelOverrides.None;
                ++currentIndex;
                ++listsRead;
            }
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.V6)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
