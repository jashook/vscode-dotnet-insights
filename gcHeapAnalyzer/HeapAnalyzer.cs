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

    private const int TopTypeLimit = 50;

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

        FragmentationReport report = new FragmentationReport();
        report.ProcessId = pid;
        report.ProcessName = processName;
        report.CaptureTimeUtc = DateTime.UtcNow.ToString("o");

        report.Generations = BuildGenerationStatsArray();

        // Pinned handle enumeration runs *before* the object walk below (not
        // after, as this used to be ordered) specifically so pinnedAddresses
        // is populated in time for the free-chunk adjacency check inside
        // that walk to consult it. GCHandles can reference objects in any
        // generation (a gen0 object pinned by an async I/O operation is as
        // interesting as a gen2 one).
        Dictionary<string, PinnedTypeStat> pinnedByKey = new Dictionary<string, PinnedTypeStat>();
        HashSet<ulong> pinnedAddresses = new HashSet<ulong>();
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
            pinnedAddresses.Add(obj.Address);

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

        Console.Error.WriteLine($"Walking heap ({heap.Segments.Length} segment(s)) — process suspended...");

        FreeChunkBucket[] histogram = BuildHistogramBuckets();
        List<LargeFreeChunk> largeChunks = new List<LargeFreeChunk>();
        List<SegmentOccupancy> segmentOccupancy = new List<SegmentOccupancy>();
        List<SegmentMap> segmentMaps = new List<SegmentMap>();

        Dictionary<string, TypeStat> lohByType = new Dictionary<string, TypeStat>();
        // POH objects don't necessarily have a Pinned/AsyncPinned GCHandle at
        // all - GC.AllocateArray<T>(pinned: true) lives on this heap and is
        // non-relocatable by residency alone, no handle required - so
        // PinnedObjects (handle-enumeration-based, see above) can't be relied
        // on to show what's actually occupying the Pinned Object Heap. This
        // mirrors lohByType exactly for the same reason LOH gets one: without
        // it, the report can say POH is fragmented but never say why.
        Dictionary<string, TypeStat> pohByType = new Dictionary<string, TypeStat>();

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

            // Adjacency/occupancy state, reset per segment - a hole can't
            // span two segments, and neither can "the last live object seen
            // so far".
            long segmentLiveBytes = 0;
            bool hasLastLive = false;
            ulong lastLiveAddress = 0;
            LargeFreeChunk pendingChunk = null;

            // Block-map tracking (for the address-ordered segment strip
            // visualization) only runs for Gen2/LOH/POH segments - Gen0/Gen1
            // segments cycle through far too many objects for this to stay
            // small, and aren't what this feature is for. For an Ephemeral
            // segment (workstation GC), Gen2's objects occupy a contiguous
            // address-range prefix of the segment (Gen1 and Gen0 follow at
            // higher addresses) - EnumerateObjects walks in address order,
            // so trackingBlocks below simply stops the moment an object's
            // generation no longer matches primaryGen, without needing any
            // segment.Kind-specific handling.
            bool trackingBlocks = primaryGen == 2 || primaryGen == 3 || primaryGen == 4;
            bool pastTargetGenPrefix = false;
            List<SegmentBlock> blocks = trackingBlocks ? new List<SegmentBlock>() : null;
            Dictionary<string, long> currentRunTypeBytes = trackingBlocks ? new Dictionary<string, long>() : null;
            long currentRunBytes = 0;
            int currentRunObjectCount = 0;
            bool currentRunHasPinned = false;

            void FlushCurrentRun()
            {
                if (currentRunObjectCount == 0)
                {
                    return;
                }

                string dominantTypeName = "<unknown>";
                long dominantBytes = -1;
                foreach (KeyValuePair<string, long> entry in currentRunTypeBytes)
                {
                    if (entry.Value > dominantBytes)
                    {
                        dominantBytes = entry.Value;
                        dominantTypeName = entry.Key;
                    }
                }

                blocks.Add(new SegmentBlock
                {
                    IsGap = false,
                    TypeName = dominantTypeName,
                    OtherTypeCount = currentRunTypeBytes.Count - 1,
                    ObjectCount = currentRunObjectCount,
                    Bytes = currentRunBytes,
                    HasPinnedObject = currentRunHasPinned
                });

                currentRunTypeBytes.Clear();
                currentRunBytes = 0;
                currentRunObjectCount = 0;
                currentRunHasPinned = false;
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

                if (trackingBlocks && !pastTargetGenPrefix && objGen != primaryGen)
                {
                    FlushCurrentRun();
                    pastTargetGenPrefix = true;
                }

                if (obj.IsFree)
                {
                    if (objGen >= 0 && objGen < 5)
                    {
                        report.Generations[objGen].FreeBytes += objSize;
                        ++report.Generations[objGen].FreeChunkCount;
                        AddToHistogram(report.Generations[objGen].Histogram, objSize);
                    }

                    AddToHistogram(histogram, objSize);

                    if (trackingBlocks && !pastTargetGenPrefix)
                    {
                        if (blocks.Count > 0 && blocks[blocks.Count - 1].IsGap)
                        {
                            blocks[blocks.Count - 1].Bytes += objSize;
                            ++blocks[blocks.Count - 1].ObjectCount;
                        }
                        else
                        {
                            blocks.Add(new SegmentBlock { IsGap = true, Bytes = objSize, ObjectCount = 1 });
                        }
                    }

                    if (objSize >= LargeChunkThresholdBytes)
                    {
                        // Preceding is resolvable right now, from whatever
                        // live object we last walked past (re-resolved by
                        // address rather than carried as a ClrType/string on
                        // every iteration - this ClrType lookup only runs
                        // for the rare object adjacent to an actual large
                        // hole, not per object walked, same reasoning as why
                        // lohByType/pohByType below only resolve Type.Name
                        // for LOH/POH objects). Following can't be known
                        // until the next live object turns up, so it's left
                        // for the pendingChunk hookup below - or, if this
                        // segment ends first, resolved to "<end of segment>"
                        // right after this loop.
                        string precedingTypeName = "<start of segment>";
                        bool precedingIsPinned = false;
                        if (hasLastLive)
                        {
                            ClrObject precedingObj = heap.GetObject(lastLiveAddress);
                            precedingTypeName = precedingObj.Type?.Name ?? "<unknown>";
                            precedingIsPinned = pinnedAddresses.Contains(lastLiveAddress);
                        }

                        LargeFreeChunk chunk = new LargeFreeChunk
                        {
                            Address = $"0x{obj.Address:x16}",
                            SizeBytes = objSize,
                            Generation = objGen,
                            PrecedingTypeName = precedingTypeName,
                            PrecedingIsPinned = precedingIsPinned
                        };

                        largeChunks.Add(chunk);
                        pendingChunk = chunk;
                    }

                    continue;
                }

                // Live object - resolves any pending chunk's Following side,
                // then becomes the new "last live object" for whichever
                // chunk comes next.
                if (pendingChunk != null)
                {
                    pendingChunk.FollowingTypeName = obj.Type?.Name ?? "<unknown>";
                    pendingChunk.FollowingIsPinned = pinnedAddresses.Contains(obj.Address);
                    pendingChunk = null;
                }

                lastLiveAddress = obj.Address;
                hasLastLive = true;
                segmentLiveBytes += objSize;

                bool isLohOrPoh = objGen == 3 || objGen == 4;
                bool isTargetGenForBlocks = trackingBlocks && !pastTargetGenPrefix;

                if (isLohOrPoh || isTargetGenForBlocks)
                {
                    string typeName = obj.Type?.Name ?? "<unknown>";

                    if (isLohOrPoh)
                    {
                        Dictionary<string, TypeStat> typesByName = objGen == 3 ? lohByType : pohByType;

                        TypeStat typeStat;
                        if (!typesByName.TryGetValue(typeName, out typeStat))
                        {
                            typeStat = new TypeStat { TypeName = typeName };
                            typesByName[typeName] = typeStat;
                        }

                        ++typeStat.Count;
                        typeStat.TotalBytes += objSize;
                    }

                    if (isTargetGenForBlocks)
                    {
                        long existingRunBytes;
                        currentRunTypeBytes.TryGetValue(typeName, out existingRunBytes);
                        currentRunTypeBytes[typeName] = existingRunBytes + objSize;
                        ++currentRunObjectCount;
                        currentRunBytes += objSize;

                        if (pinnedAddresses.Contains(obj.Address))
                        {
                            currentRunHasPinned = true;
                        }
                    }
                }
            }

            if (trackingBlocks && !pastTargetGenPrefix)
            {
                FlushCurrentRun();
            }

            if (trackingBlocks)
            {
                segmentMaps.Add(new SegmentMap
                {
                    Address = $"0x{segment.Start:x16}",
                    Generation = primaryGen,
                    Blocks = blocks
                });
            }

            // A hole that runs to the end of the segment has no following
            // live object to resolve it against - distinct from "<unknown>"
            // (a real object whose type just couldn't be resolved).
            if (pendingChunk != null)
            {
                pendingChunk.FollowingTypeName = "<end of segment>";
                pendingChunk.FollowingIsPinned = false;
            }

            segmentOccupancy.Add(new SegmentOccupancy
            {
                Address = $"0x{segment.Start:x16}",
                Generation = primaryGen,
                CommittedBytes = segmentCommitted,
                LiveBytes = segmentLiveBytes,
                OccupancyPct = segmentCommitted > 0
                    ? Math.Round((segmentLiveBytes / (double)segmentCommitted) * 100.0, 2)
                    : 0.0
            });
        }

        long walkMs = phaseStopwatch.ElapsedMilliseconds;
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

        List<TypeStat> sortedLoh = new List<TypeStat>(lohByType.Values);
        sortedLoh.Sort((left, right) => right.TotalBytes.CompareTo(left.TotalBytes));
        if (sortedLoh.Count > TopTypeLimit)
        {
            sortedLoh.RemoveRange(TopTypeLimit, sortedLoh.Count - TopTypeLimit);
        }

        report.TopLohTypes = sortedLoh;

        List<TypeStat> sortedPoh = new List<TypeStat>(pohByType.Values);
        sortedPoh.Sort((left, right) => right.TotalBytes.CompareTo(left.TotalBytes));
        if (sortedPoh.Count > TopTypeLimit)
        {
            sortedPoh.RemoveRange(TopTypeLimit, sortedPoh.Count - TopTypeLimit);
        }

        report.TopPohTypes = sortedPoh;

        // Worst (least occupied) segments first - the ones most worth
        // looking at for "why is this segment still around".
        segmentOccupancy.Sort((left, right) => left.OccupancyPct.CompareTo(right.OccupancyPct));
        report.Segments = segmentOccupancy;
        report.SegmentMaps = segmentMaps;

        long postProcessMs = phaseStopwatch.ElapsedMilliseconds;
        long totalMs = totalStopwatch.ElapsedMilliseconds;

        double objectsPerSecond = walkMs > 0 ? totalObjectsWalked / (walkMs / 1000.0) : totalObjectsWalked;
        double handlesPerSecond = handleMs > 0 ? totalHandlesWalked / (handleMs / 1000.0) : totalHandlesWalked;

        Console.Error.WriteLine("Analysis complete.");
        Console.Error.WriteLine(
            $"Timing: attach={attachMs}ms createRuntime={createRuntimeMs}ms " +
            $"handleWalk={handleMs}ms ({totalHandlesWalked} handles, {handlesPerSecond:F0}/s) " +
            $"objectWalk={walkMs}ms ({totalObjectsWalked} objects, {objectsPerSecond:F0}/s) " +
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
                Label = labels[genIndex],
                Histogram = BuildHistogramBuckets()
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
