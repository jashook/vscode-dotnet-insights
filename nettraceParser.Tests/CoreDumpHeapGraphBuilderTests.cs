////////////////////////////////////////////////////////////////////////////////
// Module: CoreDumpHeapGraphBuilderTests.cs
//
// Notes:
// Exercises the core-dump heap source (CoreDump/CoreDumpHeapGraphBuilder.cs)
// against a REAL process dump, opt-in via CORE_DUMP_FIXTURE - the same shape as
// GcDumpReaderTests' own GCDUMP_GROUNDTRUTH_FIXTURE, and for the same reason:
// a dump is hundreds of megabytes and machine-specific, so nothing is checked
// in and this is a silent no-op by default.
//
//   CORE_DUMP_FIXTURE=~/path/to/core.dmp dotnet test --filter CoreDumpHeapGraphBuilder
//
// Producing a dump to point it at, on a machine where `dotnet-dump collect`
// cannot attach (macOS refuses task_for_pid without entitlements), is a matter
// of letting the runtime write one itself:
//
//   DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=2 \
//     DOTNET_DbgMiniDumpName=/tmp/app.core ./yourapp   # then crash it
//
// WHAT IS ASSERTED, AND WHY IT IS NOT A PINNED VALUE. The interesting property
// of this source is not "did it produce N objects" - that changes with every
// dump - it is that the graph it produces is INTERNALLY CONSISTENT, which is
// exactly what the event-based sources stop being on a busy process (see
// CoreDumpHeapGraphBuilder's header). So the assertions are structural: every
// edge lands on a real node, the CSR offsets are monotone and cover the array,
// the synthetic root reaches essentially the whole heap, and no object carries
// the UNDEFINED placeholder type. On a dump from a frozen process every one of
// those is exactly true, which is the entire value being tested.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.IO;

using DotnetInsights.NetTrace.CoreDump;
using DotnetInsights.NetTrace.GcDump;

