////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpWriter.cs
//
// Notes:
// Writes a HeapGraph out as a real `.gcdump` file - the exact format PerfView,
// Visual Studio and `dotnet-gcdump report` all read. This is the inverse of
// GcDumpReader.cs; GcDumpFormat.cs specifies the layout both implement, and
// the two are meant to be read side by side.
//
// This is what makes "collect a gcdump without dotnet-gcdump" a real
// capability rather than an internal detail: the output is a normal .gcdump,
// so it opens in every existing heap tool as well as this extension's own
// editor.
//
// NO NODE CAP. dotnet-gcdump's own reader stops at 10,000,000 nodes and writes
// the truncated result anyway (see HeapDumpEventDecoder.cs's header). Nothing
// here imposes a limit; the real ceiling is the format's own int32 blob length,
// which is 2GB - roughly 100M+ objects at typical per-node record sizes.
//
// TYPE SIZES ARE NOT COSMETIC. A type table entry's Size is what lets a node
// omit its own size (the low bit of `typeAndSize`), and `dotnet-gcdump report`
// SKIPS every type whose Size is 0 - so writing 0 for everything would produce
// a file that is structurally valid, round-trips through this repo's own
// reader perfectly, and shows up as an empty report in the tool everyone would
// check it against. Each type therefore takes the size of its first instance,
// and only instances that differ from it pay for an explicit size.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using DotnetInsights.NetTrace.Progress;
using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpWriter
{
    public static void WriteToFile(string outputPath, HeapGraph graph, GcDumpMetadata metadata)
    {
        NodeBlob blob = NodeBlob.Build(graph);

        GCHeapDump entry = new GCHeapDump(graph, metadata, blob);

        // The Serializer takes ownership of the writer and closes it on
        // Dispose, so the writer must NOT get a `using` of its own - disposing
        // it a second time flushes to an already-closed file and throws
        // ObjectDisposedException after the whole file has been written.
        IOStreamStreamWriter streamWriter = new IOStreamStreamWriter(outputPath, GcDumpFormat.ReaderSettings);

        using (Serializer serializer = new Serializer(streamWriter, entry))
        {
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// The node blob plus each node's offset into it - the two pieces the format
// stores separately (see GcDumpFormat.cs's "NODE BLOB" section).
internal sealed class NodeBlob
{
    public byte[] Bytes;
    public int Length;
    public int[] NodeOffsets;
    public int[] TypeSizes;

    public static NodeBlob Build(HeapGraph graph)
    {
        NodeBlob blob = new NodeBlob();
        blob.NodeOffsets = new int[graph.NodeCount];
        blob.TypeSizes = ChooseTypeSizes(graph);

        // Sized EXACTLY, by encoding the whole graph once to count bytes
        // before encoding it again to write them.
        //
        // The obvious alternative - start with a guess and double - costs a
        // full copy of a buffer already in the hundreds of megabytes, and
        // holds the old and new arrays alive simultaneously while it does. On
        // a 12M-object heap that is a ~300MB spike to avoid re-running an
        // encode that is pure arithmetic over arrays already in cache.
        blob.Bytes = new byte[MeasureBlobLength(graph, blob.TypeSizes)];
        blob.Length = 0;

        for (int nodeIndex = 0; nodeIndex < graph.NodeCount; ++nodeIndex)
        {
            blob.NodeOffsets[nodeIndex] = blob.Length;

            int typeIndex = graph.NodeTypeIndex[nodeIndex];
            int nodeSize = graph.NodeSize[nodeIndex];
            bool sizeIsExplicit = nodeSize != blob.TypeSizes[typeIndex];

            // Low bit records whether an explicit size follows; the type index
            // occupies the remaining bits.
            int typeAndSize = (typeIndex << 1) | (sizeIsExplicit ? 1 : 0);
            blob.WriteCompressedInt(typeAndSize);

            if (sizeIsExplicit)
            {
                blob.WriteCompressedInt(nodeSize);
            }

            int childStart = graph.ChildStart[nodeIndex];
            int childEnd = graph.ChildStart[nodeIndex + 1];
            blob.WriteCompressedInt(childEnd - childStart);

            for (int edgeIndex = childStart; edgeIndex < childEnd; ++edgeIndex)
            {
                // Children are deltas from the owning node's own index - that
                // is what keeps a reference to a nearby object down to one
                // byte, and it is why this cannot be written as a plain index.
                blob.WriteCompressedInt(graph.ChildTarget[edgeIndex] - nodeIndex);
            }

            if ((nodeIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                ProgressReporter.ReportFraction((double)nodeIndex / graph.NodeCount);
            }
        }

        return blob;
    }

    // Walks exactly what Build writes, adding up encoded lengths instead of
    // emitting bytes. The two must stay in lockstep: an undercount here would
    // overrun the buffer Build then allocates.
    private static long MeasureBlobLength(HeapGraph graph, int[] typeSizes)
    {
        long totalLength = 0;

        for (int nodeIndex = 0; nodeIndex < graph.NodeCount; ++nodeIndex)
        {
            int typeIndex = graph.NodeTypeIndex[nodeIndex];
            int nodeSize = graph.NodeSize[nodeIndex];
            bool sizeIsExplicit = nodeSize != typeSizes[typeIndex];

            totalLength += CompressedIntLength((typeIndex << 1) | (sizeIsExplicit ? 1 : 0));

            if (sizeIsExplicit)
            {
                totalLength += CompressedIntLength(nodeSize);
            }

            int childStart = graph.ChildStart[nodeIndex];
            int childEnd = graph.ChildStart[nodeIndex + 1];
            totalLength += CompressedIntLength(childEnd - childStart);

            for (int edgeIndex = childStart; edgeIndex < childEnd; ++edgeIndex)
            {
                totalLength += CompressedIntLength(graph.ChildTarget[edgeIndex] - nodeIndex);
            }
        }

        return totalLength;
    }

    // Mirrors WriteCompressedInt's own branch ladder exactly.
    private static int CompressedIntLength(int value)
    {
        if (value << 25 >> 25 == value)
        {
            return 1;
        }

        if (value << 18 >> 18 == value)
        {
            return 2;
        }

        if (value << 11 >> 11 == value)
        {
            return 3;
        }

        if (value << 4 >> 4 == value)
        {
            return 4;
        }

        return 5;
    }

    // Each type takes its first instance's size, so the common case (a
    // fixed-size type) writes no per-node size at all. See this file's header
    // for why leaving these at 0 is not an option.
    private static int[] ChooseTypeSizes(HeapGraph graph)
    {
        int[] typeSizes = new int[graph.TypeCount];
        bool[] typeSeen = new bool[graph.TypeCount];

        for (int nodeIndex = 0; nodeIndex < graph.NodeCount; ++nodeIndex)
        {
            int typeIndex = graph.NodeTypeIndex[nodeIndex];

            if (!typeSeen[typeIndex])
            {
                typeSeen[typeIndex] = true;
                typeSizes[typeIndex] = graph.NodeSize[nodeIndex];
            }
        }

        return typeSizes;
    }

    private void EnsureCapacity(int additionalBytes)
    {
        if (this.Length + additionalBytes <= this.Bytes.Length)
        {
            return;
        }

        int newCapacity = this.Bytes.Length * 2;

        while (newCapacity < this.Length + additionalBytes)
        {
            newCapacity *= 2;
        }

        byte[] grown = new byte[newCapacity];
        Array.Copy(this.Bytes, grown, this.Length);
        this.Bytes = grown;
    }

    // The exact inverse of GcDumpReader's ReadCompressedInt: the first byte
    // carries a SIGN-EXTENDED low 7 bits (so a small negative delta fits in
    // one byte), and each further byte contributes 7 more bits with the high
    // bit meaning "another follows". Ported from Graph.cs's WriteCompressedInt.
    public void WriteCompressedInt(int value)
    {
        EnsureCapacity(5);

        unchecked
        {
            if (value << 25 >> 25 == value)
            {
                goto oneByte;
            }

            if (value << 18 >> 18 == value)
            {
                goto twoBytes;
            }

            if (value << 11 >> 11 == value)
            {
                goto threeBytes;
            }

            if (value << 4 >> 4 == value)
            {
                goto fourBytes;
            }

            WriteByte((byte)((value >> 28) | 0x80));
        fourBytes:
            WriteByte((byte)((value >> 21) | 0x80));
        threeBytes:
            WriteByte((byte)((value >> 14) | 0x80));
        twoBytes:
            WriteByte((byte)((value >> 7) | 0x80));
        oneByte:
            WriteByte((byte)(value & 0x7F));
        }
    }

    private void WriteByte(byte value)
    {
        this.Bytes[this.Length] = value;
        ++this.Length;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
