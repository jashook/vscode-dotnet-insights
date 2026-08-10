////////////////////////////////////////////////////////////////////////////////
// Module: CpuProfileJsonExporterTests.cs
//
// Notes:
// The non-obvious rules CpuProfileJsonExporter.Write actually needs to guard
// against regressing: the flame tree is built ROOT-to-leaf even though
// SampleEvent.Stack/frameIds are leaf-first (the one place this inverts
// Gc/AllocationJsonExporter.cs's own BuildCallerTree direction - see this
// file's own header comment), and a recursive method must only count once
// per sample toward its own inclusive ("totalSamples") count in the hot
// methods table, never once per stack frame it occupies.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text.Json;

using DotnetInsights.NetTrace.Cpu;
using DotnetInsights.NetTrace.Rundown;

using Xunit;

namespace DotnetInsights.NetTrace.Tests {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class CpuProfileJsonExporterTests
{
    private static byte[] MakeMethodDCStartVerbosePayload(long methodId, long moduleId, long methodStartAddress, int methodSize, string methodName)
    {
        return new PayloadBuilder()
            .WriteAddress(methodId, 8)
            .WriteAddress(moduleId, 8)
            .WriteAddress(methodStartAddress, 8)
            .WriteInt32(methodSize)
            .WriteInt32(0x06000001)
            .WriteInt32(0)
            .WriteUnicodeString("")
            .WriteUnicodeString(methodName)
            .WriteUnicodeString("sig")
            .ToArray();
    }

    // A rundown (DCStartVerbose) record, valid for the whole capture - same
    // helper shape as MethodSymbolTableTests.cs's own MakeRundownEvent.
    private static EventRecord MakeRundownEvent(long methodId, long startAddress, int size, string name)
    {
        byte[] payload = MakeMethodDCStartVerbosePayload(methodId, moduleId: 2, startAddress, size, name);

        return new EventRecord("Microsoft-Windows-DotNETRuntimeRundown", eventName: null, ClrRundownEventIds.MethodDCStartVerbose, version: 1, timeStampRelativeQpc: 0, threadId: 0, stack: Array.Empty<long>(), fields: null, payload, payloadOffset: 0, payload.Length);
    }

    // Builds a MethodSymbolTable resolving three distinct, non-overlapping
    // 0x100-byte address ranges to "MethodA"/"MethodB"/"MethodC" - enough
    // for every test below to construct readable synthetic stacks.
    private static MethodSymbolTable MakeSymbolTable()
    {
        List<EventRecord> events = new List<EventRecord>
        {
            MakeRundownEvent(methodId: 1, startAddress: 0x1000, size: 0x100, name: "MethodA"),
            MakeRundownEvent(methodId: 2, startAddress: 0x2000, size: 0x100, name: "MethodB"),
            MakeRundownEvent(methodId: 3, startAddress: 0x3000, size: 0x100, name: "MethodC"),
        };

        return MethodSymbolTable.Build(events, pointerSize: 8, qpcFrequency: 0, referenceQpc: 0);
    }

    private static JsonDocument WriteAndParse(List<SampleEvent> sampleEvents, MethodSymbolTable symbolTable)
    {
        using System.IO.MemoryStream stream = new System.IO.MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(stream))
        {
            CpuProfileJsonExporter.Write(writer, sampleEvents, symbolTable);
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    [Fact]
    public void Write_EmptyCapture_WritesZeroedShapeNotAnError()
    {
        JsonDocument document = WriteAndParse(new List<SampleEvent>(), MakeSymbolTable());
        JsonElement root = document.RootElement;

        Assert.Equal(0, root.GetProperty("totalSampleCount").GetInt32());
        Assert.Equal(0, root.GetProperty("hotMethods").GetArrayLength());
        Assert.Equal(0, root.GetProperty("flameTree").GetProperty("totalSamples").GetInt64());
    }

    // Every sample shares the same [leaf=MethodA, caller=MethodB] stack -
    // MethodA is the leaf (index 0), so it should get 3 self samples; both
    // methods should show 3 inclusive (total) samples since each appears
    // exactly once per sample.
    [Fact]
    public void Write_RanksHotMethodsBySelfSamplesLeafFirst()
    {
        long[] stack = new long[] { 0x1010, 0x2010 }; // leaf-first: MethodA, then caller MethodB
        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(0.0, threadId: 1, stack),
            new SampleEvent(1.0, threadId: 1, stack),
            new SampleEvent(2.0, threadId: 1, stack),
        };

        JsonDocument document = WriteAndParse(sampleEvents, MakeSymbolTable());
        JsonElement root = document.RootElement;

        Assert.Equal(3, root.GetProperty("totalSampleCount").GetInt32());

        JsonElement methodNames = root.GetProperty("methodNames");
        JsonElement hotMethods = root.GetProperty("hotMethods");
        Assert.Equal(2, hotMethods.GetArrayLength());

        JsonElement topMethod = hotMethods[0];
        Assert.Equal("MethodA", methodNames[topMethod.GetProperty("frame").GetInt32()].GetString());
        Assert.Equal(3, topMethod.GetProperty("selfSamples").GetInt32());
        Assert.Equal(3, topMethod.GetProperty("totalSamples").GetInt32());

        JsonElement callerMethod = hotMethods[1];
        Assert.Equal("MethodB", methodNames[callerMethod.GetProperty("frame").GetInt32()].GetString());
        Assert.Equal(0, callerMethod.GetProperty("selfSamples").GetInt32());
        Assert.Equal(3, callerMethod.GetProperty("totalSamples").GetInt32());
    }