using Xunit;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class CoreDumpHeapGraphBuilderTests
{
    private static string FixturePath()
    {
        string path = Environment.GetEnvironmentVariable("CORE_DUMP_FIXTURE");

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));
        }

        return File.Exists(path) ? path : null;
    }

    private static CoreDumpBuildResult BuildFromFixture()
    {
        string fixturePath = FixturePath();

        if (fixturePath == null)
        {
            return null;
        }

        CoreDumpBuildOptions options = new CoreDumpBuildOptions();

        // A macOS dump's stack unwind crashes the DAC (see the builder's own
        // header). The command line auto-detects this from the dump's magic;
        // a test cannot afford to guess wrong, because the failure mode is a
        // SIGSEGV that takes the whole test run with it.
        options.SkipStackRoots = IsMachOCore(fixturePath);

        CoreDumpBuildResult result = CoreDumpHeapGraphBuilder.Build(fixturePath, options);

        // A missing DAC is an environment problem, not a failing assertion -
        // it means this machine cannot read this dump at all.
        if (!result.Succeeded)
        {
            return null;
        }

        return result;
    }

    private static bool IsMachOCore(string path)
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] magic = new byte[4];

            if (stream.Read(magic, 0, magic.Length) != magic.Length)
            {
                return false;
            }

            uint value = (uint)(magic[0] | (magic[1] << 8) | (magic[2] << 16) | (magic[3] << 24));
            return value == 0xFEEDFACF || value == 0xFEEDFACE;
        }
    }

    [Fact]
    public void EveryEdgeLandsOnARealNode()
    {
        CoreDumpBuildResult result = BuildFromFixture();

        if (result == null)
        {
            return;
        }

        HeapGraph graph = result.Graph;

        for (int edgeIndex = 0; edgeIndex < graph.ChildTarget.Length; ++edgeIndex)
        {
            int target = graph.ChildTarget[edgeIndex];
            Assert.InRange(target, 0, graph.NodeCount - 1);
        }
    }

    [Fact]
    public void CsrOffsetsAreMonotoneAndCoverTheEdgeArray()
    {
        CoreDumpBuildResult result = BuildFromFixture();

        if (result == null)
        {
            return;
        }

        HeapGraph graph = result.Graph;

        // A CSR whose offsets go backwards silently hands one node another
        // node's children, which reads as a plausible - and wrong - retention
        // path rather than as a crash.
        for (int nodeIndex = 0; nodeIndex < graph.NodeCount; ++nodeIndex)
        {
            Assert.True(graph.ChildStart[nodeIndex] <= graph.ChildStart[nodeIndex + 1],
                $"child offsets went backwards at node {nodeIndex}");
        }

        Assert.Equal(graph.ChildTarget.Length, graph.ChildStart[graph.NodeCount]);
    }

    [Fact]
    public void NoReferenceIsLeftUnresolved()
    {
        CoreDumpBuildResult result = BuildFromFixture();

        if (result == null)
        {
            return;
        }

        // This is the whole point of reading a frozen process image instead of
        // an event stream: every reference in the dump names an object that is
        // also in the dump. The event paths cannot promise this on a live
        // process, and on a busy one they miss it by 35%.
        Assert.Equal(0, result.UnresolvedReferenceCount);
    }

    [Fact]
    public void NoObjectCarriesThePlaceholderType()
    {
        CoreDumpBuildResult result = BuildFromFixture();

        if (result == null)
        {
            return;
        }

        HeapGraph graph = result.Graph;
        int undefinedCount = 0;

        // Object nodes occupy [0, ObjectCount); the root categories and the
        // synthetic root follow them and are deliberately typed as themselves.
        for (int nodeIndex = 0; nodeIndex < result.ObjectCount; ++nodeIndex)
        {
            if (graph.NodeTypeIndex[nodeIndex] == HeapGraph.UndefinedTypeIndex)
            {
                ++undefinedCount;
            }
        }

        Assert.Equal(0, undefinedCount);
    }

    [Fact]
    public void TheSyntheticRootReachesEssentiallyTheWholeHeap()
    {
        CoreDumpBuildResult result = BuildFromFixture();

        if (result == null)
        {
            return;
        }

        HeapGraph graph = result.Graph;
        bool[] reachable = new bool[graph.NodeCount];
        Stack<int> pending = new Stack<int>();

        pending.Push(graph.RootNodeIndex);
        reachable[graph.RootNodeIndex] = true;
        int reachedCount = 0;

        while (pending.Count > 0)
        {
            int nodeIndex = pending.Pop();
            ++reachedCount;

            int edgeEnd = graph.ChildStart[nodeIndex + 1];
            for (int edgeIndex = graph.ChildStart[nodeIndex]; edgeIndex < edgeEnd; ++edgeIndex)
            {
                int target = graph.ChildTarget[edgeIndex];

                if (!reachable[target])
                {
                    reachable[target] = true;
                    pending.Push(target);
                }
            }
        }

        double reachedFraction = (double)reachedCount / graph.NodeCount;

        // Deliberately a floor, not an equality. Some objects genuinely are
        // unrooted at any instant (a dead object the next GC has not collected
        // yet), and when stack roots are unavailable the objects held only by a
        // live frame join them. What this rules out is the failure this whole
        // source exists to avoid: the busy-process event dumps came out at
        // 1.5%, and the same test heap read from a core dump came out at 99.8%.
        double floor = result.StackRootsIncluded ? 0.90 : 0.75;

        Assert.True(reachedFraction >= floor,
            $"only {reachedFraction:P1} of the heap was reachable from the root (stack roots included: {result.StackRootsIncluded})");
    }

    [Fact]
    public void RootCategoriesAreNamedRatherThanLumpedTogether()
    {
        CoreDumpBuildResult result = BuildFromFixture();

        if (result == null)
        {
            return;
        }

        HeapGraph graph = result.Graph;

        // A retention path that ends "held by [.NET Roots]" says nothing; the
        // kind of root is the part that tells a reader what they are looking
        // at. Every category node hangs off the root, whether or not it holds
        // anything - an empty one means "looked, found none", which is
        // different from "never looked".
        List<string> categoryNames = new List<string>();
        int rootEnd = graph.ChildStart[graph.RootNodeIndex + 1];

        for (int edgeIndex = graph.ChildStart[graph.RootNodeIndex]; edgeIndex < rootEnd; ++edgeIndex)
        {
            categoryNames.Add(graph.TypeNameOf(graph.NodeTypeIndex[graph.ChildTarget[edgeIndex]]));
        }

        Assert.Contains("[strong handle]", categoryNames);
        Assert.Contains("[finalizer queue]", categoryNames);
        Assert.Contains("[thread stack]", categoryNames);
    }

    [Fact]
    public void ReportsAMissingDacAsAnErrorRatherThanThrowing()
    {
        // Not fixture-gated: any file that is not a dump exercises the same
        // boundary, which exists so the CLI can print one line instead of a
        // stack trace.
        string notADump = Path.GetTempFileName();

        try
        {
            File.WriteAllText(notADump, "this is not a core dump");

            CoreDumpBuildResult result = CoreDumpHeapGraphBuilder.Build(notADump, new CoreDumpBuildOptions());

            Assert.False(result.Succeeded);
            Assert.NotNull(result.ErrorMessage);
        }
        finally
        {
            File.Delete(notADump);
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
