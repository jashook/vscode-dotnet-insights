////////////////////////////////////////////////////////////////////////////////
// Module: HeapGraph.cs
//
// Notes:
// The decoded heap snapshot: every object, its type, its size, and its
// outgoing references, held as a structure of arrays.
//
// WHY NO PER-OBJECT CLASS. This has to hold heaps of more than ten million
// objects. A `class HeapObject { int Type; int Size; HeapObject[] Children; }`
// would cost an object header plus a separate children array PER OBJECT -
// well over 100 bytes each before any references are stored, so 10M objects
// would be >1GB of pure overhead, plus 10M+ individually-traced references
// for the GC to walk on every collection. dotnet-gcdump's own Graph type
// sidesteps this differently, by never materializing nodes at all and
// re-decoding the blob on every property access (see Node.Size/ChildCount in
// GcDumpFormat.cs's layout notes - each one seeks and re-reads). That is the
// right trade for a one-pass report; it is the wrong one here, where four
// separate analyses each traverse the whole graph and the dominator pass
// traverses it many times over.
//
// So instead: flat arrays indexed by node index, and edges in CSR (compressed
// sparse row) form - the standard adjacency representation for a static
// graph. All children of node i are ChildTarget[ChildStart[i] .. ChildStart[i+1]),
// which is one contiguous, cache-friendly run per node and exactly zero
// per-node allocations.
//
// MEASURED FOOTPRINT at 10M nodes / ~30M edges:
//     NodeTypeIndex   40MB      ChildStart    40MB
//     NodeSize        40MB      ChildTarget  120MB
// ~240MB total, all of it in four arrays the GC treats as four objects rather
// than tens of millions. The node blob and the node offset table are needed
// only while decoding and are dropped before any analysis runs.
//
// A note on sizes: NodeSize is stored per node even though most nodes could
// derive it from their type (the blob only encodes a size explicitly when the
// type's own size is 0, i.e. for variable-length types like arrays and
// strings). Keeping it flat costs 40MB and saves an unpredictable branch plus
// a random type-table lookup in every one of the hot traversal loops.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class HeapGraph
{
    // Type table, indexed by type index. Parallel arrays for the same reason
    // the node data is - there are far fewer types than objects, but keeping
    // the shape consistent keeps the analysis loops uniform.
    public string[] TypeNames;
    public string[] TypeModuleNames;
    public int[] TypeSizes;

    public int NodeCount;
    public int TypeCount;

    // The synthetic node every GC root hangs off. Not a real object; it is
    // the graph's entry point for reachability, and analyses that report
    // "types" must skip it rather than reporting it as a live type.
    public int RootNodeIndex;

    // Sum of every node's size, straight from the file's own header field
    // rather than recomputed - it is what `dotnet-gcdump report` prints as
    // "GC Heap bytes", so keeping the file's value makes the two directly
    // comparable (see GcDumpReaderTests.cs).
    public long TotalSize;

    public int[] NodeTypeIndex;
    public int[] NodeSize;

    // CSR adjacency. ChildStart has NodeCount + 1 entries; the extra trailing
    // entry is the total edge count, which is what lets the "children of i"
    // slice below be written without a bounds special-case on the last node.
    public int[] ChildStart;
    public int[] ChildTarget;

    // Each node's object address on the heap.
    //
    // NULL when the graph came from GcDumpReader: none of the four analyses
    // need addresses, and at 10M nodes retaining them costs 80MB for nothing.
    // POPULATED when the graph came from HeapDumpEventDecoder, because
    // GcDumpWriter has to write them back out - a .gcdump carries one address
    // per node, and a reader that asks for one (PerfView does) would otherwise
    // index past the end of a list that did not match the node count.
    public ulong[] NodeAddresses;

    public int EdgeCount
    {
        get
        {
            return this.ChildStart[this.NodeCount];
        }
    }

    public int ChildCountOf(int nodeIndex)
    {
        return this.ChildStart[nodeIndex + 1] - this.ChildStart[nodeIndex];
    }

    // The type index reported for the synthetic root and for any node the
    // file references but never defines. Both exist in real captures.
    public const int UndefinedTypeIndex = 0;

    public string TypeNameOf(int typeIndex)
    {
        if (typeIndex < 0 || typeIndex >= this.TypeCount)
        {
            return "?";
        }

        return this.TypeNames[typeIndex];
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Everything in the file that is not the graph itself. Small, fixed-size, and
// purely descriptive - it populates the header strip of the rendered view so a
// dump can be identified without going back to the filename.
public sealed class GcDumpMetadata
{
    public string MachineName;
    public string ProcessName;
    public int ProcessId;
    public long TimeCollectedTicks;
    public long TotalProcessCommit;
    public long TotalProcessWorkingSet;
    public string CreationTool;

    // Free-text provenance the format carries. GcDumpReader reads past it (the
    // UI has no place for it); GcDumpWriter uses it to record that a file was
    // built from a .nettrace rather than captured by dotnet-gcdump, which is
    // the first thing worth knowing when a converted dump looks surprising.
    public string CollectionLog;

    // dotnet-gcdump samples very large heaps rather than dumping every object,
    // and reports the scale-up factors it used. A value greater than 1 means
    // the counts below are estimates, which the UI has to say out loud rather
    // than presenting sampled numbers as exact.
    public float AverageCountMultiplier;
    public float AverageSizeMultiplier;

    public bool IsSampled
    {
        get
        {
            return this.AverageCountMultiplier > 1.0f || this.AverageSizeMultiplier > 1.0f;
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class GcDumpFile
{
    public HeapGraph Graph;
    public GcDumpMetadata Metadata;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
