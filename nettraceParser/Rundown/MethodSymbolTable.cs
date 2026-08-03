////////////////////////////////////////////////////////////////////////////////
// Module: MethodSymbolTable.cs
//
// Notes:
// Resolves a raw instruction pointer *at a given point in the capture's
// timeline* (see Resolve's relativeMSec parameter) to a method display
// name - built from every MethodDCStartVerbose (Rundown provider, fired
// once at trace-end for every method still loaded then), MethodLoadVerbose,
// and MethodUnloadVerbose (regular CLR provider, fired live during tracing,
// each with its own real timestamp - see ClrMethodRundown.cs) record's
// [MethodStartAddress, MethodStartAddress + MethodSize) range.
//
// The time dimension matters because code addresses get reused: a
// collectible/dynamic method (Reflection.Emit, compiled Expression
// lambdas - common in instrumentation/serialization code) can be JIT'd,
// run, and unloaded mid-capture, after which the CLR is free to hand that
// exact address range to a *different*, later-JIT'd method. A real bug
// found this way: a resolver that only ever consulted a single
// end-of-capture snapshot (the original, MethodDCStartVerbose-only version
// of this class) would resolve a stack frame captured *before* that
// address was reused to whichever method rundown's final snapshot says
// owns it *now* - producing a coherent-looking but factually wrong call
// chain (real method names from the same binary, just the wrong one for
// that specific historical frame). Each MethodRange below therefore
// carries a [LoadRelativeMSec, UnloadRelativeMSec) validity window, not
// just an address range - a DCStart-derived range's real load time within
// the capture is unknown (LoadRelativeMSec = 0, "valid since the start"),
// and a range with no matching Unload is still resident at capture end
// (UnloadRelativeMSec = double.MaxValue, "valid through the end").
//
// Rundown (and Load/Unload) only cover managed methods the process
// actually JIT'd - an IP outside every known range is expected (native/
// runtime-internal frames) and resolves to a placeholder rather than
// throwing.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Rundown {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using DotnetInsights.NetTrace.Gc;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class MethodSymbolTable
{
    private class MethodRange
    {
        public long StartAddress;
        public long EndAddress;
        public string DisplayName;
        public double LoadRelativeMSec;
        public double UnloadRelativeMSec;
    }

    private const string ClrProviderName = "Microsoft-Windows-DotNETRuntime";
    private const string ClrRundownProviderName = "Microsoft-Windows-DotNETRuntimeRundown";

    // Sorted by StartAddress, but - unlike the original single-snapshot
    // version of this table - ranges CAN legitimately overlap in address
    // now (different ranges, different time windows, from address reuse).
    // Resolve accounts for that with a bounded backward scan (see
    // maxRangeSize) rather than a plain non-overlapping binary search.
    private readonly List<MethodRange> sortedRanges;
    private readonly long maxRangeSize;

    private MethodSymbolTable(List<MethodRange> sortedRanges, long maxRangeSize)
    {
        this.sortedRanges = sortedRanges;
        this.maxRangeSize = maxRangeSize;
    }

    public static MethodSymbolTable Build(List<EventRecord> events, int pointerSize, long qpcFrequency, long referenceQpc)
    {
        List<MethodRange> ranges = new List<MethodRange>();

        // MethodID -> ranges awaiting a matching MethodUnloadVerbose,
        // oldest-load-first. A MethodID (like an address) can be reused
        // across multiple load/unload cycles within one capture - a plain
        // last-writer-wins dictionary would close off whichever occurrence
        // happened to be written last, not necessarily the one a given
        // Unload event actually corresponds to. FIFO queue per MethodID
        // pairs each Unload with the oldest still-open Load for that same
        // MethodID, matching a real chronological load-then-unload
        // sequence correctly.
        Dictionary<long, Queue<MethodRange>> pendingByMethodId = new Dictionary<long, Queue<MethodRange>>();

        long maxRangeSize = 0;

        // EventRecord is a struct (~70 bytes) - events is the whole capture's
        // event list (14.8M+ for a real 5-minute capture), so this is
        // iterated as a Span over the List<T>'s backing array rather than a
        // plain `foreach` - see GcEventProjector.Project's own comment on
        // why a boxed/virtual IEnumerable<T> enumerator regressed here once
        // EventRecord stopped being a cheap 8-byte class reference.
        Span<EventRecord> eventsSpan = CollectionsMarshal.AsSpan(events);
        for (int eventIndex = 0; eventIndex < eventsSpan.Length; ++eventIndex)
        {
            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            bool isRundownStart = record.ProviderName == ClrRundownProviderName && record.EventId == ClrRundownEventIds.MethodDCStartVerbose;
            bool isLoad = record.ProviderName == ClrProviderName && record.EventId == ClrMethodEventIds.MethodLoadVerbose;
            bool isUnload = record.ProviderName == ClrProviderName && record.EventId == ClrMethodEventIds.MethodUnloadVerbose;

            if (!isRundownStart && !isLoad && !isUnload)
            {
                continue;
            }

            PayloadReader reader = new PayloadReader(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength, pointerSize);

            if (isUnload)
            {
                ClrMethodRecord unloadedMethod = ClrMethodRecord.Decode(reader);
                if (unloadedMethod == null)
                {
                    continue;
                }

                Queue<MethodRange> pending;
                if (pendingByMethodId.TryGetValue(unloadedMethod.MethodID, out pending) && pending.Count > 0)
                {
                    MethodRange closedRange = pending.Dequeue();
                    closedRange.UnloadRelativeMSec = ComputeRelativeMSec(in record, qpcFrequency, referenceQpc);
                }

                continue;
            }

            ClrMethodRecord method = ClrMethodRecord.Decode(reader);
            if (method == null || method.MethodSize <= 0)
            {
                continue;
            }

            MethodRange range = new MethodRange();
            range.StartAddress = method.MethodStartAddress;
            range.EndAddress = method.MethodStartAddress + method.MethodSize;
            range.DisplayName = method.DisplayName;
            // isRundownStart: real load time within this capture is unknown
            // (the method may well have been loaded before tracing even
            // began) - 0 conservatively treats it as valid for the entire
            // capture rather than guessing a too-late lower bound.
            range.LoadRelativeMSec = isLoad ? ComputeRelativeMSec(in record, qpcFrequency, referenceQpc) : 0;
            range.UnloadRelativeMSec = double.MaxValue;
            ranges.Add(range);

            long rangeSize = range.EndAddress - range.StartAddress;
            if (rangeSize > maxRangeSize)
            {
                maxRangeSize = rangeSize;
            }

            if (isLoad)
            {
                Queue<MethodRange> pending;
                if (!pendingByMethodId.TryGetValue(method.MethodID, out pending))
                {
                    pending = new Queue<MethodRange>();
                    pendingByMethodId[method.MethodID] = pending;
                }

                pending.Enqueue(range);
            }
        }

        ranges.Sort(CompareByStartAddress);

        return new MethodSymbolTable(ranges, maxRangeSize);
    }

    private static double ComputeRelativeMSec(in EventRecord record, long qpcFrequency, long referenceQpc)
    {
        if (qpcFrequency <= 0)
        {
            return 0;
        }

        long qpcDelta = record.TimeStampRelativeQPC - referenceQpc;
        return qpcDelta * 1000.0 / qpcFrequency;
    }

    // relativeMSec must be the same "elapsed ms since SyncTimeQPC" domain
    // every other timestamp in this codebase uses (see
    // GcEventProjector.Project/AllocationEventProjector.Project) - callers
    // resolving a stack should pass the RelativeMSec of the specific tick
    // that captured it (or, for an aggregate merging several ticks sharing
    // one StackId, a representative one - see AllocationJsonExporter.cs's
    // StackAggregate.FirstSeenRelativeMSec).
    public string Resolve(long instructionPointer, double relativeMSec)
    {
        int lowIndex = 0;
        int highIndex = this.sortedRanges.Count - 1;
        int foundIndex = -1;

        // Rightmost range with StartAddress <= instructionPointer - the
        // starting point for the backward scan below. Ranges are sorted by
        // StartAddress only (they can overlap in address now, from address
        // reuse), so this alone doesn't guarantee instructionPointer falls
        // inside foundIndex's own range - just that every range past it
        // starts too late to contain it.
        while (lowIndex <= highIndex)
        {
            int midIndex = lowIndex + ((highIndex - lowIndex) / 2);
            if (this.sortedRanges[midIndex].StartAddress <= instructionPointer)
            {
                foundIndex = midIndex;
                lowIndex = midIndex + 1;
            }
            else
            {
                highIndex = midIndex - 1;
            }
        }

        // Scan backward from there - a real address-space reuse produces a
        // handful of overlapping candidates at most, not thousands, so this
        // is cheap in practice. maxRangeSize bounds how far back a range
        // could possibly start and still reach instructionPointer (every
        // earlier range has a smaller-or-equal StartAddress, so once even
        // the largest-possible method size couldn't span the gap, no
        // earlier range can either) - this keeps the scan from ever
        // degrading to a full linear pass over every range in the file.
        string fallbackDisplayName = null;
        for (int scanIndex = foundIndex; scanIndex >= 0; --scanIndex)
        {
            MethodRange candidate = this.sortedRanges[scanIndex];

            if (instructionPointer - candidate.StartAddress > this.maxRangeSize)
            {
                break;
            }

            if (instructionPointer < candidate.StartAddress || instructionPointer >= candidate.EndAddress)
            {
                continue;
            }

            if (relativeMSec >= candidate.LoadRelativeMSec && relativeMSec < candidate.UnloadRelativeMSec)
            {
                return candidate.DisplayName;
            }

            // Address matches but this candidate's own validity window
            // doesn't cover relativeMSec (e.g. a since-reused range, or
            // missing Load/Unload data) - kept only as a last resort so a
            // frame still resolves to *something* real rather than
            // silently regressing to "unresolved" for captures whose
            // Load/Unload timing isn't perfectly clean.
            if (fallbackDisplayName == null)
            {
                fallbackDisplayName = candidate.DisplayName;
            }
        }

        return fallbackDisplayName ?? $"<unresolved 0x{instructionPointer:X}>";
    }

    private static int CompareByStartAddress(MethodRange left, MethodRange right)
    {
        return left.StartAddress.CompareTo(right.StartAddress);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Rundown)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
