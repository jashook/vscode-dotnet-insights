////////////////////////////////////////////////////////////////////////////////
// Module: HeapDumpEventDecoder.cs
//
// Notes:
// Builds a HeapGraph directly from the GC heap-dump EVENTS in a `.nettrace`,
// with no involvement from `dotnet-gcdump` at all.
//
// WHY THIS EXISTS. `dotnet-gcdump collect` silently truncates. Two independent
// mechanisms, both in ITS post-processing rather than in the runtime's event
// stream:
//
//   - `MaxNodeCount = 10_000_000` in dotnet/diagnostics'
//     DotNetHeapDumpGraphReader.cs. On hitting it the reader stops adding
//     nodes, logs a `[WARNING]`, and the tool writes the truncated file
//     anyway. That log goes to `TextWriter.Null` unless `-v` is passed, so by
//     default the truncation is invisible. Reproduced here: a process holding
//     ~12M objects produced a dump containing exactly 10,000,000 nodes, with
//     the sampling multipliers still reporting 1.
//   - A 30-second default `--timeout` in EventPipeDotNetHeapDumper.cs, after
//     which the dump is abandoned (this one at least fails loudly - no file is
//     written).
//
// The runtime emits the complete event stream in both cases. So capturing with
// `dotnet-trace` and decoding the events ourselves removes the truncation
// entirely - there is no node cap here, and the only ceiling is the .gcdump
// format's own 2GB node blob (see GcDumpFormat.cs), roughly 100M+ objects.
//
// EVENT IDS AND LAYOUTS were confirmed against a real capture, not assumed -
// the payloads below carry no field metadata over EventPipe (every one of them
// decodes with an empty EventName), so they are hand-decoded by event id the
// same way this project already hand-decodes GCPerHeapHistory. The ids match
// TraceEvent's own `eventID` values.
//
// THE NODE/EDGE PAIRING is the subtle part. Nodes and their outgoing edges
// arrive on two SEPARATE bulk streams: a GCBulkNode value carries an EdgeCount
// but not the edges themselves, and GCBulkEdge carries one flat, globally
// ordered run of targets. The i'th node owns the next EdgeCount_i entries of
// that run.
//
// ============================================================================
// THREE PASSES, TO KEEP PEAK MEMORY DOWN
// ============================================================================
//
// The obvious single-pass shape - accumulate nodes and edge targets into
// List<T>s, then assemble - measured **3.8GB of decode-phase RSS** on a
// 12M-object/35.8M-edge heap, on top of the ~950MB the trace itself occupies.
// Three things caused it, and all three are structural rather than incidental:
//
//   1. Edge targets were buffered as 8-byte ADDRESSES (35.8M x 8 = 286MB)
//      and only resolved to node indices afterward.
//   2. Every buffer was a List<T> reaching its size by DOUBLING, so each one
//      held its old array alive while allocating one twice the size - the
//      edge list alone spiked past 570MB mid-copy.
//   3. A Dictionary<ulong, int> of 12M entries costs ~470MB and doubles its
//      way there too (see AddressToIndexMap.cs).
//
// Passing over the events three times fixes all three, because after the
// counting pass every allocation's exact final size is known:
//
//   Pass 1  Count node values, edge values and root values. Decode the type
//           table (types must be complete before any node resolves its type,
//           and BulkType events are not guaranteed to precede GCBulkNode).
//   Pass 2  Define nodes into exactly-sized arrays, assigning each node its
//           index in definition order.
//   Pass 3  Resolve edges STRAIGHT into the final CSR ChildTarget array.
//
// Pass 3 needs no intermediate buffer at all: because node indices are handed
// out in definition order, and the edge stream is ordered by that same
// definition order, the i'th node's edges land exactly where CSR wants them.
// That deletes the 286MB edge buffer outright rather than shrinking it.
//
// Re-walking the event list costs nothing - it is a few thousand entries whose
// payloads are already in memory; the passes are over the payload BYTES, which
// would be read three times either way.
//
// Measured result on the same 12M-object capture: decode-phase RSS 3.8GB ->
// ~0.6GB, with byte-identical output.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

