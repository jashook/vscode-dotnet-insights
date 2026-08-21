////////////////////////////////////////////////////////////////////////////////
// Module: V6ThreadTable.cs
//
// Notes:
// v6 event headers no longer carry a thread id. They carry a ThreadIndex - a
// reference into a table built by ThreadBlocks and torn down by
// RemoveThreadBlocks (NetTraceFormat.md, "Multi-process support"). This exists
// so a multi-process trace can name a thread's owning process once instead of
// repeating a pid on every event.
//
// A ThreadIndex is RECYCLABLE, exactly like a v5 StackId: it is valid only
// between the ThreadBlock that introduces it and the RemoveThreadBlock that
// removes it, and a SequencePointBlock with Flags & 1 flushes the whole table
// so later blocks may hand the same number to a completely different thread.
// This project has already been bitten by precisely this shape of bug once -
// see EventRecord.cs's comment on StackIndex, where resolving StackIds lazily
// after the whole file was parsed made every event's stack a coin flip - so
// the same rule applies here and for the same reason: V6EventBlock resolves a
// ThreadIndex to a real OS thread id EAGERLY, at the moment the event is
// parsed, against whatever this table holds at that point. Nothing downstream
// ever sees an index.
//
// processIdByThreadId accumulates across the WHOLE file rather than being
// flushed with the index table, because it answers a different question -
// "which process does this thread belong to", which does not stop being true
// when the index is recycled. It is what lets the Universal.System mapping and
// symbol tables be keyed per process without adding a ProcessId field to
// EventRecord (a struct that exists 35M+ times over on a real capture, where
// 4 more bytes is hundreds of MB). This relies on OS thread ids being unique
// across the processes in one trace, which holds on Linux, where a tid is
// allocated from the same namespace-wide space as pids. Across a pid namespace
// boundary, or after enough tid recycling, two processes could collide; the
// cost of that is a stack symbolicated against the wrong process's module
// list, not a crash or a misparse.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights.NetTrace.V6 {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System.Collections.Generic;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public sealed class V6ThreadTable
{
    public struct ThreadEntry
    {
        public long ThreadId;
        public int ProcessId;
        public string Name;
    }

    private readonly Dictionary<ulong, ThreadEntry> entriesByIndex = new Dictionary<ulong, ThreadEntry>();
    private readonly Dictionary<long, int> processIdByThreadId = new Dictionary<long, int>();
    private readonly Dictionary<long, string> nameByThreadId = new Dictionary<long, string>();

    public int LiveIndexCount => this.entriesByIndex.Count;

    public int KnownThreadCount => this.processIdByThreadId.Count;

    public void Define(ulong threadIndex, long threadId, int processId, string name)
    {
        ThreadEntry entry = new ThreadEntry();
        entry.ThreadId = threadId;
        entry.ProcessId = processId;
        entry.Name = name;

        this.entriesByIndex[threadIndex] = entry;

        // Last writer wins rather than first: a thread's pid cannot change,
        // so any disagreement here means tid recycling, and the most recent
        // definition is the one the events that follow it refer to.
        this.processIdByThreadId[threadId] = processId;

        if (name != null)
        {
            this.nameByThreadId[threadId] = name;
        }
    }

    public void Remove(ulong threadIndex)
    {
        this.entriesByIndex.Remove(threadIndex);
    }

    // A SequencePointBlock with Flags & 1 set flushes the thread cache. The
    // accumulated pid/name maps deliberately survive - see this file's header.
    public void FlushIndices()
    {
        this.entriesByIndex.Clear();
    }

    public bool TryResolve(ulong threadIndex, out ThreadEntry entry)
    {
        return this.entriesByIndex.TryGetValue(threadIndex, out entry);
    }

    public bool TryGetProcessId(long threadId, out int processId)
    {
        return this.processIdByThreadId.TryGetValue(threadId, out processId);
    }

    public bool TryGetName(long threadId, out string name)
    {
        return this.nameByThreadId.TryGetValue(threadId, out name);
    }

    public IReadOnlyDictionary<long, int> ProcessIdByThreadId => this.processIdByThreadId;
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // end of namespace(DotnetInsights.NetTrace.V6)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
