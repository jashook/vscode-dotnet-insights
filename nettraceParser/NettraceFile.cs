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
        file.Events = new List<EventRecord>();

        NettraceHeader header = new NettraceHeader();

        // Deserializer/IOStreamStreamReader track their own logical position starting
        // at 0 and seek the underlying stream from there - they have no notion of a
        // pre-existing offset. So rather than handing them a stream we've already
        // advanced past the nettrace-specific 8-byte magic, we hand them a fresh
        // stream whose position 0 already IS file offset 8 (the true start of the
        // generic FastSerialization content).
        int metadataBlockCount = 0;
        int eventBlockCount = 0;
        int skippedBlockCount = 0;

        using (MemoryStream contentStream = new MemoryStream(fileBytes, ExpectedMagic.Length, fileBytes.Length - ExpectedMagic.Length, writable: false))
        {
            using (Deserializer deserializer = new Deserializer(contentStream, filePath, SerializationSettings.Default))
            {
                deserializer.RegisterFactory("Trace", () => header);
                deserializer.RegisterFactory("MetadataBlock", () => { ++metadataBlockCount; return new MetadataBlock(file.MetadataById); });
                deserializer.RegisterFactory("EventBlock", () => { ++eventBlockCount; return new EventBlock(file.MetadataById, file.Events); });
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
