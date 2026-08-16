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

    // See the block comment at its use site in Read for the measurements behind
    // this value. Must stay a multiple of the reader's 8-byte alignment.
    private const int ReadBufferBytes = 4 * 1024 * 1024;

    public NettraceHeader Header { get; private set; }
    public Dictionary<int, EventMetadata> MetadataById { get; private set; }
    public List<EventRecord> Events { get; private set; }
    // StackId -> raw pointer-sized instruction pointers, leaf frame first
    // (see Blocks/StackBlock.cs). Populated regardless of whether anything
    // in a given trace references stacks - empty, not null, when no
    // StackBlock objects are present.
    // StackId -> stack index (see StackTable): ids recycle across sequence
    // points, indices do not.
    public Dictionary<int, int> StackIndexById { get; private set; }

    // Every decoded stack, indexed by the StackIndex an EventRecord carries.
    public StackTable Stacks { get; private set; }
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
        // much larger element than the old 8-byte class reference.
        //
        // This estimate is deliberately LOW - it is meant to over-shoot the
        // event count, not to be accurate - because the two ways of being
        // wrong cost wildly different amounts:
        //
        //   Over-estimating is close to free. The backing array's untouched
        //   tail is never written, so the OS never makes those pages
        //   resident: a 46.1M-capacity list holding 35.1M records measured
        //   the same 2.58GB peak RSS as an exactly-sized one, and .NET does
        //   not eagerly zero a large array either (allocation of a 3.3GB
        //   array measured 0ms - it comes from fresh OS zero pages, so
        //   GC.AllocateUninitializedArray has nothing to save here).
        //
        //   Under-estimating costs a full doubling: a new array twice the
        //   size, a copy of everything so far, and BOTH arrays resident
        //   until the old one is collected.
        //
        // The old value of 70 was measured on one capture and under-shot on
        // two of three real captures, forcing exactly that doubling.
        // Measured bytes/event across them is 40-92 (denser event streams -
        // lots of small CPU samples - sit at the low end), so no single
        // accurate value exists; 32 sits below the whole observed range with
        // margin. Switching 70 -> 38 on those three captures, with
        // byte-identical output on each:
        //
        //     3.01GB capture   8.23GB -> 8.15GB peak RSS, 12430 -> 11274ms
        //      836MB capture   5.09GB -> 4.24GB peak RSS,  6596 ->  6552ms
        //     1.39GB capture   8.85GB -> 6.37GB peak RSS,  9938 ->  8241ms
        //
        // Dropping further to 24 measured no better and no worse, confirming
        // the margin itself is free.
        const int EstimatedBytesPerEvent = 32;

        // The reservation scales with file size, and "free" only holds while
        // the runtime can actually reserve it - so this caps how far the
        // guess can run ahead on a capture far larger than anything measured
        // above (it only binds past ~4GB). Past that, taking the doubling is
        // strictly better than requesting an array that may not be
        // satisfiable at all.
        const int MaxEstimatedEventCount = 128 * 1024 * 1024;

        int estimatedEventCount = (int)Math.Min(fileLength / EstimatedBytesPerEvent, MaxEstimatedEventCount);
        file.Events = new List<EventRecord>(estimatedEventCount);
        file.StackIndexById = new Dictionary<int, int>();
        file.Stacks = new StackTable();

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
            // 4MB read buffer rather than IOStreamStreamReader's own 16KB default
            // (its defaultBufferSize, which the convenience
            // IOStreamStreamReader(string, ...) constructor hardcodes - hence
            // building the FileStream here instead of using it).
            //
            // The reader refills by reading exactly one buffer at a time, so the
            // buffer size sets the syscall count for the whole parse: at 16KB a
            // 3.01GB capture needs ~197,000 read() calls. Measured on that
            // capture (median of 3 alternating runs, byte-identical output at
            // every size):
            //
            //     16KB (default) read=2677ms
            //    256KB           read=2500ms
            //      1MB           read=2168ms
            //      4MB           read=2063ms   <-- chosen
            //
            // ~23% off the read phase for 4MB of buffer, against a process whose
            // peak RSS on this capture is 6.78GB - the buffer does not register.
            // Gains flatten past 4MB, so this is the knee rather than the
            // largest value that still helps.
            //
            // Worth knowing WHY this is the lever and per-read work is not: a
            // dotnet-trace sampled-thread-time profile of this capture attributed
            // ~13% of wall clock to IOStreamStreamReader.Fill plus the
            // Thread.Sleep(0) its refill loop runs, ~95% of it under
            // StackBlock.FromStream's per-entry reads. Rewriting StackBlock to
            // bulk-read its block the way EventBlock does (the obvious reading of
            // that profile) measured as NO improvement at all - 2648ms vs 2644ms
            // - because those samples are I/O wait, not reclaimable CPU. Reducing
            // the NUMBER of reads is what actually moved it.
            FileStream inputStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            IOStreamStreamReader reader = new IOStreamStreamReader(inputStream, SerializationSettings.Default, ReadBufferBytes);
            reader.Goto((StreamLabel)ExpectedMagic.Length);

            using (Deserializer deserializer = new Deserializer(reader, filePath))
            {
                deserializer.RegisterFactory("Trace", () => header);
                deserializer.RegisterFactory("MetadataBlock", () => { ++metadataBlockCount; return new MetadataBlock(file.MetadataById); });
                deserializer.RegisterFactory("EventBlock", () => { ++eventBlockCount; return new EventBlock(file.MetadataById, file.Events, file.StackIndexById); });
                // header.PointerSize is read here (not file.Header.PointerSize,
                // which isn't assigned until after this whole loop finishes) -
                // safe because GetEntryObject() below reads the Trace header
                // (populating `header` in place) before any block factory runs.
                deserializer.RegisterFactory("StackBlock", () => new StackBlock(file.StackIndexById, file.Stacks, header.PointerSize));
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