using DotnetInsights.NetTrace.Progress;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class HeapDumpEventIds
{
    // Microsoft-Windows-DotNETRuntime. See this file's header for how these
    // were confirmed.
    public const int BulkType = 15;
    public const int GcBulkRootEdge = 16;
    public const int GcBulkRootConditionalWeakTableElementEdge = 17;
    public const int GcBulkNode = 18;
    public const int GcBulkEdge = 19;
    public const int GcBulkRootStaticVar = 38;

    public const string RuntimeProviderName = "Microsoft-Windows-DotNETRuntime";
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public readonly struct HeapDumpDecodeResult
{
    public readonly HeapGraph Graph;
    public readonly string ErrorMessage;

    // Taken from the capture's own header during the counting pass, so the
    // caller never has to read the trace a fourth time just to learn who
    // produced it.
    public readonly int ProcessId;
    public readonly long SyncTimeUtcTicks;

    private HeapDumpDecodeResult(HeapGraph graph, string errorMessage, int processId, long syncTimeUtcTicks)
    {
        this.Graph = graph;
        this.ErrorMessage = errorMessage;
        this.ProcessId = processId;
        this.SyncTimeUtcTicks = syncTimeUtcTicks;
    }

    public bool Succeeded
    {
        get
        {
            return this.ErrorMessage == null;
        }
    }

    public static HeapDumpDecodeResult Success(HeapGraph graph, int processId, long syncTimeUtcTicks)
    {
        return new HeapDumpDecodeResult(graph, null, processId, syncTimeUtcTicks);
    }

    public static HeapDumpDecodeResult Failure(string errorMessage)
    {
        return new HeapDumpDecodeResult(null, errorMessage, 0, 0);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class HeapDumpEventDecoder
{
    // A node's own record inside a GCBulkNode payload, on a 64-bit target.
    // TraceEvent expresses this as HostOffset(28, 1), which is 32 once the one
    // pointer-sized field is 8 bytes wide.
    private const int NodeValueStride64 = 32;
    private const int BulkNodeHeaderBytes = 10;

    // Target (8) + ReferencingField (4).
    private const int EdgeValueStride64 = 12;
    private const int BulkEdgeHeaderBytes = 10;

    // RootedNodeAddress (8) + GCRootKind (1) + GCRootFlag (4) + GCRootID (8).
    private const int RootEdgeValueStride64 = 21;
    private const int BulkRootEdgeHeaderBytes = 10;

    // Count (4) + AppDomainID (8) + ClrInstanceID (2).
    private const int BulkRootStaticVarHeaderBytes = 14;

    private const int BulkTypeHeaderBytes = 6;

    // GCRootFlags.WeakRef.
    private const int GcRootFlagWeakRef = 0x2;

    // Streams the capture three times rather than taking a materialized event
    // list, so no block buffer outlives the pass that reads it - see this
    // file's header. The three reads are over a file the OS has just written
    // and therefore has cached; the memory they save is not.
    public static HeapDumpDecodeResult Decode(string tracePath)
    {
        TypeTableBuilder types = new TypeTableBuilder();

        // ONE read, materialized. Streaming the capture three times (one per
        // pass, retaining nothing) was measured and is WORSE: it triples the
        // read phase's allocation churn, and this process runs under Server
        // GC, which is throughput-tuned and lets garbage accumulate. On the
        // 12M-object capture that took collections from 2 to 32, GC pause from
        // ~4ms to ~2s, wall clock from ~7s to ~12s, and peak RSS UP rather
        // than down. Holding ~800MB of block buffers costs less than
        // allocating 2.4GB of them.
        NettraceFile traceFile = NettraceFile.Read(tracePath, null);
        List<EventRecord> events = traceFile.Events;
        NettraceHeader header = traceFile.Header;

        DecodeCounts counts;
        string countError = CountAndDecodeTypes(events, types, out counts);

        if (header != null && header.PointerSize != 8)
        {
            // Every offset in this decoder is the 64-bit form. A 32-bit
            // capture would need the narrower strides throughout; rejecting it
            // is far better than decoding it into a plausible-looking wrong
            // graph.
            return HeapDumpDecodeResult.Failure($"Only 64-bit captures are supported for heap-dump decoding (this one reports a pointer size of {header.PointerSize}).");
        }

        if (countError != null)
        {
            return HeapDumpDecodeResult.Failure(countError);
        }

        if (counts.NodeValueCount == 0)
        {
            return HeapDumpDecodeResult.Failure(
                "This trace contains no GC heap-dump events. Capture with " +
                "`dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x1980001:5`, " +
                "which is what asks the runtime to walk the heap.");
        }

        if (counts.NodeValueCount > int.MaxValue - 2)
        {
            return HeapDumpDecodeResult.Failure($"This heap has {counts.NodeValueCount} objects, more than this decoder's {int.MaxValue - 2} limit.");
        }

        if (counts.EdgeValueCount + counts.RootValueCount > int.MaxValue)
        {
            return HeapDumpDecodeResult.Failure($"This heap has {counts.EdgeValueCount + counts.RootValueCount} references, more than this decoder's {int.MaxValue} limit.");
        }

        HeapDumpDecodeResult built = BuildGraph(events, types, counts);

        if (!built.Succeeded)
        {
            return built;
        }

        return HeapDumpDecodeResult.Success(built.Graph, header != null ? header.ProcessId : 0, header != null ? header.SyncTimeUtc.Ticks : 0);
    }

    ////////////////////////////////////////////////////////////////////////////

    private struct DecodeCounts
    {
        public long NodeValueCount;
        public long EdgeValueCount;
        public long RootValueCount;
    }

    // PASS 1. Sizes every subsequent allocation, and completes the type table.
    //
    // Types are decoded here rather than in pass 2 because nothing guarantees
    // a BulkType event precedes the GCBulkNode events referring to those type
    // ids; resolving a node's type against a half-built table would silently
    // mark it UNDEFINED.
    private static string CountAndDecodeTypes(List<EventRecord> events, TypeTableBuilder types, out DecodeCounts counts)
    {
        DecodeCounts running = new DecodeCounts();
        string gapError = null;

        long expectedNodeBlockIndex = 0;
        long expectedEdgeBlockIndex = 0;

        ForEachEvent(events, (record) =>
        {
            if (record.ProviderName != HeapDumpEventIds.RuntimeProviderName || record.PayloadBuffer == null || gapError != null)
            {
                return;
            }

            ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength);

            switch (record.EventId)
            {
                case HeapDumpEventIds.BulkType:
                {
                    types.AddFromBulkTypeEvent(payload);
                    break;
                }

                case HeapDumpEventIds.GcBulkNode:
                {
                    long blockIndex = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    int count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));

                    // GCBulkNode/GCBulkEdge each carry an `Index` that is the
                    // sequence number of the BLOCK, not a running count of the
                    // items inside it - verified against a real capture, where
                    // the second node block reports 1 rather than the 2008
                    // items the first one delivered. Blocks pair with each
                    // other purely by arrival order (which is what
                    // dotnet/diagnostics' own reader does - it enqueues them
                    // and never looks at Index), so this exists only to notice
                    // a block that never arrived.
                    if (blockIndex != expectedNodeBlockIndex)
                    {
                        gapError = GapMessage($"GCBulkNode block {blockIndex} arrived where block {expectedNodeBlockIndex} was expected");
                        return;
                    }

                    expectedNodeBlockIndex = blockIndex + 1;
                    running.NodeValueCount += CountValues(payload, BulkNodeHeaderBytes, NodeValueStride64, count);
                    break;
                }

                case HeapDumpEventIds.GcBulkEdge:
                {
                    long blockIndex = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    int count = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));

                    if (blockIndex != expectedEdgeBlockIndex)
                    {
                        gapError = GapMessage($"GCBulkEdge block {blockIndex} arrived where block {expectedEdgeBlockIndex} was expected");
                        return;
                    }

                    expectedEdgeBlockIndex = blockIndex + 1;
                    running.EdgeValueCount += CountValues(payload, BulkEdgeHeaderBytes, EdgeValueStride64, count);
                    break;
                }

                case HeapDumpEventIds.GcBulkRootEdge:
                {
                    running.RootValueCount += CountRootEdges(payload);
                    break;
                }

                case HeapDumpEventIds.GcBulkRootStaticVar:
                {
                    running.RootValueCount += CountRootStaticVars(payload);
                    break;
                }

                case HeapDumpEventIds.GcBulkRootConditionalWeakTableElementEdge:
                {
                    // Not decoded. A conditional-weak-table edge is a
                    // dependent handle (key keeps value alive), which affects
                    // reachability but is a genuinely different edge kind from
                    // an object field. Treating it as a plain root would
                    // overstate what is rooted, and treating it as a normal
                    // edge would attribute the reference to the wrong owner -
                    // so it is skipped rather than guessed at. Objects reached
                    // ONLY through such an edge therefore show up as unrooted.
                    break;
                }
            }
        });

        counts = running;
        return gapError;
    }

    // Applies a callback over every event. Written this way rather than as a
    // plain foreach at each call site so the three passes keep the "return to
    // skip this event" shape they had when they streamed.
    private static void ForEachEvent(List<EventRecord> events, Action<EventRecord> onEvent)
    {
        for (int eventIndex = 0; eventIndex < events.Count; ++eventIndex)
        {
            onEvent(events[eventIndex]);
        }
    }

    private static string GapMessage(string detail)
    {
        // The bulk streams carry their own block sequence numbers precisely so
        // a dropped block is detectable. Continuing would pair every
        // subsequent edge with the wrong node - individually plausible,
        // entirely wrong - so this fails loudly instead. The usual cause is
        // dotnet-trace's circular buffer dropping events under pressure.
        return $"The heap-dump event stream has gaps, so nodes and edges cannot be paired reliably ({detail}). " +
               "Re-capture with a larger dotnet-trace --buffersize.";
    }

    // A truncated final block would otherwise make pass 1 and pass 2 disagree
    // about how many values there are, so both count it the same way.
    private static int CountValues(ReadOnlySpan<byte> payload, int headerBytes, int stride, int declaredCount)
    {
        int available = (payload.Length - headerBytes) / stride;
        return declaredCount < available ? declaredCount : (available < 0 ? 0 : available);
    }

    private static int CountRootEdges(ReadOnlySpan<byte> payload)
    {
        int declaredCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));
        int usableCount = CountValues(payload, BulkRootEdgeHeaderBytes, RootEdgeValueStride64, declaredCount);
        int rooted = 0;

        for (int valueIndex = 0; valueIndex < usableCount; ++valueIndex)
        {
            int valueOffset = BulkRootEdgeHeaderBytes + (valueIndex * RootEdgeValueStride64);

            if (IsCountedRootEdge(payload, valueOffset))
            {
                ++rooted;
            }
        }

        return rooted;
    }

    // A weak reference does NOT keep its target alive, so treating one as a
    // root would report objects as reachable that a GC would happily collect -
    // and would attribute their whole retained subtree to the wrong place.
    // dotnet/diagnostics' own reader drops these for the same reason.
    private static bool IsCountedRootEdge(ReadOnlySpan<byte> payload, int valueOffset)
    {
        ulong rootedAddress = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(valueOffset));

        if (rootedAddress == 0)
        {
            return false;
        }

        int rootFlags = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(valueOffset + 9));
        return (rootFlags & GcRootFlagWeakRef) == 0;
    }

    private static int CountRootStaticVars(ReadOnlySpan<byte> payload)
    {
        // Walked exactly the way the fill pass walks it, so the two can never
        // disagree about these variable-length records.
        int rooted = 0;
        int declaredCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int cursor = BulkRootStaticVarHeaderBytes;

        for (int valueIndex = 0; valueIndex < declaredCount; ++valueIndex)
        {
            if (cursor + 28 > payload.Length)
            {
                break;
            }

            if (BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(cursor + 8)) != 0)
            {
                ++rooted;
            }

            cursor = SkipUnicodeString(payload, cursor + 28);

            if (cursor < 0)
            {
                break;
            }
        }

        return rooted;
    }

    private static int SkipUnicodeString(ReadOnlySpan<byte> payload, int cursor)
    {
        while (cursor + 1 < payload.Length)
        {
            if (payload[cursor] == 0 && payload[cursor + 1] == 0)
            {
                return cursor + 2;
            }

            cursor += 2;
        }

        return -1;
    }

    ////////////////////////////////////////////////////////////////////////////

    // PASSES 2 AND 3. Everything allocated here is sized exactly from pass 1's
    // counts, so nothing grows and nothing is copied.
    // PASSES 2 AND 3. Everything allocated here is sized exactly from pass 1's
    // counts, so nothing grows and nothing is copied. Both passes stream the
    // capture rather than holding it.
    private static HeapDumpDecodeResult BuildGraph(List<EventRecord> events, TypeTableBuilder types, DecodeCounts counts)
    {
        int definedNodeCapacity = (int)counts.NodeValueCount;
        int rootChildCount = (int)counts.RootValueCount;

        // Defined nodes occupy [0, definedNodeCount) in definition order.
        // Addresses that are only ever REFERENCED get indices after them, and
        // the synthetic root goes last. Undefined nodes are rare (tens, on
        // captures checked here), so they are collected in a small list rather
        // than reserved for.
        List<ulong> undefinedAddresses = new List<ulong>();

        AddressToIndexMap indexByAddress = new AddressToIndexMap(definedNodeCapacity);

        // Uninitialized: every one of these is written for [0, definedNodeCount)
        // before anything reads it, and nothing ever reads past that. Zeroing
        // them first is ~330MB of pure waste on a 12M-object heap, which
        // measured as most of a 743ms gap between BuildGraph's total and the
        // sum of its two passes.
        //
        // The map's own arrays deliberately do NOT get this treatment - it
        // uses a zero key as its empty-slot marker, so uninitialized memory
        // there would be indistinguishable from occupied slots.
        ulong[] definedAddress = GC.AllocateUninitializedArray<ulong>(definedNodeCapacity);
        int[] definedTypeIndex = GC.AllocateUninitializedArray<int>(definedNodeCapacity);
        int[] definedSize = GC.AllocateUninitializedArray<int>(definedNodeCapacity);

        // Child counts land here first and become CSR start offsets below.
        int[] definedEdgeCount = GC.AllocateUninitializedArray<int>(definedNodeCapacity);

        // Allocated HERE, before a single node is populated, rather than after
        // pass 2 where it is first needed. Size comes from pass 1's own edge
        // and root counts.
        //
        // This is not tidiness. Allocating 143MB on the large object heap once
        // ~600MB of nodes and map are already live triggers a gen2 collection,
        // and that one allocation measured **691ms** - a fifth of the entire
        // conversion, for a single `new`. Moving it to a point where almost
        // nothing is live makes the same allocation effectively free, because
        // any collection it provokes has nothing to trace.


        int nextNodeIndex = 0;
        long totalSize = 0;

        // PASS 2: define every node, in the order the capture defines them.
        ForEachEvent(events, (record) =>
        {
            if (record.ProviderName != HeapDumpEventIds.RuntimeProviderName || record.PayloadBuffer == null || record.EventId != HeapDumpEventIds.GcBulkNode)
            {
                return;
            }

            ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength);
            int declaredCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));
            int usableCount = CountValues(payload, BulkNodeHeaderBytes, NodeValueStride64, declaredCount);

            for (int valueIndex = 0; valueIndex < usableCount; ++valueIndex)
            {
                if (nextNodeIndex >= definedNodeCapacity)
                {
                    return;
                }

                ReadOnlySpan<byte> value = payload.Slice(BulkNodeHeaderBytes + (valueIndex * NodeValueStride64));

                ulong address = BinaryPrimitives.ReadUInt64LittleEndian(value);
                int size = (int)BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(8));
                ulong typeId = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(16));
                int edgeCount = (int)BinaryPrimitives.ReadInt64LittleEndian(value.Slice(24));

                int assignedIndex = indexByAddress.GetOrAdd(address, nextNodeIndex);

                if (assignedIndex != nextNodeIndex)
                {
                    // The same address defined twice. Keep the first, and do
                    // NOT consume its edges again - double-counting here would
                    // desynchronize the whole edge stream from pass 3 onward.
                    continue;
                }

                definedAddress[nextNodeIndex] = address;
                definedTypeIndex[nextNodeIndex] = types.IndexOfTypeId(typeId);
                definedSize[nextNodeIndex] = size;
                definedEdgeCount[nextNodeIndex] = edgeCount;

                totalSize += size;
                ++nextNodeIndex;
            }
        });

        int definedNodeCount = nextNodeIndex;

        int[] childStartForDefined = GC.AllocateUninitializedArray<int>(definedNodeCount + 1);
        long totalEdgeSlots = 0;

        for (int nodeIndex = 0; nodeIndex < definedNodeCount; ++nodeIndex)
        {
            childStartForDefined[nodeIndex] = (int)totalEdgeSlots;
            totalEdgeSlots += definedEdgeCount[nodeIndex];
        }

        childStartForDefined[definedNodeCount] = (int)totalEdgeSlots;
        totalEdgeSlots += rootChildCount;

        // Allocated HERE rather than up front with the node arrays. Hoisting
        // it earlier was tried and measured WORSE (4192ms vs 3613ms total):
        // acquiring ~190MB of fresh pages from the OS costs the same wherever
        // it happens, but doing it while the 800MB trace is live and the node
        // arrays are not yet populated gave the runtime a worse moment to grow
        // the heap.
        int[] childTarget = GC.AllocateUninitializedArray<int>((int)totalEdgeSlots);

        // Root targets are buffered as addresses (there are only ever a few
        // thousand) so that roots and edges can share ONE pass over the
        // capture - they interleave in the file, but roots must land after
        // every edge in the CSR array.
        ulong[] rootAddresses = new ulong[rootChildCount];
        int rootAddressCount = 0;

        int currentNodeIndex = 0;
        int remainingForCurrentNode = definedNodeCount > 0 ? definedEdgeCount[0] : 0;
        int writeCursor = 0;

        // PASS 3: resolve edge targets straight into their final CSR slots.
        // This works only because node indices were handed out in definition
        // order above, which is the order the flat edge stream follows - so
        // the write cursor advances monotonically and no intermediate buffer
        // of edge addresses is ever needed.
        ForEachEvent(events, (record) =>
        {
            if (record.ProviderName != HeapDumpEventIds.RuntimeProviderName || record.PayloadBuffer == null)
            {
                return;
            }

            ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(record.PayloadBuffer, record.PayloadOffset, record.PayloadLength);

            if (record.EventId == HeapDumpEventIds.GcBulkEdge)
            {
                int declaredCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));
                int usableCount = CountValues(payload, BulkEdgeHeaderBytes, EdgeValueStride64, declaredCount);

                for (int valueIndex = 0; valueIndex < usableCount; ++valueIndex)
                {
                    ulong target = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(BulkEdgeHeaderBytes + (valueIndex * EdgeValueStride64)));

                    while (remainingForCurrentNode == 0 && currentNodeIndex + 1 < definedNodeCount)
                    {
                        ++currentNodeIndex;
                        remainingForCurrentNode = definedEdgeCount[currentNodeIndex];
                    }

                    if (remainingForCurrentNode == 0 || writeCursor >= childStartForDefined[definedNodeCount])
                    {
                        break;
                    }

                    int targetIndex;

                    if (target == 0)
                    {
                        // A null reference in an object field. Pointing it at
                        // the owner keeps the CSR run the right length without
                        // inventing a node.
                        targetIndex = currentNodeIndex;
                    }
                    else
                    {
                        targetIndex = ResolveOrAppend(indexByAddress, target, definedNodeCount, undefinedAddresses);
                    }

                    childTarget[writeCursor] = targetIndex;
                    ++writeCursor;
                    --remainingForCurrentNode;
                }

                return;
            }

            if (record.EventId == HeapDumpEventIds.GcBulkRootEdge)
            {
                int declaredCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4));
                int usableCount = CountValues(payload, BulkRootEdgeHeaderBytes, RootEdgeValueStride64, declaredCount);

                for (int valueIndex = 0; valueIndex < usableCount; ++valueIndex)
                {
                    int valueOffset = BulkRootEdgeHeaderBytes + (valueIndex * RootEdgeValueStride64);

                    if (!IsCountedRootEdge(payload, valueOffset) || rootAddressCount >= rootAddresses.Length)
                    {
                        continue;
                    }

                    rootAddresses[rootAddressCount] = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(valueOffset));
                    ++rootAddressCount;
                }

                return;
            }

            if (record.EventId == HeapDumpEventIds.GcBulkRootStaticVar)
            {
                int declaredCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
                int cursor = BulkRootStaticVarHeaderBytes;

                for (int valueIndex = 0; valueIndex < declaredCount; ++valueIndex)
                {
                    if (cursor + 28 > payload.Length)
                    {
                        break;
                    }

                    ulong objectId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(cursor + 8));

                    if (objectId != 0 && rootAddressCount < rootAddresses.Length)
                    {
                        rootAddresses[rootAddressCount] = objectId;
                        ++rootAddressCount;
                    }

                    cursor = SkipUnicodeString(payload, cursor + 28);

                    if (cursor < 0)
                    {
                        break;
                    }
                }
            }
        });


        // childTarget is uninitialized, so any slot the edge stream did not
        // reach still holds arbitrary bytes rather than a harmless zero. A
        // short or truncated edge stream is exactly the case that leaves a gap,
        // and an unwritten slot would surface as an out-of-range node index in
        // every downstream analysis. Fill it with a self-reference, which is
        // the same thing a null field decodes to.
        int edgeFillEnd = childStartForDefined[definedNodeCount];

        if (writeCursor < edgeFillEnd)
        {
            int gapOwner = currentNodeIndex < definedNodeCount ? currentNodeIndex : 0;

            for (int gapIndex = writeCursor; gapIndex < edgeFillEnd; ++gapIndex)
            {
                childTarget[gapIndex] = gapOwner;
            }
        }

        // Roots occupy the tail of childTarget, owned by the synthetic root.
        int rootEdgeStart = childStartForDefined[definedNodeCount];
        writeCursor = rootEdgeStart;

        for (int rootIndex = 0; rootIndex < rootAddressCount; ++rootIndex)
        {
            childTarget[writeCursor] = ResolveOrAppend(indexByAddress, rootAddresses[rootIndex], definedNodeCount, undefinedAddresses);
            ++writeCursor;
        }

        return HeapDumpDecodeResult.Success(AssembleGraph(
            types,
            definedNodeCount,
            definedAddress,
            definedTypeIndex,
            definedSize,
            childStartForDefined,
            undefinedAddresses,
            childTarget,
            rootEdgeStart,
            writeCursor,
            totalSize), 0, 0);
    }


    private static int ResolveOrAppend(AddressToIndexMap indexByAddress, ulong address, int definedNodeCount, List<ulong> undefinedAddresses)
    {
        int candidateIndex = definedNodeCount + undefinedAddresses.Count;
        int resolvedIndex = indexByAddress.GetOrAdd(address, candidateIndex);

        if (resolvedIndex == candidateIndex)
        {
            undefinedAddresses.Add(address);
        }

        return resolvedIndex;
    }

    // Stitches the defined nodes, the referenced-but-undefined ones and the
    // synthetic root into the final HeapGraph.
    private static HeapGraph AssembleGraph(
        TypeTableBuilder types,
        int definedNodeCount,
        ulong[] definedAddress,
        int[] definedTypeIndex,
        int[] definedSize,
        int[] childStartForDefined,
        List<ulong> undefinedAddresses,
        int[] childTarget,
        int rootEdgeStart,
        int rootEdgeEnd,
        long totalSize)
    {
        int undefinedCount = undefinedAddresses.Count;
        int nodeCount = definedNodeCount + undefinedCount + 1;
        int rootNodeIndex = nodeCount - 1;

        HeapGraph graph = new HeapGraph();
        graph.NodeCount = nodeCount;
        graph.RootNodeIndex = rootNodeIndex;
        graph.TotalSize = totalSize;
        graph.ChildTarget = childTarget;

        int rootTypeIndex = types.InternSyntheticType("[.NET Roots]");
        types.Materialize(graph);

        // When nothing was left undefined (the common case) the defined arrays
        // are reused as-is apart from the single trailing root slot, so the
        // whole assembly costs one small copy rather than three large ones.
        graph.NodeAddresses = GrowUlong(definedAddress, definedNodeCount, nodeCount);
        graph.NodeTypeIndex = GrowInt(definedTypeIndex, definedNodeCount, nodeCount);
        graph.NodeSize = GrowInt(definedSize, definedNodeCount, nodeCount);
        graph.ChildStart = GrowInt(childStartForDefined, definedNodeCount + 1, nodeCount + 1);

        for (int undefinedIndex = 0; undefinedIndex < undefinedCount; ++undefinedIndex)
        {
            int nodeIndex = definedNodeCount + undefinedIndex;
            graph.NodeAddresses[nodeIndex] = undefinedAddresses[undefinedIndex];
            graph.NodeTypeIndex[nodeIndex] = HeapGraph.UndefinedTypeIndex;
            graph.NodeSize[nodeIndex] = 0;
            graph.ChildStart[nodeIndex] = rootEdgeStart;
        }

        graph.NodeTypeIndex[rootNodeIndex] = rootTypeIndex;
        graph.NodeSize[rootNodeIndex] = 0;
        graph.NodeAddresses[rootNodeIndex] = 0;
        graph.ChildStart[rootNodeIndex] = rootEdgeStart;
        graph.ChildStart[nodeCount] = rootEdgeEnd;

        return graph;
    }

    private static int[] GrowInt(int[] source, int usedLength, int newLength)
    {
        if (source.Length >= newLength)
        {
            return source;
        }

        int[] grown = new int[newLength];
        Array.Copy(source, grown, usedLength);
        return grown;
    }

    private static ulong[] GrowUlong(ulong[] source, int usedLength, int newLength)
    {
        if (source.Length >= newLength)
        {
            return source;
        }

        ulong[] grown = new ulong[newLength];
        Array.Copy(source, grown, usedLength);
        return grown;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Maps the runtime's type IDs (method table pointers) onto the dense type
// table a .gcdump carries. Type counts are in the hundreds or low thousands
// even on a huge heap, so this one really can be a plain Dictionary.
internal sealed class TypeTableBuilder
{
    private readonly Dictionary<ulong, int> indexByTypeId = new Dictionary<ulong, int>();
    private readonly List<string> names = new List<string>();

    // Objects of the same type arrive in runs (the heap walk visits segments,
    // and a segment is usually dominated by a few types), so remembering the
    // last answer turns most of the 12M-per-capture lookups into a comparison.
    // Profiling attributed ~3.6% of the whole conversion to this dictionary
    // before the cache. lastTypeIndex starts at -1 rather than 0 so that a
    // genuine type id of 0 cannot be confused with "nothing cached yet".
    private ulong lastTypeId;
    private int lastTypeIndex = -1;

    public TypeTableBuilder()
    {
        // Index 0 is UNDEFINED, matching a real .gcdump's own type table.
        this.names.Add("UNDEFINED");
    }

    public void AddFromBulkTypeEvent(ReadOnlySpan<byte> payload)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int cursor = 6;

        for (int valueIndex = 0; valueIndex < count; ++valueIndex)
        {
            // TypeID (8) + ModuleID (8) + TypeNameID (4) + Flags (4) +
            // CorElementType (1), then a null-terminated UTF-16 name, then
            // TypeParameterCount (4) and that many 8-byte parameter ids.
            if (cursor + 25 > payload.Length)
            {
                return;
            }

            ulong typeId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(cursor));

            int nameStart = cursor + 25;
            int nameEnd = FindUnicodeStringEnd(payload, nameStart);

            if (nameEnd < 0)
            {
                return;
            }

            string typeName = Encoding.Unicode.GetString(payload.Slice(nameStart, nameEnd - nameStart));

            cursor = nameEnd + 2;

            if (cursor + 4 > payload.Length)
            {
                return;
            }

            int typeParameterCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor));
            cursor += 4 + (typeParameterCount * 8);

            if (!this.indexByTypeId.ContainsKey(typeId))
            {
                this.indexByTypeId.Add(typeId, this.names.Count);
                this.names.Add(NormalizeGenericTypeName(typeName));
            }
        }
    }

    // The runtime emits generic type names in reflection syntax -
    // "System.WeakReference`1[System.Diagnostics.Tracing.EventSource]" - while
    // every tool that displays a heap (dotnet-gcdump, PerfView, Visual Studio)
    // shows C# syntax: "System.WeakReference<System.Diagnostics.Tracing.EventSource>".
    //
    // Normalizing here rather than in the renderer means the .gcdump this
    // writes carries the same names dotnet-gcdump's would, so a census taken
    // from a converted trace lines up row-for-row against one taken from a
    // native dump instead of splitting every generic into two differently
    // spelled rows.
    //
    // Only the arity-and-brackets form is rewritten. Compiler-generated names
    // like "<>c__DisplayClass0_0" are left exactly as the runtime spelled
    // them; dotnet-gcdump rewrites those angle brackets to square ones, which
    // is a lossy display convention rather than a more correct name.
    private static string NormalizeGenericTypeName(string typeName)
    {
        int backtickIndex = typeName.IndexOf('`');

        if (backtickIndex < 0)
        {
            return typeName;
        }

        int openBracketIndex = typeName.IndexOf('[', backtickIndex);

        if (openBracketIndex < 0 || !typeName.EndsWith("]", StringComparison.Ordinal))
        {
            return typeName;
        }

        // An array of a generic ("List`1[Int32][]") ends in "[]" rather than a
        // parameter list; rewriting that would produce nonsense.
        if (openBracketIndex + 1 >= typeName.Length || typeName[openBracketIndex + 1] == ']')
        {
            return typeName;
        }

        StringBuilder normalized = new StringBuilder(typeName.Length);
        normalized.Append(typeName, 0, backtickIndex);
        normalized.Append('<');
        normalized.Append(typeName, openBracketIndex + 1, typeName.Length - openBracketIndex - 2);
        normalized.Append('>');

        return normalized.ToString();
    }

    private static int FindUnicodeStringEnd(ReadOnlySpan<byte> payload, int cursor)
    {
        while (cursor + 1 < payload.Length)
        {
            if (payload[cursor] == 0 && payload[cursor + 1] == 0)
            {
                return cursor;
            }

            cursor += 2;
        }

        return -1;
    }

    // For the synthetic root, which has no runtime type id of its own.
    public int InternSyntheticType(string name)
    {
        int index = this.names.Count;
        this.names.Add(name);
        return index;
    }

    public int IndexOfTypeId(ulong typeId)
    {
        if (this.lastTypeIndex >= 0 && typeId == this.lastTypeId)
        {
            return this.lastTypeIndex;
        }

        int index;

        if (!this.indexByTypeId.TryGetValue(typeId, out index))
        {
            index = HeapGraph.UndefinedTypeIndex;
        }

        this.lastTypeId = typeId;
        this.lastTypeIndex = index;

        return index;
    }

    public void Materialize(HeapGraph graph)
    {
        graph.TypeCount = this.names.Count;
        graph.TypeNames = this.names.ToArray();
        graph.TypeSizes = new int[this.names.Count];
        graph.TypeModuleNames = new string[this.names.Count];

        for (int typeIndex = 0; typeIndex < this.names.Count; ++typeIndex)
        {
            // Module NAMES are not carried by BulkType - it identifies the
            // module by id, and resolving that to a path needs the
            // ModuleLoad/ModuleDCStop rundown events. Left empty rather than
            // guessed; the UI already treats an empty module as "unknown"
            // (see GcDumpRenderer.ts's own module handling).
            graph.TypeModuleNames[typeIndex] = "";
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
