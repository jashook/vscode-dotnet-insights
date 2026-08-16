////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpFormat.cs
//
// Notes:
// The `.gcdump` (GC heap snapshot) on-disk format, as this directory's reader
// understands it. This file is documentation plus the handful of constants
// that documentation refers to - GcDumpReader.cs is the implementation.
//
// WHAT A .gcdump IS. The output of `dotnet-gcdump collect -p <pid>`: a
// point-in-time snapshot of every object on the managed heap, its type, its
// size, and its outgoing references. Unlike a `.nettrace` (a stream of events
// over time) it answers "what is on the heap right now, and what is keeping
// it alive."
//
// A .gcdump is a `!FastSerialization.1` stream - the SAME serializer this
// repo already vendors for .nettrace in FastSerialization.cs. That is why
// this lives inside nettraceParser rather than in a tool of its own: the
// deserializer, the progress reporter, the binary output container and the
// packaging are all already here.
//
// Unlike .nettrace there is NO 8-byte "Nettrace" magic prefix to skip - the
// FastSerialization signature is at offset 0, so a Deserializer can be
// pointed straight at the file (contrast NettraceFile.cs, which has to slice
// past the magic first; see CLAUDE.md's "Stream positioning" note).
//
// LAYOUT. Entry object `GCHeapDump` (Version 10, MinimumVersionCanRead 4,
// MinimumReaderVersion 8), whose first field is a `Graphs.MemoryGraph`
// (Version 1, MinimumReaderVersion 0).
//
//   GCHeapDump:
//     MemoryGraph  m_graph          (nested object - see below)
//     bool         (ignored)        was Is64Bit, kept for compatibility
//     float        AverageCountMultiplier
//     float        AverageSizeMultiplier
//     object       JSHeapInfo       (writes no payload of its own)
//     object       DotNetHeapInfo   (segment/generation bounds)
//     string       CollectionLog
//     int64        TimeCollected.Ticks
//     string       MachineName
//     string       ProcessName
//     int32        ProcessID
//     int64        TotalProcessCommit
//     int64        TotalProcessWorkingSet
//     int32        countMultipliersByType, then that many floats
//     tagged       InteropInfo, CreationTool
//
//   MemoryGraph = Graph's fields, then its own:
//     int64        totalSize            (sum of every node's size)
//     int32        rootNodeIndex
//     int32        typeCount
//     typeCount x { string Name; int32 Size; string ModuleName }
//     int32        nodeCount
//     nodeCount x  int32                (each node's byte offset into the blob)
//     int32        blobLength, then that many raw bytes  <- the node blob
//     [deferred region, only when MinimumReaderVersionBeingRead >= 1]
//     int32        addressCount
//     addressCount x int64              (each node's object address)
//     tagged       bool Is64Bit
//
// A null string is written as length -1 (that is how a type with no module
// name round-trips).
//
// NODE BLOB. Each node's record starts at its own offset from the node offset
// table and is a run of variable-length ints (ReadCompressedInt below):
//
//     typeAndSize    - low bit set means an explicit size follows;
//                      otherwise the size comes from the type table and
//                      the type index is typeAndSize >> 1
//     [size]         - present only when that low bit was set
//     childCount
//     childCount x   - each child's node index, encoded as a DELTA from the
//                      OWNING node's own index (so back-references and
//                      near-neighbours cost one byte)
//
// VERIFIED, not assumed. The layout above was first derived by hand-decoding
// a real capture and confirmed against `dotnet-gcdump report` on that same
// file (its `totalSize` field read 712,205, matching the tool's own
// "712,205 GC Heap bytes" exactly), then cross-checked line-by-line against
// dotnet/diagnostics' own Graph.cs/GCHeapDump.cs - the code that WROTE the
// file. GcDumpReaderTests.cs keeps that agreement honest.
//
// THE "VERY LARGE GRAPH" TRAP. Graph.cs carries an `m_isVeryLargeGraph` flag
// that widens nodeCount/addressCount to int64 and stream labels to 8 bytes.
// It is NOT stored anywhere in the file - it is a constructor argument. And
// dotnet-gcdump's own reader always constructs `new MemoryGraph(1)` (flag
// false) with `StreamLabelWidth.FourBytes` hardcoded, while nothing anywhere
// in dotnet-gcdump ever constructs one with the flag true. So for any file
// dotnet-gcdump actually produces the flag is dead: the narrow layout above
// is the only layout, and a reader that "supported" both would have no way
// to tell them apart anyway.
//
// This is deliberately NOT a limit on object count. nodeCount is an int32, so
// the format itself tops out somewhere north of two billion objects; what
// bounds a real capture first is blobLength, also an int32, i.e. a 2GB node
// blob. At the ~10-25 bytes a typical node's record occupies that still
// leaves room for well over 100 million objects - far past the 10 million
// this reader is built for. See HeapGraph.cs for where the real cost lands
// (in-memory arrays, not the file).
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpFormat
{
    // Type names exactly as they appear in the file's own SerializationType
    // records - the Deserializer matches factories on these strings, so they
    // are wire values and not just documentation.
    public const string GcHeapDumpTypeName = "GCHeapDump";
    public const string MemoryGraphTypeName = "Graphs.MemoryGraph";
    public const string JsHeapInfoTypeName = "JSHeapInfo";
    public const string DotNetHeapInfoTypeName = "DotNetHeapInfo";
    public const string GcHeapDumpSegmentTypeName = "GCHeapDumpSegment";
    public const string InteropInfoTypeName = "InteropInfo";
    public const string ModuleTypeName = "Module";

    // GCHeapDump's own versioning triple, mirrored from dotnet-gcdump's
    // GCHeapDump.cs. These have to be reported back to the Deserializer
    // through IFastSerializableVersion or its compatibility check fails with
    // "App is version 0" (see CLAUDE.md's "Version compatibility" note - the
    // same trap the .nettrace side already hit).
    public const int GcHeapDumpVersion = 10;
    public const int GcHeapDumpMinimumVersionCanRead = 4;
    public const int GcHeapDumpMinimumReaderVersion = 8;

    // MemoryGraph's. MinimumReaderVersion 0 is load-bearing rather than
    // incidental: Graph.ToStream only emits its trailing deferred region when
    // MinimumReaderVersionBeingRead >= 1, so for every real dotnet-gcdump
    // file there is no region there to skip.
    public const int MemoryGraphVersion = 1;
    public const int MemoryGraphMinimumVersionCanRead = 0;
    public const int MemoryGraphMinimumReaderVersion = 0;

    // GCHeapDumpSegment gained Gen4End (the pinned object heap) in its
    // version 1; a version 0 record simply stops after Gen3End.
    public const int GcHeapDumpSegmentVersion = 1;

    // .gcdump is written with 4-byte stream labels, unconditionally - see
    // "THE VERY LARGE GRAPH TRAP" above. The vendored SerializationSettings.
    // Default is EightBytes (what .nettrace wants), so this override is
    // required rather than optional: getting it wrong misreads every object
    // reference in the file rather than failing loudly.
    public static SerializationSettings ReaderSettings
    {
        get
        {
            return SerializationSettings.Default.WithStreamLabelWidth(StreamLabelWidth.FourBytes);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
