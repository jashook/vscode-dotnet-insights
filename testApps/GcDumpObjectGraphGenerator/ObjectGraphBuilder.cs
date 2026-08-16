////////////////////////////////////////////////////////////////////////////////
// Module: ObjectGraphBuilder.cs
//
// Notes:
// Builds the retained object graph described in Program.cs's header.
//
// Shape, and why each part of it is there:
//
//   root -> Shard[]            fan-out, so the graph's root is not a single
//                              enormous array (which would make every
//                              dominator trivially the root)
//   Shard -> ChainNode         depth, via a linked list per shard, so the
//                              dominator tree has real height
//   ChainNode -> Payload       sharing, from a bounded pool, so many objects
//                              have several predecessors and their retained
//                              size genuinely differs from their own size
//   ChainNode -> string        a second type with per-instance sizes, so the
//                              census exercises variable-size types (the ones
//                              whose bytes are encoded explicitly per node
//                              rather than taken from the type table)
//
// Strings come from a bounded pool that is deliberately reused rather than
// freshly allocated per node: the point of this fixture is object COUNT and
// reference count, and giving every node its own string would double the
// former while adding nothing the reader is not already exercised by.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.GcDumpObjectGraphGenerator {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public struct GraphStatistics
{
    public long ObjectCount;
    public long ShardCount;
    public long NodeCount;
    public long PayloadCount;
    public long StringCount;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ChainNode
{
    public ChainNode Next;
    public Payload SharedPayload;
    public string Label;
    public int Value;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class Payload
{
    public int[] Values;
    public string Name;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class Shard
{
    public ChainNode Head;
    public string Name;
    public int Index;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class ObjectGraphBuilder
{
    // Enough shards that the root's fan-out is realistic, few enough that each
    // chain is still thousands of nodes deep.
    private const int ShardCount = 4096;

    // Shared payloads, referenced from many chains. Bounded so the sharing is
    // dense - a pool as large as the node count would give every node its own
    // payload and remove the sharing entirely.
    private const int PayloadPoolSize = 64 * 1024;

    private const int StringPoolSize = 8 * 1024;
    private const int PayloadValueCount = 8;

    private readonly int targetObjectCount;

    private Shard[] shards;
    private Payload[] payloadPool;
    private string[] stringPool;

    public ObjectGraphBuilder(int targetObjectCount)
    {
        this.targetObjectCount = targetObjectCount;
    }

    public GraphStatistics Build()
    {
        GraphStatistics statistics = new GraphStatistics();

        this.stringPool = new string[StringPoolSize];

        for (int stringIndex = 0; stringIndex < StringPoolSize; ++stringIndex)
        {
            // Varying lengths so the census sees a spread of per-instance
            // sizes rather than one uniform string size.
            this.stringPool[stringIndex] = new string('x', 8 + (stringIndex % 48));
        }

        this.payloadPool = new Payload[PayloadPoolSize];

        for (int payloadIndex = 0; payloadIndex < PayloadPoolSize; ++payloadIndex)
        {
            Payload payload = new Payload();
            payload.Values = new int[PayloadValueCount];
            payload.Name = this.stringPool[payloadIndex % StringPoolSize];
            this.payloadPool[payloadIndex] = payload;
        }

        // Payload plus its int[] is two objects each.
        statistics.PayloadCount = PayloadPoolSize;
        statistics.StringCount = StringPoolSize;

        long accountedObjects = StringPoolSize + (PayloadPoolSize * 2L) + ShardCount;
        long remainingObjects = this.targetObjectCount - accountedObjects;

        if (remainingObjects < ShardCount)
        {
            remainingObjects = ShardCount;
        }

        int nodesPerShard = (int)(remainingObjects / ShardCount);

        this.shards = new Shard[ShardCount];

        long createdNodes = 0;

        for (int shardIndex = 0; shardIndex < ShardCount; ++shardIndex)
        {
            Shard shard = new Shard();
            shard.Index = shardIndex;
            shard.Name = this.stringPool[shardIndex % StringPoolSize];

            ChainNode previousNode = null;

            for (int nodeIndex = 0; nodeIndex < nodesPerShard; ++nodeIndex)
            {
                ChainNode node = new ChainNode();
                node.Value = nodeIndex;
                node.Label = this.stringPool[(shardIndex + nodeIndex) % StringPoolSize];
                node.SharedPayload = this.payloadPool[(shardIndex * 31 + nodeIndex) % PayloadPoolSize];
                node.Next = previousNode;

                previousNode = node;
                ++createdNodes;
            }

            shard.Head = previousNode;
            this.shards[shardIndex] = shard;
        }

        statistics.ShardCount = ShardCount;
        statistics.NodeCount = createdNodes;
        statistics.ObjectCount = statistics.StringCount + (statistics.PayloadCount * 2) + statistics.ShardCount + createdNodes;

        return statistics;
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.GcDumpObjectGraphGenerator)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
