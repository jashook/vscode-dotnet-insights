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
        // Assigned once, sequentially, at Build time (insertion order - NOT
        // this range's position in sortedRanges, which gets reordered by
        // StartAddress) - see ResolveId/NameForId's own comment for why
        // this exists.
        public int Id;
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

    // Last range Resolve/ResolveId actually matched for a given address,
    // kept purely as a fast-path cache - a real capture resolves the same
    // handful of hot call-site addresses across huge numbers of distinct
    // stacks (a caller-tree fold walks every frame of every raw stack -
    // see AllocationJsonExporter.cs's BuildCallerTree), and the full binary
    // search + backward scan below was measured (dotnet-trace, a real
    // capture) as this whole export's single largest cost once that tree
    // build was added. A cache hit is only ever trusted after re-checking
    // the SAME [LoadRelativeMSec, UnloadRelativeMSec) condition the slow
    // path already enforces, so this changes nothing about correctness
    // (an address that got reused for a different method at a different
    // time still falls through to the real scan below, exactly as if this
    // cache didn't exist) - it only skips redoing the search when the
    // cached candidate's own window still covers the new call, which is
    // the overwhelmingly common case (most methods aren't reused at all).
    private readonly Dictionary<long, MethodRange> lastMatchedRangeByAddress = new Dictionary<long, MethodRange>();

    // namesById[range.Id] == range.DisplayName, in the same insertion order
    // Ids were assigned in at Build time - see ResolveId/NameForId's own
    // comment for why callers building a large data structure keyed by
    // "which method is this" (rather than needing the name itself
    // immediately) should prefer ResolveId over Resolve.
    private readonly List<string> namesById;

    // Ids for addresses that never matched any real range (or matched one
    // whose validity window didn't cover the call - see ResolveId's
    // fallback path) - assigned lazily, on first sight, offset past every
    // real range's Id (namesById.Count, fixed at construction) so the two
    // id spaces never collide. Also incidentally caches the "<unresolved
    // 0x...>" string's own formatting cost, previously redone on every
    // single call for the same never-resolved address.
    private readonly Dictionary<long, int> unresolvedIdByAddress = new Dictionary<long, int>();
    private readonly List<string> unresolvedNames = new List<string>();

    private MethodSymbolTable(List<MethodRange> sortedRanges, long maxRangeSize, List<string> namesById)
    {
        this.sortedRanges = sortedRanges;
        this.maxRangeSize = maxRangeSize;
        this.namesById = namesById;
    }

    public static MethodSymbolTable Build(List<EventRecord> events, int pointerSize, long qpcFrequency, long referenceQpc)
    {
        List<MethodRange> ranges = new List<MethodRange>();
        List<string> namesById = new List<string>();
        // Content-keyed, not per-range - two distinct MethodRanges (e.g.
        // two tiered-JIT code versions of the same source method) can
        // share the exact same DisplayName, and callers keying a data
        // structure by ResolveId's own id (see AllocationJsonExporter.cs's
        // BuildCallerTree) expect that case to merge into one entry, the
        // same as it always would have via the resolved string's own
        // content equality (Resolve's original, string-keyed behavior).
        // Interning here, once per distinct range at Build time, keeps
        // that merging guarantee while still handing hot-path callers a
        // cheap int id instead of a string to key by.
        Dictionary<string, int> idByDisplayName = new Dictionary<string, int>();

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
            // Content-interned, not insertion order - see idByDisplayName's
            // own comment: two ranges with the same DisplayName must share
            // one id so they merge for callers keying by ResolveId, the
            // same as they always would have via Resolve's own string
            // content equality.
            int displayNameId;
            if (!idByDisplayName.TryGetValue(range.DisplayName, out displayNameId))
            {
                displayNameId = namesById.Count;
                namesById.Add(range.DisplayName);
                idByDisplayName[range.DisplayName] = displayNameId;
            }

            range.Id = displayNameId;
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

        return new MethodSymbolTable(ranges, maxRangeSize, namesById);
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
    //
    // Thin wrapper over ResolveId/NameForId, kept for callers that just
    // want the name directly (tests, and any one-off resolution not on a
    // hot path) - see ResolveId's own comment for why a caller building a
    // large data structure keyed by "which method is this" should call
    // ResolveId instead and only convert to a string once it actually
    // needs one.
    public string Resolve(long instructionPointer, double relativeMSec)
    {
        return this.NameForId(this.ResolveId(instructionPointer, relativeMSec));
    }

    // Same address+time resolution as Resolve, but returns a small stable
    // integer identity instead of the resolved display name string -
    // callers building a data structure keyed by "which distinct method is
    // this" (see AllocationJsonExporter.cs's BuildCallerTree, which builds
    // a call-stack tree by walking every frame of every raw stack) can use
    // this as a MUCH cheaper dictionary key than the resolved string
    // itself: an int compare/hash instead of hashing the whole string's
    // content on every dictionary operation, which was measured
    // (dotnet-trace, a real capture) as this whole export's single largest
    // cost once that tree build started keying its nodes by resolved name.
    // Guaranteed: two calls return the same id if and only if they'd have
    // returned the same DisplayName from Resolve (not just equal
    // *content* - literally the same underlying range, or the same
    // memoized unresolved-address slot), and a given id's name never
    // changes for this table's lifetime - call NameForId once the string
    // is actually needed (e.g., at JSON-write time), not on this hot path.
    public int ResolveId(long instructionPointer, double relativeMSec)
    {
        MethodRange lastMatched;
        if (this.lastMatchedRangeByAddress.TryGetValue(instructionPointer, out lastMatched)
            && relativeMSec >= lastMatched.LoadRelativeMSec && relativeMSec < lastMatched.UnloadRelativeMSec)
        {
            return lastMatched.Id;
        }

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
        MethodRange fallbackRange = null;
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
                this.lastMatchedRangeByAddress[instructionPointer] = candidate;
                return candidate.Id;
            }

            // Address matches but this candidate's own validity window
            // doesn't cover relativeMSec (e.g. a since-reused range, or
            // missing Load/Unload data) - kept only as a last resort so a
            // frame still resolves to *something* real rather than
            // silently regressing to "unresolved" for captures whose
            // Load/Unload timing isn't perfectly clean. Deliberately NOT
            // cached into lastMatchedRangeByAddress - this range's own
            // window does not actually cover this call, so trusting it for
            // a *different* future relativeMSec could be wrong; only a
            // genuinely-validated match (above) is safe to cache.
            if (fallbackRange == null)
            {
                fallbackRange = candidate;
            }
        }

        if (fallbackRange != null)
        {
            return fallbackRange.Id;
        }

        int unresolvedId;
        if (!this.unresolvedIdByAddress.TryGetValue(instructionPointer, out unresolvedId))
        {
            unresolvedId = this.namesById.Count + this.unresolvedNames.Count;
            this.unresolvedNames.Add($"<unresolved 0x{instructionPointer:X}>");
            this.unresolvedIdByAddress[instructionPointer] = unresolvedId;
        }

        return unresolvedId;
    }

    // See ResolveId's own comment - id must have come from this same
    // MethodSymbolTable instance's own ResolveId.
    public string NameForId(int id)
    {
        if (id < this.namesById.Count)
        {
            return this.namesById[id];
        }

        return this.unresolvedNames[id - this.namesById.Count];
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
