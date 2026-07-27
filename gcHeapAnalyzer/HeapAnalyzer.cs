////////////////////////////////////////////////////////////////////////////////
// Module: HeapAnalyzer.cs
//
// Notes:
// ClrMD-based analysis of a live .NET process's managed heap. Suspends the
// target process for the duration of the heap walk to get a fully consistent
// snapshot - the process resumes when DataTarget is disposed at the end of
// Analyze(). The suspension typically lasts a few hundred milliseconds for
// heaps in the 100MB-few-GB range.
//
// Generation mapping from GCSegmentKind:
//   Ephemeral -> gen0/1/2 determined by per-address range check within segment
//   Generation0/1/2 -> dedicated server-GC segments, directly mapped
//   Large -> gen 3 (LOH)
//   Pinned -> gen 4 (POH)
//   Frozen -> skipped (runtime-internal, not user heap)
//
// Free chunks >= LargeChunkThresholdBytes (85,000 bytes, the LOH threshold)
// are individually listed in FreeChunkReport.LargeChunks because a free slot
// that large is both rare and directly actionable: it can absorb a LOH
// allocation without growing the segment, or its persistence indicates a
// specific allocation hole from a past pinned/large object.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.GcHeapAnalyzer {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class HeapAnalyzer
{
    // The .NET runtime promotes objects to LOH when they are >= this size.
    // Free chunks at or above this threshold are individually catalogued.
    private const long LargeChunkThresholdBytes = 85_000;

    private const int TopLohTypeLimit = 50;

    public static bool TryAnalyze(int pid, out FragmentationReport report, out string errorMessage)
    {
        report = null;
        errorMessage = null;

        try
        {
            report = Analyze(pid);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static FragmentationReport Analyze(int pid)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch phaseStopwatch = Stopwatch.StartNew();

        Console.Error.WriteLine($"Attaching to process {pid}...");

        using DataTarget target = DataTarget.AttachToProcess(pid, suspend: true);

        if (target.ClrVersions.Length == 0)
        {
            throw new InvalidOperationException($"No CLR runtime found in process {pid}. Is this a managed .NET process?");
        }

        long attachMs = phaseStopwatch.ElapsedMilliseconds;
        phaseStopwatch.Restart();

        ClrRuntime runtime = target.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        long createRuntimeMs = phaseStopwatch.ElapsedMilliseconds;
        phaseStopwatch.Restart();

        string processName = GetProcessName(pid);
        Console.Error.WriteLine($"Walking heap ({heap.Segments.Length} segment(s)) — process suspended...");

        FragmentationReport report = new FragmentationReport();
        report.ProcessId = pid;
        report.ProcessName = processName;
        report.CaptureTimeUtc = DateTime.UtcNow.ToString("o");

        report.Generations = BuildGenerationStatsArray();

        FreeChunkBucket[] histogram = BuildHistogramBuckets();
        List<LargeFreeChunk> largeChunks = new List<LargeFreeChunk>();

        Dictionary<string, PinnedTypeStat> pinnedByKey = new Dictionary<string, PinnedTypeStat>();
        Dictionary<string, LohTypeStat> lohByType = new Dictionary<string, LohTypeStat>();

        int totalSegments = 0;
        long totalCommitted = 0;
        long totalObjectsWalked = 0;

        foreach (ClrSegment segment in heap.Segments)
        {
            if (segment.Kind == GCSegmentKind.Frozen)
            {
                continue;
            }

            ++totalSegments;
            long segmentCommitted = (long)segment.CommittedMemory.Length;
            totalCommitted += segmentCommitted;

            int primaryGen = MapSegmentKindToGeneration(segment.Kind);
            if (primaryGen >= 0)
            {
                report.Generations[primaryGen].CommittedBytes += segmentCommitted;
                ++report.Generations[primaryGen].SegmentCount;
            }

            foreach (ClrObject obj in segment.EnumerateObjects())
            {
                ++totalObjectsWalked;

                if (!obj.IsValid)
                {
                    continue;
                }

                long objSize = (long)obj.Size;
                int objGen = GetObjectGeneration(obj.Address, segment);

                if (obj.IsFree)
                {
                    if (objGen >= 0 && objGen < 5)
                    {
                        report.Generations[objGen].FreeBytes += objSize;
                        ++report.Generations[objGen].FreeChunkCount;
                    }

                    AddToHistogram(histogram, objSize);

                    if (objSize >= LargeChunkThresholdBytes)
                    {
                        largeChunks.Add(new LargeFreeChunk
                        {
                            Address = $"0x{obj.Address:x16}",
                            SizeBytes = objSize,
                            Generation = objGen
                        });
                    }
                }
                else if (objGen == 3)
                {
                    string typeName = obj.Type?.Name ?? "<unknown>";

                    LohTypeStat lohStat;
                    if (!lohByType.TryGetValue(typeName, out lohStat))
                    {
                        lohStat = new LohTypeStat { TypeName = typeName };
                        lohByType[typeName] = lohStat;
                    }

                    ++lohStat.Count;
                    lohStat.TotalBytes += objSize;
                }
            }
        }

        long walkMs = phaseStopwatch.ElapsedMilliseconds;
        phaseStopwatch.Restart();

        // Pinned handle enumeration — separate from the object walk since
        // GCHandles can reference objects in any generation (a gen0 object
        // pinned by an async I/O operation is as interesting as a gen2 one).
        int pinnedCount = 0;
        long totalHandlesWalked = 0;
        foreach (ClrHandle handle in runtime.EnumerateHandles())
        {
            ++totalHandlesWalked;

            if (handle.HandleKind != ClrHandleKind.Pinned && handle.HandleKind != ClrHandleKind.AsyncPinned)
            {
                continue;
            }

            ClrObject obj = handle.Object;
            if (!obj.IsValid)
            {
                continue;
            }

            ++pinnedCount;

            string typeName = obj.Type?.Name ?? "<unknown>";
            ClrSegment objSegment = heap.GetSegmentByAddress(obj.Address);
            int gen = objSegment != null ? GetObjectGeneration(obj.Address, objSegment) : -1;

            string key = $"{typeName}|{gen}";

            PinnedTypeStat pinnedStat;
            if (!pinnedByKey.TryGetValue(key, out pinnedStat))
            {
                pinnedStat = new PinnedTypeStat { TypeName = typeName, Generation = gen };
                pinnedByKey[key] = pinnedStat;
            }

            ++pinnedStat.Count;
            pinnedStat.TotalBytes += (long)obj.Size;
        }

        long handleMs = phaseStopwatch.ElapsedMilliseconds;
        phaseStopwatch.Restart();

        // Compute derived fields
        long totalFree = 0;
        for (int genIndex = 0; genIndex < report.Generations.Length; ++genIndex)
        {
            GenerationStats genStats = report.Generations[genIndex];
            genStats.ObjectBytes = genStats.CommittedBytes - genStats.FreeBytes;
            genStats.FragmentationPct = genStats.CommittedBytes > 0
                ? Math.Round((genStats.FreeBytes / (double)genStats.CommittedBytes) * 100.0, 2)
                : 0.0;
            totalFree += genStats.FreeBytes;
        }

        report.Summary = new HeapSummary
        {
            TotalCommittedBytes = totalCommitted,
            TotalObjectBytes = totalCommitted - totalFree,
            TotalFreeBytes = totalFree,
            FragmentationPct = totalCommitted > 0
                ? Math.Round((totalFree / (double)totalCommitted) * 100.0, 2)
                : 0.0,
            PinnedObjectCount = pinnedCount,
            SegmentCount = totalSegments
        };

        largeChunks.Sort((left, right) => right.SizeBytes.CompareTo(left.SizeBytes));

        long totalHistogramCount = 0;
        long totalHistogramBytes = 0;
        for (int bucketIndex = 0; bucketIndex < histogram.Length; ++bucketIndex)
        {
            totalHistogramCount += histogram[bucketIndex].Count;
            totalHistogramBytes += histogram[bucketIndex].TotalBytes;
        }

        report.FreeChunks = new FreeChunkReport
        {
            TotalCount = totalHistogramCount,
            TotalFreeBytes = totalHistogramBytes,
            Histogram = histogram,
            LargeChunks = largeChunks
        };

        List<PinnedTypeStat> sortedPinned = new List<PinnedTypeStat>(pinnedByKey.Values);
        sortedPinned.Sort((left, right) => right.Count.CompareTo(left.Count));
        report.PinnedObjects = sortedPinned;

        List<LohTypeStat> sortedLoh = new List<LohTypeStat>(lohByType.Values);
        sortedLoh.Sort((left, right) => right.TotalBytes.CompareTo(left.TotalBytes));
        if (sortedLoh.Count > TopLohTypeLimit)
        {
            sortedLoh.RemoveRange(TopLohTypeLimit, sortedLoh.Count - TopLohTypeLimit);
        }

        report.TopLohTypes = sortedLoh;

        long postProcessMs = phaseStopwatch.ElapsedMilliseconds;
        long totalMs = totalStopwatch.ElapsedMilliseconds;

        double objectsPerSecond = walkMs > 0 ? totalObjectsWalked / (walkMs / 1000.0) : totalObjectsWalked;
        double handlesPerSecond = handleMs > 0 ? totalHandlesWalked / (handleMs / 1000.0) : totalHandlesWalked;

        Console.Error.WriteLine("Analysis complete.");
        Console.Error.WriteLine(
            $"Timing: attach={attachMs}ms createRuntime={createRuntimeMs}ms " +
            $"objectWalk={walkMs}ms ({totalObjectsWalked} objects, {objectsPerSecond:F0}/s) " +
            $"handleWalk={handleMs}ms ({totalHandlesWalked} handles, {handlesPerSecond:F0}/s) " +
            $"postProcess={postProcessMs}ms total={totalMs}ms");

        return report;
    }

    private static int MapSegmentKindToGeneration(GCSegmentKind kind)
    {
        switch (kind)
        {
            case GCSegmentKind.Generation0: return 0;
            case GCSegmentKind.Generation1: return 1;
            case GCSegmentKind.Generation2: return 2;
            case GCSegmentKind.Large:       return 3;
            case GCSegmentKind.Pinned:      return 4;
            case GCSegmentKind.Ephemeral:   return 2;
            default: return -1;
        }
    }

    private static int GetObjectGeneration(ulong address, ClrSegment segment)
    {
        switch (segment.Kind)
        {
            case GCSegmentKind.Generation0: return 0;
            case GCSegmentKind.Generation1: return 1;
            case GCSegmentKind.Generation2: return 2;
            case GCSegmentKind.Large:       return 3;
            case GCSegmentKind.Pinned:      return 4;

            case GCSegmentKind.Ephemeral:
                if (address >= segment.Generation0.Start && address < segment.Generation0.End)
                {
                    return 0;
                }

                if (address >= segment.Generation1.Start && address < segment.Generation1.End)
                {
                    return 1;
                }

                return 2;

            default: return -1;
        }
    }

    private static GenerationStats[] BuildGenerationStatsArray()
    {
        string[] labels = { "Gen0", "Gen1", "Gen2", "LOH", "POH" };
        GenerationStats[] stats = new GenerationStats[5];

        for (int genIndex = 0; genIndex < 5; ++genIndex)
        {
            stats[genIndex] = new GenerationStats
            {
                Generation = genIndex,
                Label = labels[genIndex]
            };
        }

        return stats;
    }

    private static FreeChunkBucket[] BuildHistogramBuckets()
    {
        return new FreeChunkBucket[]
        {
            new FreeChunkBucket { Label = "< 1 KB",      MinBytes = 0,          MaxBytes = 1_023 },
            new FreeChunkBucket { Label = "1–8 KB",      MinBytes = 1_024,      MaxBytes = 8_191 },
            new FreeChunkBucket { Label = "8–85 KB",     MinBytes = 8_192,      MaxBytes = 84_999 },
            new FreeChunkBucket { Label = "85 KB–1 MB",  MinBytes = 85_000,     MaxBytes = 1_048_575 },
            new FreeChunkBucket { Label = "> 1 MB",      MinBytes = 1_048_576,  MaxBytes = long.MaxValue }
        };
    }

    private static void AddToHistogram(FreeChunkBucket[] histogram, long sizeBytes)
    {
        for (int bucketIndex = 0; bucketIndex < histogram.Length; ++bucketIndex)
        {
            if (sizeBytes >= histogram[bucketIndex].MinBytes && sizeBytes <= histogram[bucketIndex].MaxBytes)
            {
                ++histogram[bucketIndex].Count;
                histogram[bucketIndex].TotalBytes += sizeBytes;
                return;
            }
        }
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return $"pid_{pid}";
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcHeapAnalyzer)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
