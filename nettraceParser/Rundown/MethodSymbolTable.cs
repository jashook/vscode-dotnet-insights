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
using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class MethodSymbolTable
{
    // Sentinel for MethodRange.Id meaning "DisplayName not decoded yet" -
    // never returned to a ResolveId caller (EnsureResolved always replaces
    // it with a real id, >= 0, before any Id is read back out) - purely an
    // internal "have we paid the decode cost for this range yet" flag.
    private const int UnassignedRangeId = -1;

    private class MethodRange
    {
        public long StartAddress;
        public long EndAddress;
        public double LoadRelativeMSec;
        public double UnloadRelativeMSec;

        // DisplayName is deliberately NOT decoded at Build time - see
        // EnsureResolved's own comment for the measured reason (a real
        // capture's method-rundown volume is dominated, often by well over
        // an order of magnitude, by methods no resolved stack frame ever
        // actually looks up). This is the raw payload slice DecodeHeader
        // already proved is long enough to decode (ClrMethodRundown.cs's
        // DecodeHeader/DecodeDisplayName share one bounds check for exactly
        // this reason) - PayloadBuffer is the whole capture's shared byte[]
        // (see EventRecord.cs), so holding this reference costs nothing
        // beyond what already existed; it does NOT re-root the 4.29M+
        // EventRecord structs Program.cs's `file = null` is specifically
        // written to let go of (see that file's own comment) - only the
        // (already free-to-trace, per that same comment) raw byte[] buffer
        // itself survives a little longer, for the small subset of ranges
        // whose payload offset/length actually got stored here.
        public byte[] PayloadBuffer;
        public int PayloadOffset;
        public int PayloadLength;

        // UnassignedRangeId until EnsureResolved decodes this range's
        // DisplayName for the first time (lazily, on first real match from
        // ResolveId) and interns it - see EnsureResolved. Once assigned,
        // stable for this table's lifetime, same guarantee ResolveId's own
        // doc comment already makes.
        public int Id = UnassignedRangeId;
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
    // ids were assigned - NOT at Build time anymore (see EnsureResolved),
    // but this list is still append-only and a range's own Id, once
    // assigned, is a permanently valid index into it regardless of how
    // much longer the list keeps growing afterward. See ResolveId/
    // NameForId's own comment for why callers building a large data
    // structure keyed by "which method is this" (rather than needing the
    // name itself immediately) should prefer ResolveId over Resolve.
    private readonly List<string> namesById;

    // Content-keyed, not per-range - two distinct MethodRanges (e.g. two
    // tiered-JIT code versions of the same source method) can share the
    // exact same DisplayName, and callers keying a data structure by
    // ResolveId's own id (see AllocationJsonExporter.cs's BuildCallerTree)
    // expect that case to merge into one entry, the same as it always
    // would have via the resolved string's own content equality (Resolve's
    // original, string-keyed behavior). Populated lazily now (see
    // EnsureResolved), not eagerly at Build time - the merging guarantee
    // is unaffected either way, since it only depends on every DisplayName
    // being looked up against the same shared dictionary before a new id
    // is minted, regardless of when that lookup happens.
    private readonly Dictionary<string, int> idByDisplayName = new Dictionary<string, int>();

    // pointerSize is fixed for a whole capture (see NettraceHeader) -
    // stored once here rather than per-range, since EnsureResolved needs it
    // to reconstruct a PayloadReader over a range's saved payload slice.
    private readonly int pointerSize;

    // Fixed, large base for ids handed out via the unresolved-address path
    // below - deliberately NOT "namesById.Count" (a moving target now that
    // real ids are minted lazily, interleaved in time with unresolved-id
    // lookups, instead of all being assigned upfront before any Resolve
    // call). A real capture will never have anywhere near a billion
    // distinct method ranges, so this can never collide with a real id.
    //
    // Public because callers that MEMOIZE something per frame id (see
    // Cpu/IdleWaitFrameCache.cs) have to know the id space is two dense
    // ranges - [0, namesById.Count) and [UnresolvedIdBase, ...) - rather
    // than one, so they can index an array per range instead of paying a
    // dictionary lookup per id.
    public const int UnresolvedIdBase = 1_000_000_000;

    // Ids for addresses that never matched any real range (or matched one
    // whose validity window didn't cover the call - see ResolveId's
    // fallback path) - assigned lazily, on first sight. Also incidentally
    // caches the "<unresolved 0x...>" string's own formatting cost,
    // previously redone on every single call for the same never-resolved
    // address.
    private readonly Dictionary<long, int> unresolvedIdByAddress = new Dictionary<long, int>();
    private readonly List<string> unresolvedNames = new List<string>();

    private MethodSymbolTable(List<MethodRange> sortedRanges, long maxRangeSize, List<string> namesById, int pointerSize)
    {
        this.sortedRanges = sortedRanges;
        this.maxRangeSize = maxRangeSize;
        this.namesById = namesById;
        this.pointerSize = pointerSize;
    }

    public static MethodSymbolTable Build(List<EventRecord> events, int pointerSize, long qpcFrequency, long referenceQpc, Action<double> onProgress = null)
    {
        List<MethodRange> ranges = new List<MethodRange>();
        List<string> namesById = new List<string>();

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
            if (onProgress != null && (eventIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                onProgress((double)eventIndex / eventsSpan.Length);
            }

            ref readonly EventRecord record = ref eventsSpan[eventIndex];

            bool isRundownStart = record.ProviderName == ClrRundownProviderName && record.EventId == ClrRundownEventIds.MethodDCStartVerbose;
            bool isLoad = record.ProviderName == ClrProviderName && record.EventId == ClrMethodEventIds.MethodLoadVerbose;
            bool isUnload = record.ProviderName == ClrProviderName && record.EventId == ClrMethodEventIds.MethodUnloadVerbose;

            if (!isRundownStart && !isLoad && !isUnload)
            {
                continue;
            }

            PayloadReader reader = new PayloadReader(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength, pointerSize);

            // Header-only decode for every branch below (MethodID/
            // MethodStartAddress/MethodSize, no string reads) - see
            // ClrMethodRundown.cs's own comment on DecodeHeader for why:
            // isUnload only ever needed MethodID in the first place (its
            // MethodStartAddress/MethodSize/DisplayName were always
            // discarded), and isRundownStart/isLoad now defer their own
            // DisplayName decode to EnsureResolved below instead of paying
            // for it here unconditionally.
            long decodedMethodId;
            long decodedMethodStartAddress;
            long decodedMethodSize;
            if (!ClrMethodRecord.DecodeHeader(reader, out decodedMethodId, out decodedMethodStartAddress, out decodedMethodSize))
            {
                continue;
            }

            if (isUnload)
            {
                Queue<MethodRange> pending;
                if (pendingByMethodId.TryGetValue(decodedMethodId, out pending) && pending.Count > 0)
                {
                    MethodRange closedRange = pending.Dequeue();
                    closedRange.UnloadRelativeMSec = ComputeRelativeMSec(in record, qpcFrequency, referenceQpc);
                }

                continue;
            }

            if (decodedMethodSize <= 0)
            {
                continue;
            }

            MethodRange range = new MethodRange();
            range.StartAddress = decodedMethodStartAddress;
            range.EndAddress = decodedMethodStartAddress + decodedMethodSize;
            range.PayloadBuffer = record.PayloadBuffer;
            range.PayloadOffset = record.PayloadOffset;
            range.PayloadLength = record.PayloadLength;
            // isRundownStart: real load time within this capture is unknown
            // (the method may well have been loaded before tracing even
            // began) - 0 conservatively treats it as valid for the entire
            // capture rather than guessing a too-late lower bound.
            range.LoadRelativeMSec = isLoad ? ComputeRelativeMSec(in record, qpcFrequency, referenceQpc) : 0;
            range.UnloadRelativeMSec = double.MaxValue;
            // range.Id stays UnassignedRangeId (its own field initializer) -
            // DisplayName decode + content-interning now happens lazily,
            // the first time this range is actually matched - see
            // EnsureResolved. Measured against a real 736MB/4.29M-event
            // capture (dotnet-trace, dotnet-sampled-thread-time profile):
            // this capture's rundown alone carried 101,795
            // MethodDCStartVerbose records, but only ~1,700 distinct
            // methods ever showed up across every resolved allocation/
            // exception stack frame in the whole export - eagerly decoding
            // (two UTF-16 string reads + a concat) for all 101,795
            // regardless was over 50x more decode work than the capture
            // actually needed.
            ranges.Add(range);

            long rangeSize = range.EndAddress - range.StartAddress;
            if (rangeSize > maxRangeSize)
            {
                maxRangeSize = rangeSize;
            }

            if (isLoad)
            {
                Queue<MethodRange> pending;
                if (!pendingByMethodId.TryGetValue(decodedMethodId, out pending))
                {
                    pending = new Queue<MethodRange>();
                    pendingByMethodId[decodedMethodId] = pending;
                }

                pending.Enqueue(range);
            }
        }

        ranges.Sort(CompareByStartAddress);

        return new MethodSymbolTable(ranges, maxRangeSize, namesById, pointerSize);
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
            // Already resolved by definition (only a range that already
            // passed through the validated-match branch below ever gets
            // cached here), but EnsureResolved is a cheap no-op once
            // resolved - calling it unconditionally avoids relying on that
            // invariant staying true forever as this method evolves.
            this.EnsureResolved(lastMatched);
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
                this.EnsureResolved(candidate);
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
            this.EnsureResolved(fallbackRange);
            return fallbackRange.Id;
        }

        int unresolvedId;
        if (!this.unresolvedIdByAddress.TryGetValue(instructionPointer, out unresolvedId))
        {
            unresolvedId = UnresolvedIdBase + this.unresolvedNames.Count;
            this.unresolvedNames.Add($"<unresolved 0x{instructionPointer:X}>");
            this.unresolvedIdByAddress[instructionPointer] = unresolvedId;
        }

        return unresolvedId;
    }

    // Decodes and content-interns range's DisplayName the first time it's
    // actually matched by a real ResolveId call - see MethodRange's own
    // comment on why this is deferred instead of happening eagerly at
    // Build time. A no-op once range.Id is already assigned (every call
    // site below calls this unconditionally rather than checking first,
    // since the check IS this method's own first line).
    private void EnsureResolved(MethodRange range)
    {
        if (range.Id != UnassignedRangeId)
        {
            return;
        }

        PayloadReader reader = new PayloadReader(range.PayloadBuffer, range.PayloadOffset, range.PayloadLength, this.pointerSize);
        string displayName = ClrMethodRecord.DecodeDisplayName(reader);

        // Same content-interning idByDisplayName's own comment describes -
        // just performed lazily, on first match, instead of eagerly for
        // every range at Build time.
        int displayNameId;
        if (!this.idByDisplayName.TryGetValue(displayName, out displayNameId))
        {
            displayNameId = this.namesById.Count;
            this.namesById.Add(displayName);
            this.idByDisplayName[displayName] = displayNameId;
        }

        range.Id = displayNameId;
    }

    // See ResolveId's own comment - id must have come from this same
    // MethodSymbolTable instance's own ResolveId.
    public string NameForId(int id)
    {
        // Branch on UnresolvedIdBase, not namesById.Count - see that
        // constant's own comment for why: namesById.Count is no longer
        // fixed once ids are minted lazily (EnsureResolved), so a
        // since-grown count would compute the wrong index into
        // unresolvedNames for an id minted back when the count was
        // smaller. UnresolvedIdBase is a fixed boundary a real id can
        // never reach, so this stays correct regardless of interleaving.
        if (id >= UnresolvedIdBase)
        {
            return this.unresolvedNames[id - UnresolvedIdBase];
        }

        return this.namesById[id];
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
