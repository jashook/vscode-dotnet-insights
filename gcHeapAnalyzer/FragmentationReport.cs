////////////////////////////////////////////////////////////////////////////////
// Module: FragmentationReport.cs
//
// Notes:
// Data model for the heap fragmentation analysis produced by HeapAnalyzer.cs.
// Kept as plain data classes (no methods) so ReportJsonExporter.cs owns all
// serialization logic in one place.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.GcHeapAnalyzer {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class FragmentationReport
{
    public int ProcessId;
    public string ProcessName;
    public string CaptureTimeUtc;

    public HeapSummary Summary;
    public GenerationStats[] Generations;
    public FreeChunkReport FreeChunks;
    public List<PinnedTypeStat> PinnedObjects;
    public List<TypeStat> TopLohTypes;
    public List<TypeStat> TopPohTypes;
    public List<SegmentOccupancy> Segments;

    // Address-ordered block map, one entry per Gen2/LOH/POH segment (see
    // SegmentMap) - Gen0/Gen1 segments are skipped since their object counts
    // (constant ephemeral churn) would make this data both huge and
    // uninteresting for the fragmentation investigation this exists for.
    public List<SegmentMap> SegmentMaps;
}

public class HeapSummary
{
    public long TotalCommittedBytes;
    public long TotalObjectBytes;
    public long TotalFreeBytes;
    public double FragmentationPct;
    public int PinnedObjectCount;
    public int SegmentCount;
}

public class GenerationStats
{
    public int Generation;
    public string Label;
    public long CommittedBytes;
    public long ObjectBytes;
    public long FreeBytes;
    public double FragmentationPct;
    public int SegmentCount;
    public int FreeChunkCount;

    // Same bucket shape as FreeChunkReport.Histogram, scoped to just this
    // generation - lets a caller show "what does Gen2's fragmentation
    // actually look like" (many small holes vs a few huge ones) instead of
    // only the cross-generation aggregate, which drowns out a single
    // generation's shape when the others differ wildly (e.g. Gen0 has
    // thousands of tiny holes that would swamp Gen2's histogram bars).
    public FreeChunkBucket[] Histogram;
}

public class FreeChunkReport
{
    public long TotalCount;
    public long TotalFreeBytes;
    public FreeChunkBucket[] Histogram;
    public List<LargeFreeChunk> LargeChunks;
}

public class FreeChunkBucket
{
    public string Label;
    public long MinBytes;
    public long MaxBytes;
    public long Count;
    public long TotalBytes;
}

// A single free hole large enough to accept a LOH allocation (>= 85,000 bytes).
// Address as a formatted hex string so JSON consumers don't need to handle
// 64-bit integer precision issues.
//
// Preceding/Following describe the nearest live objects on either side of
// this hole in address order within the same segment - the actual root
// cause of most non-LOH fragmentation. A compacting GC can't move an object
// past a pinned one, so a gap bounded by a pinned object on either side is a
// permanent hole regardless of how many compactions run afterward; a gap
// bounded by two ordinary (non-pinned) objects instead points at a GC that
// simply hasn't compacted this region yet, or a free-list size mismatch -
// a completely different fix. Empty string ("<start of segment>"/
// "<end of segment>") when the hole runs off either edge of its segment
// rather than being bounded by a live object.
public class LargeFreeChunk
{
    public string Address;
    public long SizeBytes;
    public int Generation;
    public string PrecedingTypeName;
    public bool PrecedingIsPinned;
    public string FollowingTypeName;
    public bool FollowingIsPinned;
}

// Pinned objects grouped by (TypeName, Generation) so the output immediately
// shows which types are pinning in which generation.
public class PinnedTypeStat
{
    public string TypeName;
    public int Generation;
    public int Count;
    public long TotalBytes;
}

// Shared shape for both TopLohTypes and TopPohTypes - a type ranked by how
// much of that heap it occupies, with no generation field of its own since
// each list is already scoped to one heap (LOH or POH).
public class TypeStat
{
    public string TypeName;
    public int Count;
    public long TotalBytes;
}

// Live occupancy of a single physical segment - a segment sitting at very
// low occupancy is a strong fragmentation signal on its own, independent of
// *why* (a pinned object, an ordinary long-lived anchor object, or a GC that
// simply hasn't compacted recently), and complements LargeFreeChunk by
// showing which physical segments the holes are concentrated in rather than
// just where each individual hole is. Ephemeral segments (workstation/
// non-region GC) hold Gen0/Gen1/Gen2 objects together and are attributed
// here to Gen2 (Generation == 2), matching HeapAnalyzer's existing
// MapSegmentKindToGeneration convention used for CommittedBytes/SegmentCount
// - so an ephemeral segment's occupancy reflects all three generations'
// objects combined, not Gen2 alone.
public class SegmentOccupancy
{
    public string Address;
    public int Generation;
    public long CommittedBytes;
    public long LiveBytes;
    public double OccupancyPct;
}

// Address-ordered block map for a single Gen2/LOH/POH segment - the data
// behind an "address-ordered strip" visualization (live-object runs and
// free-space gaps, left to right in memory order). Address matches the
// corresponding SegmentOccupancy entry so a caller can correlate "this
// segment is only 25% occupied" with "here's exactly why".
public class SegmentMap
{
    public string Address;
    public int Generation;
    public List<SegmentBlock> Blocks;
}

// One block in a SegmentMap - either a free-space gap, or a maximal run of
// consecutive live objects bounded by gaps (or by the segment's own generation
// boundary). A run can span many distinct types (e.g. a cache entry followed
// immediately by the byte[] it holds a reference to) - TypeName is whichever
// type contributes the most bytes to the run, with OtherTypeCount recording
// how many additional distinct types were folded in, so the strip stays
// readable instead of needing one block per individual object.
public class SegmentBlock
{
    public bool IsGap;
    public string TypeName;
    public int OtherTypeCount;
    public int ObjectCount;
    public long Bytes;
    public bool HasPinnedObject;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapAnalyzer)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
