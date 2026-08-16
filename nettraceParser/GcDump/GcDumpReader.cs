////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpReader.cs
//
// Notes:
// Decodes a `.gcdump` file into a HeapGraph. GcDumpFormat.cs is the format
// specification this implements; read it first.
//
// The vendored Deserializer (FastSerialization.cs) drives the outer object
// graph, exactly as it does for `.nettrace`. Every type the file names has to
// have a factory registered or the Deserializer cannot instantiate it, so the
// small metadata objects below exist purely to consume their own bytes
// faithfully - skipping them is not an option, because their payloads sit
// between fields this reader does care about and the stream is positional.
//
// TWO PASSES OVER THE NODE BLOB, on purpose. Building CSR adjacency
// (HeapGraph.cs) needs each node's child count before any child can be
// placed, so pass one decodes type/size/child-count and pass two fills in the
// edges. The alternative - one pass into a doubling GrowableArray - would
// peak at up to 2x the final edge array during a resize, which at 30M edges
// is a 240MB spike and a 120MB copy, to save re-reading a blob that is
// already in memory and sequential. Two cheap passes beat one expensive one
// here.
//
// The blob and the node offset table are both released before this returns.
// They are only inputs to the decode; nothing downstream needs them, and at
// 10M nodes they are ~250MB and ~40MB respectively.
//
// This file returns errors on the stack (GcDumpReadResult) rather than
// throwing. A malformed or truncated .gcdump is an ordinary, expected input
// condition - the VS Code extension hands this tool whatever the user
// double-clicked - not an exceptional one.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.IO;