    // frameIds are leaf-first ([MethodA, MethodB, MethodC] == A called by B
    // called by C) - the flame tree must fold them in REVERSE so the tree
    // reads root-to-leaf (C -> B -> A), the opposite direction from
    // Gc/AllocationJsonExporter.cs's own leaf-first caller tree.
    [Fact]
    public void Write_FlameTreeIsRootToLeafNotLeafFirst()
    {
        long[] stack = new long[] { 0x1010, 0x2010, 0x3010 }; // leaf-first: A, B, C
        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(0.0, threadId: 1, stack),
        };

        JsonDocument document = WriteAndParse(sampleEvents, MakeSymbolTable());
        JsonElement methodNames = document.RootElement.GetProperty("methodNames");
        JsonElement flameTree = document.RootElement.GetProperty("flameTree");

        // Root itself is synthetic (frame == -1, not written into
        // methodNames) - its one child should be the OUTERMOST frame
        // (MethodC), not the leaf (MethodA).
        JsonElement rootChildren = flameTree.GetProperty("children");
        Assert.Equal(1, rootChildren.GetArrayLength());

        JsonElement outermost = rootChildren[0];
        Assert.Equal("MethodC", methodNames[outermost.GetProperty("frame").GetInt32()].GetString());

        JsonElement middle = outermost.GetProperty("children")[0];
        Assert.Equal("MethodB", methodNames[middle.GetProperty("frame").GetInt32()].GetString());

        JsonElement leaf = middle.GetProperty("children")[0];
        Assert.Equal("MethodA", methodNames[leaf.GetProperty("frame").GetInt32()].GetString());
        Assert.Equal(0, leaf.GetProperty("children").GetArrayLength());
    }

    // A recursive stack ([leaf=MethodA, caller=MethodA, caller=MethodB]) -
    // MethodA occupies two frames in the SAME sample. Its inclusive
    // (totalSamples) count must still only increment once for this one
    // sample, not twice - PerfView's own "By Name" semantics, and the
    // specific bug this dedup exists to avoid (see this file's own header
    // comment).
    [Fact]
    public void Write_DedupesRecursiveFramesWithinOneSampleForInclusiveCount()
    {
        long[] stack = new long[] { 0x1010, 0x1020, 0x2010 }; // leaf-first: A, A (recursive), B
        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(0.0, threadId: 1, stack),
        };

        JsonDocument document = WriteAndParse(sampleEvents, MakeSymbolTable());
        JsonElement methodNames = document.RootElement.GetProperty("methodNames");
        JsonElement hotMethods = document.RootElement.GetProperty("hotMethods");

        JsonElement methodAStats = default;
        foreach (JsonElement entry in hotMethods.EnumerateArray())
        {
            if (methodNames[entry.GetProperty("frame").GetInt32()].GetString() == "MethodA")
            {
                methodAStats = entry;
            }
        }

