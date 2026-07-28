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
public class LargeFreeChunk
{
    public string Address;
    public long SizeBytes;
    public int Generation;
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

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapAnalyzer)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
