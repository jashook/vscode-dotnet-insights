////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpWriterTests.cs
//
// Notes:
// Round-trips a HeapGraph through GcDumpWriter and back through GcDumpReader.
//
// The reader is already diffed against dotnet-gcdump (GcDumpReaderTests.cs), so
// pinning the writer against the reader pins it, transitively, against an
// independent implementation of the same format - which is the only reason a
// round-trip test is worth anything here. A writer checked only against its own
// reader would happily agree with itself about a wrong format.
//
// These run unconditionally: unlike the ground-truth diffs they need no
// fixture, because the graph under test is constructed here.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.IO;

using DotnetInsights.NetTrace.GcDump;

using Xunit;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class GcDumpWriterTests
{
    // A small graph that still exercises every encoding decision the writer
    // makes: a fixed-size type (so its nodes omit their own size), a
    // variable-size type (so its nodes carry an explicit one), a BACKWARD edge
    // (so a negative child delta is written), and a node with no children.
    private static HeapGraph BuildSampleGraph()
    {
        HeapGraph graph = new HeapGraph();

        graph.TypeCount = 3;
        graph.TypeNames = new string[] { "UNDEFINED", "Fixed.Type", "Variable.Type" };
        graph.TypeSizes = new int[] { 0, 24, 0 };
        graph.TypeModuleNames = new string[] { "", "Fixed.dll", "Variable.dll" };

        graph.NodeCount = 4;
        graph.RootNodeIndex = 0;

        //   0 [root]      -> 1, 2
        //   1 Fixed  (24) -> 3
        //   2 Variable(64)-> 1   (backward edge: negative delta)
        //   3 Variable(40)-> none
        graph.NodeTypeIndex = new int[] { 0, 1, 2, 2 };
        graph.NodeSize = new int[] { 0, 24, 64, 40 };
        graph.NodeAddresses = new ulong[] { 0, 0x1000, 0x2000, 0x3000 };
        graph.ChildStart = new int[] { 0, 2, 3, 4, 4 };
        graph.ChildTarget = new int[] { 1, 2, 3, 1 };
        graph.TotalSize = 24 + 64 + 40;

        return graph;
    }

    [Fact]
    public void GcDumpWriter_WriteToFile_RoundTripsThroughGcDumpReader()
    {
        HeapGraph original = BuildSampleGraph();
        string outputPath = Path.Combine(Path.GetTempPath(), $"nettraceParser-writer-{Guid.NewGuid():N}.gcdump");

        try
        {
            GcDumpWriter.WriteToFile(outputPath, original, new GcDumpMetadata());

            GcDumpReadResult readResult = GcDumpReader.Read(outputPath);
            Assert.True(readResult.Succeeded, readResult.ErrorMessage);

            HeapGraph roundTripped = readResult.File.Graph;

            Assert.Equal(original.NodeCount, roundTripped.NodeCount);
            Assert.Equal(original.RootNodeIndex, roundTripped.RootNodeIndex);
            Assert.Equal(original.TotalSize, roundTripped.TotalSize);
            Assert.Equal(original.TypeCount, roundTripped.TypeCount);

            for (int nodeIndex = 0; nodeIndex < original.NodeCount; ++nodeIndex)
            {
                Assert.Equal(original.NodeTypeIndex[nodeIndex], roundTripped.NodeTypeIndex[nodeIndex]);

                // The size every consumer sees has to survive, whether it was
                // written explicitly or inherited from the type table.
                Assert.Equal(original.NodeSize[nodeIndex], roundTripped.NodeSize[nodeIndex]);

                int originalStart = original.ChildStart[nodeIndex];
                int roundTrippedStart = roundTripped.ChildStart[nodeIndex];

                Assert.Equal(original.ChildCountOf(nodeIndex), roundTripped.ChildCountOf(nodeIndex));

                for (int childIndex = 0; childIndex < original.ChildCountOf(nodeIndex); ++childIndex)
                {
                    Assert.Equal(
                        original.ChildTarget[originalStart + childIndex],
                        roundTripped.ChildTarget[roundTrippedStart + childIndex]);
                }
            }

            for (int typeIndex = 0; typeIndex < original.TypeCount; ++typeIndex)
            {
                Assert.Equal(original.TypeNames[typeIndex], roundTripped.TypeNames[typeIndex]);
            }
        }
        finally
        {
            try
            {
                File.Delete(outputPath);
            }
            catch (IOException)
            {
                // Best effort - a leaked temp file is not worth failing over.
            }
        }
    }

    // The signature and entry-object type name are what every other tool keys
    // off. Getting the type name wrong produces a file this repo's own reader
    // round-trips perfectly and dotnet-gcdump rejects outright, which is
    // exactly the failure this pins (see GcDumpWireTypes.cs).
    [Fact]
    public void GcDumpWriter_WriteToFile_EmitsTheTypeNamesOtherToolsLookUp()
    {
        HeapGraph graph = BuildSampleGraph();
        string outputPath = Path.Combine(Path.GetTempPath(), $"nettraceParser-writer-{Guid.NewGuid():N}.gcdump");

        try
        {
            GcDumpWriter.WriteToFile(outputPath, graph, new GcDumpMetadata());

            byte[] bytes = File.ReadAllBytes(outputPath);
            string asText = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512));

            Assert.Contains("!FastSerialization.1", asText);
            Assert.Contains("GCHeapDump", asText);
            Assert.Contains("Graphs.MemoryGraph", asText);
        }
        finally
        {
            try
            {
                File.Delete(outputPath);
            }
            catch (IOException)
            {
            }
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
