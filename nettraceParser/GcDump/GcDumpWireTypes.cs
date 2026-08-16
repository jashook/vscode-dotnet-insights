////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpWireTypes.cs
//
// Notes:
// The two IFastSerializable objects GcDumpWriter emits.
//
// THEIR NAMES AND NAMESPACES ARE WIRE FORMAT, NOT STYLE. FastSerialization
// writes each object's type as `instance.GetType().FullName` (see
// Serializer.CreateTypeForObject, and SerializationType's FullName), and a
// reader looks its factory up by that exact string. dotnet-gcdump registers
// factories for "GCHeapDump" and "Graphs.MemoryGraph", so these two types must
// have precisely those full names or no other tool can open the file.
//
// This is not theoretical. A first version of this writer used ordinary names
// in this project's own namespace, produced a file this repo's reader
// round-tripped perfectly, and `dotnet-gcdump report` rejected outright:
//
//     An error occured while parsing the file, ... Message: Could not find
//     type DotnetInsights.NetTrace.GcDump.GcHeapDumpWriterEntry
//
// Hence a class in the GLOBAL namespace named GCHeapDump, and one in a
// namespace literally called Graphs named MemoryGraph. They look out of place
// next to the rest of this codebase precisely because they are not really this
// codebase's types - they are the format's.
//
// Both deliberately mirror GcDumpReader.cs's own reader classes field for
// field and in the same order. Any divergence desynchronizes the stream, so
// the two files are meant to be changed together.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Runtime.CompilerServices;

using DotnetInsights.NetTrace.GcDump;
using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Full name must be exactly "GCHeapDump" - hence the global namespace.
internal sealed class GCHeapDump : IFastSerializable, IFastSerializableVersion
{
    private readonly HeapGraph graph;
    private readonly GcDumpMetadata metadata;
    private readonly NodeBlob blob;

    public GCHeapDump(HeapGraph graph, GcDumpMetadata metadata, NodeBlob blob)
    {
        this.graph = graph;
        this.metadata = metadata;
        this.blob = blob;
    }

    public int Version { get { return GcDumpFormat.GcHeapDumpVersion; } }
    public int MinimumVersionCanRead { get { return GcDumpFormat.GcHeapDumpMinimumVersionCanRead; } }
    public int MinimumReaderVersion { get { return GcDumpFormat.GcHeapDumpMinimumReaderVersion; } }

    public void FromStream(Deserializer deserializer)
    {
        throw new NotSupportedException("GcDumpWriter only writes; GcDumpReader reads.");
    }