        Assert.Equal(1, methodAStats.GetProperty("selfSamples").GetInt32());
        Assert.Equal(1, methodAStats.GetProperty("totalSamples").GetInt32());
    }

    [Fact]
    public void Write_SamplesWithNoCapturedStackAreCountedButNotRankedAsAMethod()
    {
        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(0.0, threadId: 1, Array.Empty<long>()),
        };

        JsonDocument document = WriteAndParse(sampleEvents, MakeSymbolTable());
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("totalSampleCount").GetInt32());
        Assert.Equal(0, root.GetProperty("hotMethods").GetArrayLength());

        JsonElement flameTree = root.GetProperty("flameTree");
        JsonElement methodNames = root.GetProperty("methodNames");
        JsonElement noStackChild = flameTree.GetProperty("children")[0];
        Assert.Equal("<no stack captured>", methodNames[noStackChild.GetProperty("frame").GetInt32()].GetString());
        Assert.Equal(1, noStackChild.GetProperty("totalSamples").GetInt64());
    }

    // Regression: methodNames used to be written BEFORE hotMethodDrillDown
    // (see Write's own comment on why it's ordered after now) - since
    // WriteFlameTreeNode interns names as it writes, and a per-METHOD
    // drill-down tree's own separate node budget can include callers the
    // whole-capture flameTree's shared/global budget excluded, any name
    // interned ONLY during hotMethodDrillDown was silently missing from
    // the already-written methodNames array, leaving hotMethodDrillDown's
    // own "frame" fields pointing past the end of it. Confirmed against a
    // real 19.7M-sample production capture (not reproducible at this
    // fixture's own tiny scale - the budget-exclusion this depends on only
    // kicks in with thousands of distinct call stacks - see this test's
    // own limitation note below), where it broke the Profile view's own
    // expand-a-method-row UI for whichever hot methods happened to hit it.
    //
    // This test can't reproduce the exact BUDGET-EXCLUSION trigger at unit-
    // test scale, but it does assert the actual INVARIANT that was
    // violated - every "frame" index anywhere in the output (hotMethods,
    // flameTree, hotMethodDrillDown, all recursively) must be a valid
    // methodNames index - across every stack shape this file's other tests
    // already construct (recursive frames, multi-level callers, the
    // synthetic no-stack-captured frame), so any future change that
    // reintroduces an out-of-order write/intern dependency anywhere in
    // this file has a real chance of tripping it.
    [Fact]
    public void Write_EveryFrameReferenceAnywhereInOutputIsWithinBoundsOfMethodNames()
    {
        long[] stackA = new long[] { 0x1010, 0x2010 }; // MethodA, caller MethodB
        long[] stackRecursive = new long[] { 0x1010, 0x1020, 0x2010 }; // MethodA, MethodA, MethodB
        long[] stackDeep = new long[] { 0x1010, 0x2010, 0x3010 }; // MethodA, MethodB, MethodC

        List<SampleEvent> sampleEvents = new List<SampleEvent>
        {
            new SampleEvent(0.0, threadId: 1, stackA),
            new SampleEvent(1.0, threadId: 1, stackRecursive),
            new SampleEvent(2.0, threadId: 1, stackDeep),
            new SampleEvent(3.0, threadId: 2, Array.Empty<long>()),
        };

        JsonDocument document = WriteAndParse(sampleEvents, MakeSymbolTable());
        JsonElement root = document.RootElement;
        int methodNamesLength = root.GetProperty("methodNames").GetArrayLength();

        foreach (JsonElement hotMethod in root.GetProperty("hotMethods").EnumerateArray())
        {
            AssertFrameInBounds(hotMethod.GetProperty("frame").GetInt32(), methodNamesLength, "hotMethods");
        }

        AssertEveryFrameInTreeInBounds(root.GetProperty("flameTree"), methodNamesLength, "flameTree");

        foreach (JsonElement drillDownRoot in root.GetProperty("hotMethodDrillDown").EnumerateArray())
        {
            AssertEveryFrameInTreeInBounds(drillDownRoot, methodNamesLength, "hotMethodDrillDown");
        }
    }

    private static void AssertEveryFrameInTreeInBounds(JsonElement node, int methodNamesLength, string treeName)
    {
        AssertFrameInBounds(node.GetProperty("frame").GetInt32(), methodNamesLength, treeName);

        foreach (JsonElement child in node.GetProperty("children").EnumerateArray())
        {
            AssertEveryFrameInTreeInBounds(child, methodNamesLength, treeName);
        }
    }

    private static void AssertFrameInBounds(int frame, int methodNamesLength, string treeName)
    {
        Assert.True(frame >= 0 && frame < methodNamesLength, $"{treeName} referenced frame index {frame}, but methodNames only has {methodNamesLength} entries");
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.Tests)
