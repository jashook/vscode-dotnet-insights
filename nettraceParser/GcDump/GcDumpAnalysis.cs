////////////////////////////////////////////////////////////////////////////////
// Module: GcDumpAnalysis.cs
//
// Notes:
// The result types the four heap analyses produce, plus the shared limits
// that keep every one of them O(types) rather than O(objects) on the way out.
//
// THE CENTRAL CONSTRAINT. A heap snapshot can hold tens of millions of
// objects; a webview can render maybe a few thousand rows before it stops
// being usable, and postMessage/JSON of a per-object payload would be far
// larger than the .gcdump itself. So nothing in this file is per-object.
// Every structure here is keyed by TYPE, and the two that could still grow
// without bound (the reference graph and the root-path trie) carry explicit
// caps documented at their declarations.
//
// That is a real analytical choice, not just a rendering convenience:
// "System.String retains 400MB across 2.1M instances" is the answer someone
// investigating a heap actually wants, and it stays true and readable at any
// object count, whereas a list of 2.1M individual strings does not.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.GcDump {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public static class GcDumpAnalysisLimits
{
    // How many types the root-path and reference views are computed for,
    // ranked by retained bytes. Everything below this still appears in the
    // census - it just does not get its own path tree, because nobody
    // investigates a heap by reading the 5,000th largest type's root paths.
    public const int InterestingTypeCount = 200;

    // Per interesting type, how many instances contribute to its path trie.
    // Root paths converge hard in practice - a few thousand instances of a
    // type overwhelmingly walk the same handful of chains - so sampling past
    // this changes the ranking essentially never while costing a tree walk
    // per extra instance.
    public const int MaxInstancesPerTypeForPaths = 20000;

    // How far up toward the root a single path is followed. Real retention
    // chains that matter are short; a deeper walk mostly accumulates
    // framework plumbing that is identical across every path and therefore
    // tells the reader nothing.
    public const int MaxRootPathDepth = 24;

    // Hard ceiling on the path trie, so a pathological heap cannot turn this
    // into an unbounded structure.
    public const int MaxRootPathTrieNodes = 200000;

    // Rows kept in a single type's expandable reference list, each direction.
    public const int MaxReferenceEdgesPerType = 64;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// One row of the type census: what exists on the heap, and what it holds onto.
public sealed class TypeCensusEntry
{
    public int TypeIndex;
    public string TypeName;
    public string ModuleName;

    public long InstanceCount;

    // Bytes in the objects of this type themselves.
    public long ExclusiveBytes;

    // What this type retains: the memory that would be freed if every
    // instance of it became unreachable.
    //
    // Summed only over instances whose immediate dominator is NOT itself of
    // this type - the "outermost" instances. Naively summing retained size
    // over ALL instances is the obvious implementation and is badly wrong for
    // any self-nesting type: in a linked list the head retains the whole list,
    // the second element retains all but one, and so on, so the tail gets
    // counted once per element ahead of it. That is not a rounding error. On a
    // real 480MB heap dump of 9.9M linked nodes the naive sum reported
    // **633GB** for the node type - a number over a thousand times the size of
    // the entire heap, which is worse than useless in a column someone is
    // trying to rank by.
    //
    // Restricting to outermost instances counts each dominated byte once: the
    // list head's dominator is the object holding the list, so it counts; every
    // other element's dominator is the previous element, so it does not.
    public long RetainedBytes;

    // The single largest instance's retained size. Kept alongside the sum
    // because they answer different questions - "this type retains 400MB in
    // total" and "one instance of it retains 400MB" call for very different
    // next steps, and only the second points at a single object to go look at.
    public long MaxInstanceRetainedBytes;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// A type-to-type reference, aggregated over every object-level edge between
// instances of the two types.
public struct TypeReferenceEdge
{
    public int FromTypeIndex;
    public int ToTypeIndex;
    public long ReferenceCount;

    // Bytes of the referenced objects, summed once per edge traversed. Two
    // separate references to the same object count it twice - this measures
    // "how much does this relationship point at", which is what makes it
    // useful for ranking, and is why it is not called a retained size.
    public long ReferencedBytes;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

// One node of the aggregated paths-to-root trie. Depth 0 entries are the
// type being explained; each child steps one reference CLOSER to a GC root,
// so reading a branch from the top down answers "what is holding this".
public struct RootPathNode
{
    public int ParentIndex;
    public int TypeIndex;
    public int Depth;

    // Instances of the depth-0 type whose path passes through here.
    public long InstanceCount;

    // Their own bytes, so a branch can be ranked by how much it explains.
    public long Bytes;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class GcDumpAnalysis
{
    public List<TypeCensusEntry> Census;

    // Ranked by RetainedBytes, capped at InterestingTypeCount. The types the
    // path and reference views were computed for.
    public List<int> InterestingTypeIndices;

    public List<TypeReferenceEdge> OutgoingEdges;
    public List<TypeReferenceEdge> IncomingEdges;

    public List<RootPathNode> RootPaths;

    // Where each interesting type's depth-0 RootPaths entry lives, so the
    // renderer can jump straight to a type's tree without scanning.
    public Dictionary<int, int> RootPathIndexByType;

    public long TotalLiveBytes;
    public long TotalLiveObjects;

    // Objects the root cannot reach. Should be zero for a well-formed
    // dotnet-gcdump capture; surfaced rather than silently ignored because a
    // non-zero value means the retained-size column is incomplete and the
    // reader deserves to know that.
    public long UnreachableObjects;
    public long UnreachableBytes;

    // Per-phase wall clock, reported on the Timing: line.
    public long CensusMSec;
    public long DominatorMSec;
    public long RootPathMSec;
    public long ReferenceGraphMSec;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.GcDump)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