    public void ToStream(Serializer serializer)
    {
        serializer.Write(new Graphs.MemoryGraph(this.graph, this.blob));

        // Was Is64Bit; still in the stream for compatibility.
        serializer.Write(true);

        // 1.0 both: this is a complete walk, not a sampled one, and claiming
        // otherwise would make every consumer scale the counts it displays.
        serializer.Write(1.0f);
        serializer.Write(1.0f);

        // JSHeapInfo and DotNetHeapInfo. Null is what a reader expects when
        // there is nothing to say - DotNetHeapInfo would carry per-segment
        // generation bounds, which needs the GCGenerationRange events this
        // decoder does not consume.
        serializer.Write((IFastSerializable)null);
        serializer.Write((IFastSerializable)null);

        serializer.Write(this.metadata.CollectionLog ?? "");
        serializer.Write(this.metadata.TimeCollectedTicks);
        serializer.Write(this.metadata.MachineName ?? "");
        serializer.Write(this.metadata.ProcessName ?? "");
        serializer.Write(this.metadata.ProcessId);
        serializer.Write(this.metadata.TotalProcessCommit);
        serializer.Write(this.metadata.TotalProcessWorkingSet);

        // No per-type count multipliers - nothing here is sampled.
        serializer.Write(0);

        // Tagged tail. m_interop is simply absent (readers use TryReadTagged),
        // so the creation tool is the only thing written here - and it is
        // worth writing, because "which tool produced this dump" is the first
        // question asked of a file that looks unusual.
        serializer.WriteTagged("nettraceParser");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

namespace Graphs
{
    // Full name must be exactly "Graphs.MemoryGraph".
    internal sealed class MemoryGraph : IFastSerializable, IFastSerializableVersion
    {
        private readonly HeapGraph graph;
        private readonly NodeBlob blob;

        public MemoryGraph(HeapGraph graph, NodeBlob blob)
        {
            this.graph = graph;
            this.blob = blob;
        }

        public int Version { get { return GcDumpFormat.MemoryGraphVersion; } }
        public int MinimumVersionCanRead { get { return GcDumpFormat.MemoryGraphMinimumVersionCanRead; } }

        // Deliberately 0, matching what dotnet-gcdump writes. Graph.ToStream
        // only emits its trailing deferred region when this is >= 1, so keeping
        // it at 0 means there is no region for a reader to skip - and no chance
        // of this writer and GcDumpReader disagreeing about whether one is
        // present.
        public int MinimumReaderVersion { get { return GcDumpFormat.MemoryGraphMinimumReaderVersion; } }

        public void FromStream(Deserializer deserializer)
        {
            throw new NotSupportedException("GcDumpWriter only writes; GcDumpReader reads.");
        }

        // Writes the node blob EIGHT BYTES AT A TIME instead of one.
        //
        // Serializer exposes no bulk byte write - IStreamWriter has a
        // Read(byte[], int, int) on the reader side but no writer counterpart -
        // and adding one would mean editing vendored FastSerialization source.
        // Widening to long instead needs no such change and cuts the call count
        // by 8x: on a 12M-object heap the blob is ~139MB, so this is ~139
        // million virtual calls through an interface, reduced to ~17 million.
        //
        // This is not a liberty with the format. It is exactly what
        // dotnet-gcdump's own Graph.FromStream does on the READ side, which
        // consumes this same region as `while (8 <= blobCount) ReadInt64()`
        // followed by the leftover bytes.
        //
        // Unsafe.ReadUnaligned gives a NATIVE-endian load to pair with
        // MemoryStreamWriter.Write(long)'s native-endian store, so the bytes
        // land in the same order they were in regardless of the machine's
        // endianness. Reading them as explicitly little-endian would be subtly
        // wrong on a big-endian host, where the store would then reverse them.
        private static void WriteBlobBytes(Serializer serializer, byte[] blobBytes, int blobLength)
        {
            int wholeLongCount = blobLength / sizeof(long);

            for (int longIndex = 0; longIndex < wholeLongCount; ++longIndex)
            {
                serializer.Write(Unsafe.ReadUnaligned<long>(ref blobBytes[longIndex * sizeof(long)]));
            }

            for (int byteIndex = wholeLongCount * sizeof(long); byteIndex < blobLength; ++byteIndex)
            {
                serializer.Write(blobBytes[byteIndex]);
            }
        }

        public void ToStream(Serializer serializer)
        {
            serializer.Write(this.graph.TotalSize);
            serializer.Write(this.graph.RootNodeIndex);

            serializer.Write(this.graph.TypeCount);

            for (int typeIndex = 0; typeIndex < this.graph.TypeCount; ++typeIndex)
            {
                serializer.Write(this.graph.TypeNames[typeIndex] ?? "");
                serializer.Write(this.blob.TypeSizes[typeIndex]);

                string moduleName = this.graph.TypeModuleNames != null ? this.graph.TypeModuleNames[typeIndex] : null;
                serializer.Write(string.IsNullOrEmpty(moduleName) ? null : moduleName);
            }

            serializer.Write(this.graph.NodeCount);

            for (int nodeIndex = 0; nodeIndex < this.graph.NodeCount; ++nodeIndex)
            {
                serializer.Write(this.blob.NodeOffsets[nodeIndex]);
            }

            serializer.Write(this.blob.Length);
            WriteBlobBytes(serializer, this.blob.Bytes, this.blob.Length);

            // One address per node, so a reader that asks for one (PerfView
            // does) does not index past the end of a list shorter than the
            // node count.
            serializer.Write(this.graph.NodeCount);

            for (int nodeIndex = 0; nodeIndex < this.graph.NodeCount; ++nodeIndex)
            {
                ulong address = this.graph.NodeAddresses != null ? this.graph.NodeAddresses[nodeIndex] : 0;
                serializer.Write((long)address);
            }

            serializer.WriteTagged(true);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
