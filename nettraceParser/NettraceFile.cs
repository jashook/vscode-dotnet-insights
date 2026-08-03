////////////////////////////////////////////////////////////////////////////////
// Module: NettraceFile.cs
//
// Notes:
// Top-level entry point: validates the .nettrace-specific 8-byte magic (which
// sits in front of the generic FastSerialization stream and so isn't
// something Deserializer itself knows about), wires up the Deserializer with
// factories for the block types we decode plus a skip-fallback for anything
// else, and drives the read to completion.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class NettraceFile
{
    private static readonly byte[] ExpectedMagic = Encoding.UTF8.GetBytes("Nettrace");

    public NettraceHeader Header { get; private set; }
    public Dictionary<int, EventMetadata> MetadataById { get; private set; }
    public List<EventRecord> Events { get; private set; }
    // StackId -> raw pointer-sized instruction pointers, leaf frame first
    // (see Blocks/StackBlock.cs). Populated regardless of whether anything
    // in a given trace references stacks - empty, not null, when no
    // StackBlock objects are present.
    public Dictionary<int, long[]> StacksById { get; private set; }
    public int MetadataBlockCount { get; private set; }
    public int EventBlockCount { get; private set; }
    public int SkippedBlockCount { get; private set; }

    public static NettraceFile Read(string filePath)
    {
        byte[] fileBytes = File.ReadAllBytes(filePath);

        if (fileBytes.Length < ExpectedMagic.Length || !MagicMatches(fileBytes))
        {
            throw new InvalidDataException($"'{filePath}' does not start with the expected 'Nettrace' magic bytes.");
        }

        NettraceFile file = new NettraceFile();
        file.MetadataById = new Dictionary<int, EventMetadata>();
        // EventRecord is a struct (~70 bytes) - without a capacity hint this
        // list regrows via doubling as EventBlock.FromStream adds all 14.8M+
        // events for a real 5-minute capture, and each doubling now copies a
        // much larger element than the old 8-byte class reference. ~70
        // bytes/event is this parser's own measured ratio on a real 1GB/
        // 14.8M-event capture (compressed-header event blobs plus payload,
        // averaged across the whole file) - not exact for every capture, but
        // close enough to skip most of the early resizes; a wrong guess still
        // falls back to normal doubling for the remainder.
        const int EstimatedBytesPerEvent = 70;
        int estimatedEventCount = (int)Math.Min(fileBytes.Length / EstimatedBytesPerEvent, int.MaxValue);
        file.Events = new List<EventRecord>(estimatedEventCount);
        file.StacksById = new Dictionary<int, long[]>();

        NettraceHeader header = new NettraceHeader();

        // IOStreamStreamReader (the previous reader here) copies data twice per
        // event: once from fileBytes into its own internal Fill()'d buffer, then
        // again out of that buffer into whatever the caller reads into (e.g.
        // EventBlock.FromStream's per-event payload array). fileBytes is already
        // the entire file resident in memory (File.ReadAllBytes above), so that
        // first copy is pure overhead. MemoryStreamReader instead reads directly
        // out of fileBytes with no intermediate buffer at all.
        //
        // MemoryStreamReader(data, start, length, settings)'s `length` parameter
        // is actually an absolute end-position bound (endPosition = length), not
        // a count relative to `start` - confirmed by the single-array constructor
        // MemoryStreamReader(data, settings) delegating as
        // this(data, 0, data.Length, settings), which only makes sense under that
        // reading. So skipping the 8-byte "Nettrace" magic is start=8 with the
        // bound left at fileBytes.Length (not fileBytes.Length - 8), which also
        // means Current/position values from this reader are already absolute
        // byte offsets into fileBytes - exactly what EventBlock.FromStream needs
        // to record a zero-copy (offset, length) slice for each event's payload
        // instead of allocating and copying a dedicated byte[] per event.
        int metadataBlockCount = 0;
        int eventBlockCount = 0;
        int skippedBlockCount = 0;

        {
            MemoryStreamReader reader = new MemoryStreamReader(fileBytes, ExpectedMagic.Length, fileBytes.Length, SerializationSettings.Default);

            using (Deserializer deserializer = new Deserializer(reader, filePath))
            {
                deserializer.RegisterFactory("Trace", () => header);
                deserializer.RegisterFactory("MetadataBlock", () => { ++metadataBlockCount; return new MetadataBlock(file.MetadataById); });
                deserializer.RegisterFactory("EventBlock", () => { ++eventBlockCount; return new EventBlock(file.MetadataById, file.Events, fileBytes, file.StacksById); });
                // header.PointerSize is read here (not file.Header.PointerSize,
                // which isn't assigned until after this whole loop finishes) -
                // safe because GetEntryObject() below reads the Trace header
                // (populating `header` in place) before any block factory runs.
                deserializer.RegisterFactory("StackBlock", () => new StackBlock(file.StacksById, header.PointerSize));
                deserializer.OnUnregisteredType = (typeName) => (() => { ++skippedBlockCount; return new SkippableBlock(); });

                // GetEntryObject() reads just the Trace header. The Block sequence that
                // follows is a flat run of independent top-level objects, not something
                // reached lazily via object references, so we read it ourselves.
                // ReadObject() is the right primitive for this (not the Deserializer's
                // own allowLazyDeserialization=false eager-read loop, which expects a
                // bare EndObject tag to terminate - a holdover from the older V1
                // "EventPipeFile" container format - whereas .nettrace's real terminator
                // is a NullReference tag, which ReadObject() already handles by
                // returning null instead of throwing).
                deserializer.GetEntryObject();

                while (deserializer.ReadObject() != null)
                {
                }
            }
        }

        file.MetadataBlockCount = metadataBlockCount;
        file.EventBlockCount = eventBlockCount;
        file.SkippedBlockCount = skippedBlockCount;

        file.Header = header;
        return file;
    }

    private static bool MagicMatches(byte[] magic)
    {
        for (int index = 0; index < ExpectedMagic.Length; ++index)
        {
            if (magic[index] != ExpectedMagic[index])
            {
                return false;
            }
        }

        return true;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
