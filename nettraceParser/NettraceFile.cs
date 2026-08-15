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

using DotnetInsights.NetTrace.Progress;
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

    // onProgress: this phase's own 0.0-1.0 completion fraction, reported
    // from the block-read loop below - null (the default) for every
    // caller except Program.cs's --json mode, so every other caller (the
    // plain CLI/--dump-fields path, and every test in nettraceParser.Tests)
    // is completely unaffected by this parameter's existence. See
    // Progress/ProgressReporter.cs for how a fraction reported here maps
    // into the overall progress bar.
    public static NettraceFile Read(string filePath, Action<double> onProgress = null)
    {
        long fileLength = new FileInfo(filePath).Length;

        if (fileLength < ExpectedMagic.Length || !MagicMatches(filePath))
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
        int estimatedEventCount = (int)Math.Min(fileLength / EstimatedBytesPerEvent, int.MaxValue);
        file.Events = new List<EventRecord>(estimatedEventCount);
        file.StacksById = new Dictionary<int, long[]>();

        NettraceHeader header = new NettraceHeader();

        // Streamed, not read whole. This used to be File.ReadAllBytes into one
        // byte[] with a MemoryStreamReader over it, which was zero-copy but put
        // a hard 2GB ceiling on the parser: a byte[] cannot hold more than
        // int.MaxValue elements, so a 3.2GB capture threw "The file is too
        // long" before a single event was decoded. Blocks now carry their own
        // buffers instead (see EventBlock.FromStream), so nothing here needs
        // the whole file resident at once and the ceiling is gone.
        //
        // IOStreamStreamReader's own Goto/Fill tracks position with an internal
        // counter that starts at 0 and is NOT the underlying stream's Position,
        // which is why the 8-byte "Nettrace" magic is skipped by asking the
        // READER to Goto(8) rather than by handing it a pre-advanced
        // FileStream - the latter makes the reader seek to the wrong absolute
        // offset the first time it refills. Positions from this reader are
        // therefore absolute file offsets, exactly as the old MemoryStreamReader
        // start=8 convention produced, so block bookkeeping is unchanged.
        int metadataBlockCount = 0;
        int eventBlockCount = 0;
        int skippedBlockCount = 0;

        {
            IOStreamStreamReader reader = new IOStreamStreamReader(filePath, SerializationSettings.Default);
            reader.Goto((StreamLabel)ExpectedMagic.Length);

            using (Deserializer deserializer = new Deserializer(reader, filePath))
            {
                deserializer.RegisterFactory("Trace", () => header);
                deserializer.RegisterFactory("MetadataBlock", () => { ++metadataBlockCount; return new MetadataBlock(file.MetadataById); });
                deserializer.RegisterFactory("EventBlock", () => { ++eventBlockCount; return new EventBlock(file.MetadataById, file.Events, file.StacksById); });
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
                    // Deserializer.Current is already an ABSOLUTE byte offset
                    // into the file (see this method's own comment on the
                    // reader's position convention above), so
                    // this fraction is exact, not estimated - and free to
                    // compute here specifically because this loop already
                    // advances one whole top-level block at a time (there can
                    // be anywhere from a handful to a few hundred for a real
                    // capture), not once per event, so no separate throttle
                    // mask is needed the way the per-event projector loops
                    // need one.
                    onProgress?.Invoke((long)deserializer.Current / (double)fileLength);
                }
            }
        }

        file.MetadataBlockCount = metadataBlockCount;
        file.EventBlockCount = eventBlockCount;
        file.SkippedBlockCount = skippedBlockCount;

        file.Header = header;
        return file;
    }

    // Reads just the leading bytes rather than taking the whole file as an
    // array - the file is no longer resident in memory when this runs, and a
    // 3GB capture could not be handed to this method as a byte[] at all.
    private static bool MagicMatches(string filePath)
    {
        Span<byte> magic = stackalloc byte[8];

        using (FileStream stream = File.OpenRead(filePath))
        {
            int totalRead = 0;

            while (totalRead < magic.Length)
            {
                int read = stream.Read(magic.Slice(totalRead));

                if (read == 0)
                {
                    return false;
                }

                totalRead += read;
            }
        }

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