using DotnetInsights.NetTrace.Progress;
using FastSerialization;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public readonly struct GcDumpReadResult
{
    public readonly GcDumpFile File;
    public readonly string ErrorMessage;

    private GcDumpReadResult(GcDumpFile file, string errorMessage)
    {
        this.File = file;
        this.ErrorMessage = errorMessage;
    }

    public bool Succeeded
    {
        get
        {
            return this.ErrorMessage == null;
        }
    }

    public static GcDumpReadResult Success(GcDumpFile file)
    {
        return new GcDumpReadResult(file, null);
    }

    public static GcDumpReadResult Failure(string errorMessage)
    {
        return new GcDumpReadResult(null, errorMessage);
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpReader
{
    // Matches the .nettrace read path's own buffer (see CLAUDE.md's note on
    // the 4MB read-buffer win) - the access pattern here is the same: one
    // long forward scan over a file far larger than any buffer.
    private const int ReadBufferBytes = 4 * 1024 * 1024;

    public static GcDumpReadResult Read(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return GcDumpReadResult.Failure($"File not found: {filePath}");
        }

        try
        {
            using (FileStream inputStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Unlike .nettrace there is no magic prefix to slice past -
                // the FastSerialization signature is at offset 0 (see
                // GcDumpFormat.cs), so the reader starts at the real start of
                // the file and IOStreamStreamReader's own positionInStream
                // counter agrees with the file's actual offsets.
                IOStreamStreamReader streamReader = new IOStreamStreamReader(inputStream, GcDumpFormat.ReaderSettings, ReadBufferBytes);

                using (Deserializer deserializer = new Deserializer(streamReader, filePath))
                {
                    GcHeapDumpReader entryReader = new GcHeapDumpReader();

                    deserializer.RegisterFactory(GcDumpFormat.GcHeapDumpTypeName, delegate { return entryReader; });
                    deserializer.RegisterFactory(GcDumpFormat.MemoryGraphTypeName, delegate { return new MemoryGraphReader(); });
                    deserializer.RegisterFactory(GcDumpFormat.JsHeapInfoTypeName, delegate { return new JsHeapInfoReader(); });
                    deserializer.RegisterFactory(GcDumpFormat.DotNetHeapInfoTypeName, delegate { return new DotNetHeapInfoReader(); });
                    deserializer.RegisterFactory(GcDumpFormat.GcHeapDumpSegmentTypeName, delegate { return new GcHeapDumpSegmentReader(); });
                    deserializer.RegisterFactory(GcDumpFormat.InteropInfoTypeName, delegate { return new InteropInfoReader(); });
                    deserializer.RegisterFactory(GcDumpFormat.ModuleTypeName, delegate { return new ModuleReader(); });

                    // GetEntryObject() rather than the eager
                    // allowLazyDeserialization=false loop, for the same
                    // reason NettraceFile.Read uses it (see CLAUDE.md's
                    // "Stream terminator" note). Here there is an additional
                    // reason: everything this reader wants lives inside the
                    // entry object, so there is no trailing object loop to
                    // run at all.
                    deserializer.GetEntryObject();

                    if (entryReader.Graph == null)
                    {
                        return GcDumpReadResult.Failure("No MemoryGraph was found in this .gcdump file.");
                    }

                    if (entryReader.ErrorMessage != null)
                    {
                        return GcDumpReadResult.Failure(entryReader.ErrorMessage);
                    }

                    GcDumpFile file = new GcDumpFile();
                    file.Graph = entryReader.Graph;
                    file.Metadata = entryReader.Metadata;

                    return GcDumpReadResult.Success(file);
                }
            }
        }
        catch (Exception ex)
        {
            // The Deserializer itself throws on a malformed stream and is
            // vendored source this repo does not want to fork, so its
            // exceptions are converted to an error return right at the
            // boundary rather than propagated to callers.
            return GcDumpReadResult.Failure($"Failed to read '{filePath}' as a .gcdump file: {ex.Message}");
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// The entry object. Field order is GCHeapDump.FromStream's, exactly.
internal sealed class GcHeapDumpReader : IFastSerializable, IFastSerializableVersion
{
    public HeapGraph Graph;
    public GcDumpMetadata Metadata = new GcDumpMetadata();
    public string ErrorMessage;

    public int Version { get { return GcDumpFormat.GcHeapDumpVersion; } }
    public int MinimumVersionCanRead { get { return GcDumpFormat.GcHeapDumpMinimumVersionCanRead; } }
    public int MinimumReaderVersion { get { return GcDumpFormat.GcHeapDumpMinimumReaderVersion; } }

    public void ToStream(Serializer serializer)
    {
        throw new NotSupportedException("nettraceParser reads .gcdump files; it does not write them.");
    }

    public void FromStream(Deserializer deserializer)
    {
        // Versions below 8 used a completely different layout, and version 8
        // itself is rejected by dotnet-gcdump's own reader. Neither is worth
        // carrying: every file dotnet-gcdump has produced for years is 9+.
        if (deserializer.VersionBeingRead < 9)
        {
            this.ErrorMessage = $"Unsupported .gcdump version {deserializer.VersionBeingRead}; this reader supports version 9 and later.";
            return;
        }

        MemoryGraphReader graphReader = (MemoryGraphReader)deserializer.ReadObject();

        if (graphReader == null)
        {
            return;
        }

        if (graphReader.ErrorMessage != null)
        {
            this.ErrorMessage = graphReader.ErrorMessage;
            return;
        }

        this.Graph = graphReader.Graph;

        // Was Is64Bit; kept in the stream for compatibility and ignored by
        // the writer's own reader too.
        deserializer.ReadBool();

        this.Metadata.AverageCountMultiplier = deserializer.ReadFloat();
        this.Metadata.AverageSizeMultiplier = deserializer.ReadFloat();

        // Both are read for their positional effect on the stream; neither
        // carries anything this tool renders.
        deserializer.ReadObject();
        deserializer.ReadObject();

        deserializer.ReadString();
        this.Metadata.TimeCollectedTicks = deserializer.ReadInt64();
        this.Metadata.MachineName = deserializer.ReadString();
        this.Metadata.ProcessName = deserializer.ReadString();
        this.Metadata.ProcessId = deserializer.ReadInt();
        this.Metadata.TotalProcessCommit = deserializer.ReadInt64();
        this.Metadata.TotalProcessWorkingSet = deserializer.ReadInt64();

        int countMultiplierCount = deserializer.ReadInt();

        for (int multiplierIndex = 0; multiplierIndex < countMultiplierCount; ++multiplierIndex)
        {
            deserializer.ReadFloat();
        }

        // Everything past here is tagged, so a reader may simply stop. The
        // creation tool is worth one more read: it distinguishes a
        // dotnet-gcdump capture from a PerfView one, which is exactly the
        // context needed if a file ever does turn out to use a layout this
        // reader rejects.
        InteropInfoReader interopInfo = null;
        deserializer.TryReadTagged(ref interopInfo);

        string creationTool = null;
        deserializer.TryReadTagged(ref creationTool);
        this.Metadata.CreationTool = creationTool;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// Graph.FromStream followed by MemoryGraph.FromStream's own trailing fields.
internal sealed class MemoryGraphReader : IFastSerializable, IFastSerializableVersion
{
    public HeapGraph Graph;
    public string ErrorMessage;

    public int Version { get { return GcDumpFormat.MemoryGraphVersion; } }
    public int MinimumVersionCanRead { get { return GcDumpFormat.MemoryGraphMinimumVersionCanRead; } }
    public int MinimumReaderVersion { get { return GcDumpFormat.MemoryGraphMinimumReaderVersion; } }

    public void ToStream(Serializer serializer)
    {
        throw new NotSupportedException("nettraceParser reads .gcdump files; it does not write them.");
    }

    public void FromStream(Deserializer deserializer)
    {
        HeapGraph graph = new HeapGraph();

        graph.TotalSize = deserializer.ReadInt64();
        graph.RootNodeIndex = deserializer.ReadInt();

        int typeCount = deserializer.ReadInt();

        if (typeCount < 0)
        {
            this.ErrorMessage = $"Invalid .gcdump type count: {typeCount}.";
            return;
        }

        graph.TypeCount = typeCount;
        graph.TypeNames = new string[typeCount];
        graph.TypeSizes = new int[typeCount];
        graph.TypeModuleNames = new string[typeCount];

        for (int typeIndex = 0; typeIndex < typeCount; ++typeIndex)
        {
            graph.TypeNames[typeIndex] = deserializer.ReadString();
            graph.TypeSizes[typeIndex] = deserializer.ReadInt();
            graph.TypeModuleNames[typeIndex] = deserializer.ReadString();
        }

        int nodeCount = deserializer.ReadInt();

        if (nodeCount < 0)
        {
            this.ErrorMessage = $"Invalid .gcdump node count: {nodeCount}.";
            return;
        }

        graph.NodeCount = nodeCount;

        // Each node's byte offset into the blob. Needed only to decode; not
        // retained past this method.
        int[] nodeOffsets = new int[nodeCount];

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            nodeOffsets[nodeIndex] = deserializer.ReadInt();
        }

        int blobLength = deserializer.ReadInt();

        if (blobLength < 0)
        {
            this.ErrorMessage = $"Invalid .gcdump node blob length: {blobLength}.";
            return;
        }

        byte[] blob = new byte[blobLength];
        deserializer.Read(blob, 0, blobLength);

        string decodeError = DecodeNodes(graph, nodeOffsets, blob);

        if (decodeError != null)
        {
            this.ErrorMessage = decodeError;
            return;
        }

        // Released before the analyses run - see this file's header.
        nodeOffsets = null;
        blob = null;

        // Graph's trailing deferred region, present only when the file
        // declares it (see GcDumpFormat.cs). Every dotnet-gcdump file
        // declares MinimumReaderVersion 0 and therefore has none, but
        // honoring the condition costs one comparison and keeps this correct
        // for a PerfView-written file.
        if (1 <= deserializer.MinimumReaderVersionBeingRead)
        {
            ForwardReference endOfRegion = deserializer.ReadForwardReference();
            deserializer.Goto(endOfRegion);
        }

        // MemoryGraph's own fields: one address per node. Read past rather
        // than retained - none of the four analyses need object addresses,
        // and at 10M nodes keeping them would cost 80MB for nothing.
        int addressCount = deserializer.ReadInt();

        for (int addressIndex = 0; addressIndex < addressCount; ++addressIndex)
        {
            deserializer.ReadInt64();
        }

        bool is64Bit = false;
        deserializer.TryReadTagged(ref is64Bit);

        this.Graph = graph;
    }

    // Pass one fills NodeTypeIndex/NodeSize and counts children into
    // ChildStart; pass two fills ChildTarget. See this file's header for why
    // it is split this way.
    private static string DecodeNodes(HeapGraph graph, int[] nodeOffsets, byte[] blob)
    {
        int nodeCount = graph.NodeCount;

        graph.NodeTypeIndex = new int[nodeCount];
        graph.NodeSize = new int[nodeCount];
        graph.ChildStart = new int[nodeCount + 1];

        long totalEdges = 0;

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            int cursor = nodeOffsets[nodeIndex];

            if (cursor < 0 || cursor >= blob.Length)
            {
                return $"Node {nodeIndex} has an out-of-range blob offset ({cursor}).";
            }

            int typeAndSize = ReadCompressedInt(blob, ref cursor);
            int nodeSize;

            if ((typeAndSize & 1) != 0)
            {
                // Low bit set: an explicit size follows. Used for the
                // variable-length types (arrays, strings) whose type table
                // entry carries a size of 0.
                nodeSize = ReadCompressedInt(blob, ref cursor);
            }
            else
            {
                nodeSize = 0;
            }

            int typeIndex = typeAndSize >> 1;

            if (typeIndex < 0 || typeIndex >= graph.TypeCount)
            {
                return $"Node {nodeIndex} refers to type index {typeIndex}, which is outside the {graph.TypeCount}-entry type table.";
            }

            if ((typeAndSize & 1) == 0)
            {
                nodeSize = graph.TypeSizes[typeIndex];
            }

            graph.NodeTypeIndex[nodeIndex] = typeIndex;
            graph.NodeSize[nodeIndex] = nodeSize;

            int childCount = ReadCompressedInt(blob, ref cursor);

            if (childCount < 0)
            {
                return $"Node {nodeIndex} declares a negative child count ({childCount}).";
            }

            graph.ChildStart[nodeIndex + 1] = childCount;
            totalEdges += childCount;

            if ((nodeIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                ProgressReporter.ReportFraction((double)nodeIndex / nodeCount * 0.5);
            }
        }

        if (totalEdges > int.MaxValue)
        {
            return $"This .gcdump has {totalEdges} references, more than this reader's {int.MaxValue} limit.";
        }

        // Prefix sum turns the per-node counts written above into CSR start
        // offsets, in place.
        int runningStart = 0;

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            int childCount = graph.ChildStart[nodeIndex + 1];
            graph.ChildStart[nodeIndex] = runningStart;
            runningStart += childCount;
        }

        graph.ChildStart[nodeCount] = runningStart;
        graph.ChildTarget = new int[runningStart];

        for (int nodeIndex = 0; nodeIndex < nodeCount; ++nodeIndex)
        {
            int cursor = nodeOffsets[nodeIndex];

            int typeAndSize = ReadCompressedInt(blob, ref cursor);

            if ((typeAndSize & 1) != 0)
            {
                ReadCompressedInt(blob, ref cursor);
            }

            int childCount = ReadCompressedInt(blob, ref cursor);
            int writeAt = graph.ChildStart[nodeIndex];

            for (int childIndex = 0; childIndex < childCount; ++childIndex)
            {
                // Children are stored as a delta from the OWNING node's
                // index, not as absolute indices - that is what keeps a
                // reference to a nearby object down to a single byte.
                int target = ReadCompressedInt(blob, ref cursor) + nodeIndex;

                if (target < 0 || target >= nodeCount)
                {
                    return $"Node {nodeIndex} references node {target}, which is outside the {nodeCount}-node graph.";
                }

                graph.ChildTarget[writeAt] = target;
                ++writeAt;
            }

            if ((nodeIndex & ProgressReporter.IndexProgressMask) == 0)
            {
                ProgressReporter.ReportFraction(0.5 + ((double)nodeIndex / nodeCount * 0.5));
            }
        }

        return null;
    }

    // The blob's variable-length integer encoding, ported from Graph.cs's own
    // ReadCompressedInt. Big-endian-ish and sign-extending: the first byte's
    // low 7 bits are sign-extended from bit 6 (`value << 25 >> 25`), which is
    // what lets a child delta be negative in one byte. Subsequent bytes
    // contribute 7 bits each, high bit set meaning "another byte follows".
    // Reads straight out of the blob array rather than through a stream -
    // this runs once per node plus once per edge, tens of millions of times.
    private static int ReadCompressedInt(byte[] blob, ref int cursor)
    {
        byte currentByte = blob[cursor];
        ++cursor;

        int value = currentByte << 25 >> 25;

        if ((currentByte & 0x80) == 0)
        {
            return value;
        }

        for (int byteIndex = 0; byteIndex < 3; ++byteIndex)
        {
            value <<= 7;
            currentByte = blob[cursor];
            ++cursor;
            value += currentByte & 0x7F;

            if ((currentByte & 0x80) == 0)
            {
                return value;
            }
        }

        value <<= 7;
        currentByte = blob[cursor];
        ++cursor;
        value += currentByte;

        return value;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// The remaining objects exist so the Deserializer can instantiate what the
// file names, and so each consumes exactly its own bytes. None of them carry
// anything rendered today.

internal sealed class JsHeapInfoReader : IFastSerializable
{
    public void ToStream(Serializer serializer)
    {
        throw new NotSupportedException("nettraceParser reads .gcdump files; it does not write them.");
    }

    // Genuinely empty in the format - JSHeapInfo writes no payload at all.
    public void FromStream(Deserializer deserializer)
    {
    }
}

internal sealed class DotNetHeapInfoReader : IFastSerializable
{
    public void ToStream(Serializer serializer)
    {
        throw new NotSupportedException("nettraceParser reads .gcdump files; it does not write them.");
    }

    public void FromStream(Deserializer deserializer)
    {
        deserializer.ReadInt64();

        int segmentCount = deserializer.ReadInt();

        for (int segmentIndex = 0; segmentIndex < segmentCount; ++segmentIndex)
        {
            deserializer.ReadObject();
        }
    }
}

internal sealed class GcHeapDumpSegmentReader : IFastSerializable, IFastSerializableVersion
{
    public int Version { get { return GcDumpFormat.GcHeapDumpSegmentVersion; } }
    public int MinimumVersionCanRead { get { return 0; } }
    public int MinimumReaderVersion { get { return 0; } }

    public void ToStream(Serializer serializer)
    {
        throw new NotSupportedException("nettraceParser reads .gcdump files; it does not write them.");
    }

    public void FromStream(Deserializer deserializer)
    {
        // Start, End, Gen0End, Gen1End, Gen2End, Gen3End.
        for (int fieldIndex = 0; fieldIndex < 6; ++fieldIndex)
        {
            deserializer.ReadInt64();
        }

        // Gen4End (the pinned object heap) only exists from version 1 on.
        if (deserializer.VersionBeingRead >= 1)
        {
            deserializer.ReadInt64();
        }
    }
}

internal sealed class ModuleReader : IFastSerializable
{
    public void ToStream(Serializer serializer)
    {
        throw new NotSupportedException("nettraceParser reads .gcdump files; it does not write them.");
    }

    public void FromStream(Deserializer deserializer)
    {
        deserializer.ReadString();
        deserializer.ReadInt64();
        deserializer.ReadInt();
        deserializer.ReadInt64();
        deserializer.ReadString();
        deserializer.ReadString();
        deserializer.ReadInt();
    }
}

internal sealed class InteropInfoReader : IFastSerializable
{
    public void ToStream(Serializer serializer)
    {
        throw new NotSupportedException("nettraceParser reads .gcdump files; it does not write them.");
    }

    public void FromStream(Deserializer deserializer)
    {
        int totalWrapperCount = deserializer.ReadInt();

        if (totalWrapperCount == 0)
        {
            return;
        }

        int rcwCount = deserializer.ReadInt();
        int ccwCount = deserializer.ReadInt();
        int interfaceCount = deserializer.ReadInt();
        int moduleCount = deserializer.ReadInt();

        for (int rcwIndex = 0; rcwIndex < rcwCount; ++rcwIndex)
        {
            deserializer.ReadInt();
            deserializer.ReadInt();
            deserializer.ReadInt64();
            deserializer.ReadInt64();
            deserializer.ReadInt64();
            deserializer.ReadInt();
            deserializer.ReadInt();
        }

        for (int ccwIndex = 0; ccwIndex < ccwCount; ++ccwIndex)
        {
            deserializer.ReadInt();
            deserializer.ReadInt();
            deserializer.ReadInt64();
            deserializer.ReadInt64();
            deserializer.ReadInt();
            deserializer.ReadInt();
        }

        for (int interfaceIndex = 0; interfaceIndex < interfaceCount; ++interfaceIndex)
        {
            deserializer.ReadByte();
            deserializer.ReadInt();
            deserializer.ReadInt();
            deserializer.ReadInt64();
            deserializer.ReadInt64();
            deserializer.ReadInt64();
        }

        for (int moduleIndex = 0; moduleIndex < moduleCount; ++moduleIndex)
        {
            deserializer.ReadInt64();
            deserializer.ReadInt();
            deserializer.ReadInt();
            deserializer.ReadString();
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
